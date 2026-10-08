using System.Text.Json;
using Microsoft.Extensions.AI;

namespace B1Agent.Core.Agent;

public sealed record ChatTurn(string Role, string Content);

public sealed record ToolCallInfo(string Name, string Arguments);

public sealed record AgentReply(string Reply, IReadOnlyList<ToolCallInfo> ToolCalls);

/// <summary>
/// One question in, one answer out. The conversation history lives on the client and is sent with every
/// request, so the API stays stateless and can scale horizontally.
///
/// The injected <see cref="IChatClient"/> must have function invocation enabled (see DependencyInjection):
/// it runs the tool-calling loop (model asks for a tool, we run it, the result goes back to the model)
/// until the model produces a final answer.
/// </summary>
public sealed class B1ChatAgent(IChatClient chatClient, B1Tools tools)
{
    public const int MaxHistoryTurns = 20;
    public const int MaxMessageLength = 4_000;

    private readonly IList<AITool> _tools = tools.AsAITools();

    public async Task<AgentReply> AskAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt.Build(DateTime.Today)) };
        messages.AddRange(history.TakeLast(MaxHistoryTurns).Select(ToChatMessage));

        var response = await chatClient.GetResponseAsync(messages, new ChatOptions { Tools = _tools }, ct);

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => new ToolCallInfo(c.Name, JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>())))
            .ToList();

        return new AgentReply(response.Text, calls);
    }

    // Only user and assistant turns are accepted from the caller. The system prompt is ours alone.
    private static ChatMessage ToChatMessage(ChatTurn turn)
    {
        var role = turn.Role.ToLowerInvariant() switch
        {
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            _ => throw new ArgumentException($"Unsupported role '{turn.Role}'. Use 'user' or 'assistant'.")
        };

        var content = turn.Content.Length > MaxMessageLength ? turn.Content[..MaxMessageLength] : turn.Content;
        return new ChatMessage(role, content);
    }
}
