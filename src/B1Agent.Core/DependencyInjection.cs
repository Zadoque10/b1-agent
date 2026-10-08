using System.Net;
using B1Agent.Core.Agent;
using B1Agent.Core.Demo;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B1Agent.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the SAP B1 data source (demo or Service Layer, from configuration) and the agent tools.
    /// The <c>IChatClient</c> (the LLM) and <see cref="B1ChatAgent"/> are registered by the host,
    /// so the core does not depend on any LLM vendor.
    /// </summary>
    public static IServiceCollection AddB1Agent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SapB1Options>()
            .Bind(configuration.GetSection(SapB1Options.SectionName))
            .Validate(o => o.Mode == SapB1Mode.Demo ||
                           (!string.IsNullOrWhiteSpace(o.BaseUrl) && !string.IsNullOrWhiteSpace(o.CompanyDB) &&
                            !string.IsNullOrWhiteSpace(o.UserName) && o.Password is not null),
                "SapB1:BaseUrl, CompanyDB, UserName and Password are required when SapB1:Mode is ServiceLayer.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ISapB1Client>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SapB1Options>>();
            var time = sp.GetRequiredService<TimeProvider>();

            if (options.Value.Mode == SapB1Mode.Demo)
                return new DemoSapB1Client(time);

            return new ServiceLayerClient(
                CreateServiceLayerHttpClient(options.Value),
                options, time,
                sp.GetRequiredService<ILogger<ServiceLayerClient>>());
        });

        services.AddSingleton<B1Tools>();
        return services;
    }

    // Service Layer keeps the session in cookies, so the client and its handler live for the whole app.
    // PooledConnectionLifetime still recycles connections so DNS changes are picked up.
    private static HttpClient CreateServiceLayerHttpClient(SapB1Options options)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All
        };

        if (options.AllowUntrustedCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var baseUrl = options.BaseUrl!.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
        return new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }
}
