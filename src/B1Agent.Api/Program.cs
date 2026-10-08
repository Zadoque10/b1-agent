using System.Text.Json.Serialization;
using B1Agent.Api.Llm;
using B1Agent.Core.Actions;
using B1Agent.Core.Insights;
using B1Agent.Core;
using B1Agent.Core.Agent;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddB1Agent(builder.Configuration);
builder.Services.AddLlm(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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

// Deterministic overview: no LLM involved, so it works even before a model is configured.
api.MapGet("/brief", (B1Insights insights, CancellationToken ct) => insights.GetDailyBriefAsync(ct));

// ---- Human confirmation for proposals the agent prepared. These are the only endpoints that write to SAP,
// ---- and no LLM tool can call them: a person has to click Confirm in the UI.
var actions = api.MapGroup("/actions");

actions.MapGet("/{id}", (string id, QuotationService quotations) =>
    quotations.Get(id) is { } state ? Results.Ok(state) : Results.NotFound());

actions.MapPost("/{id}/confirm", async (string id, QuotationService quotations, CancellationToken ct) =>
{
    try
    {
        var created = await quotations.ConfirmAsync(id, ct);
        return Results.Ok(new { status = "Confirmed", created.DocEntry, created.DocNum });
    }
    catch (ActionException ex)
    {
        return Results.Problem(ex.Message, statusCode: 409);
    }
    catch (ServiceLayerException ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

actions.MapPost("/{id}/cancel", (string id, QuotationService quotations) =>
{
    try
    {
        return Results.Ok(quotations.Cancel(id));
    }
    catch (ActionException ex)
    {
        return Results.Problem(ex.Message, statusCode: 409);
    }
});

app.Run();

public sealed record ChatRequest(List<ChatTurn> Messages);

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
