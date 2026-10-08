using B1Agent.Api.Llm;
using B1Agent.Core;
using B1Agent.Core.Agent;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddB1Agent(builder.Configuration);
builder.Services.AddLlm(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", (IOptions<SapB1Options> sap, LlmOptions llm) => Results.Ok(new
{
    status = "ok",
    sapMode = sap.Value.Mode.ToString(),
    llmConfigured = llm.IsConfigured,
    model = llm.IsConfigured ? llm.Model : null
}));

api.MapGet("/tools", (B1Tools tools) =>
    tools.AsAITools().Select(t => new { t.Name, t.Description }));

api.MapPost("/chat", async (ChatRequest request, IServiceProvider services, CancellationToken ct) =>
{
    if (request.Messages is not { Count: > 0 } || !string.Equals(request.Messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
        return Results.Problem("Send at least one message, and the last one must have role 'user'.", statusCode: 400);

    // The agent needs an IChatClient, which is only registered once an LLM is configured.
    if (services.GetService<IChatClient>() is null)
        return Results.Problem(
            "No LLM configured. Set Llm:ApiKey (OpenAI) or Llm:Endpoint (e.g. Ollama at http://localhost:11434/v1). See README.",
            statusCode: 503);

    try
    {
        var agent = services.GetRequiredService<B1ChatAgent>();
        return Results.Ok(await agent.AskAsync(request.Messages, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

app.Run();

public sealed record ChatRequest(List<ChatTurn> Messages);

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
