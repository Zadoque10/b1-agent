using System.Text.Json;
using B1Agent.Core.Actions;
using Microsoft.Extensions.AI;

namespace B1Agent.Core.Agent;

public sealed record ChatTurn(string Role, string Content);

public sealed record ToolCallInfo(string Name, string Arguments);

/// <param name="PendingActions">Proposals waiting for the user's confirmation (e.g. a sales quotation). Taken from
/// the server-side store, not from the model's text, so the UI always shows exactly what would be created.</param>
public sealed record AgentReply(string Reply, IReadOnlyList<ToolCallInfo> ToolCalls, IReadOnlyList<QuotationProposal> PendingActions);

/// <summary>
/// One question in, one answer out. The conversation history lives on the client and is sent with every
/// request, so the API stays stateless and can scale horizontally.
///
/// The injected <see cref="IChatClient"/> must have function invocation enabled (see DependencyInjection):
/// it runs the tool-calling loop (model asks for a tool, we run it, the result goes back to the model)
/// until the model produces a final answer.
/// </summary>
public sealed class B1ChatAgent(IChatClient chatClient, B1Tools tools, PendingActionStore actions)
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

        return new AgentReply(response.Text, calls, FindProposals(response.Messages));
    }

    private IReadOnlyList<QuotationProposal> FindProposals(IEnumerable<ChatMessage> messages)
    {
        var contents = messages.SelectMany(m => m.Contents).ToList();
        var quotationCalls = contents.OfType<FunctionCallContent>()
            .Where(c => c.Name == B1Tools.PrepareQuotationTool)
            .Select(c => c.CallId)
            .ToHashSet();

        return contents.OfType<FunctionResultContent>()
            .Where(r => quotationCalls.Contains(r.CallId))
            .Select(r => ReadActionId(r.Result))
            .OfType<string>()
            .Select(id => actions.Get(id))
            .Where(state => state is { Status: ActionStatus.Pending })
            .Select(state => state!.Proposal)
            .ToList();
    }

    // The tool result may come back as the original object or as JSON, depending on the pipeline.
    private static string? ReadActionId(object? result)
    {
        if (result is null) return null;
        var json = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result);
        if (json.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in json.EnumerateObject())
            if (property.Name.Equals("actionId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
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
