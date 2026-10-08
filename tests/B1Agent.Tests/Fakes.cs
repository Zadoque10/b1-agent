using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace B1Agent.Tests;

/// <summary>Plays the part of Service Layer: records every request and answers from a script.</summary>
internal sealed class FakeServiceLayerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public int Logins => Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/Login", StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// Plays the part of the LLM: returns scripted responses in order and keeps every message list it was sent,
/// so tests can check what the model "saw" (including tool results).
/// </summary>
internal sealed class ScriptedChatClient(params ChatResponse[] script) : IChatClient
{
    private int _next;

    public List<List<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        Calls.Add(messages.ToList());
        return Task.FromResult(script[Math.Min(_next++, script.Length - 1)]);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    public static ChatResponse ToolCall(string name, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", name, args)]));

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));
}
