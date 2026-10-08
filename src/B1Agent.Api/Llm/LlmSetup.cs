using System.ClientModel;
using B1Agent.Core.Agent;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace B1Agent.Api.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>
    /// OpenAI-compatible endpoint. Leave empty for OpenAI itself. Examples:
    /// Ollama "http://localhost:11434/v1", Azure OpenAI / OpenRouter / Groq / LM Studio endpoints.
    /// </summary>
    public string? Endpoint { get; set; }

    public string Model { get; set; } = "gpt-4o-mini";

    public string? ApiKey { get; set; }

    /// <summary>Upper bound on model/tool round trips for one question.</summary>
    public int MaxToolIterations { get; set; } = 8;

    // Local servers such as Ollama don't need a key; OpenAI does.
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Endpoint);
}

public static class LlmSetup
{
    /// <summary>
    /// Registers the LLM as an <see cref="IChatClient"/> with the tool-calling loop enabled.
    /// This is the only place that knows which LLM vendor is used; swapping vendors means changing this method
    /// (or just the Endpoint/Model settings for any OpenAI-compatible provider).
    /// </summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
        services.AddSingleton(options);

        if (!options.IsConfigured)
            return services; // /api/chat answers 503 with setup instructions.

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);

        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(options.ApiKey) ? "not-needed" : options.ApiKey);
        IChatClient inner = new ChatClient(options.Model, credential, clientOptions).AsIChatClient();

        services.AddChatClient(inner)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = options.MaxToolIterations)
            .UseLogging();

        services.AddScoped<B1ChatAgent>();
        return services;
    }
}
