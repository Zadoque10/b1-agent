using System.Collections.Concurrent;
using System.Net;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B1Agent.Core.Connections;

public sealed class ConnectionOptions
{
    public const string SectionName = "Connections";

    /// <summary>Lets visitors connect the page to their own Service Layer.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Allow Service Layers on private networks (LAN, VPN). Keep false on a public deployment.</summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>A connection is closed (and its B1 session logged out) after this much inactivity.</summary>
    public int IdleMinutes { get; set; } = 30;

    public int MaxConnections { get; set; } = 50;
}

public sealed record ConnectRequest(
    string BaseUrl,
    string CompanyDB,
    string UserName,
    string Password,
    bool AllowUntrustedCertificate = false,
    bool AllowWrites = false);

/// <summary>What the browser may know about its connection. Never includes the user name or password.</summary>
public sealed record ConnectionInfo(string Id, string Host, string CompanyDB, bool WritesAllowed, DateTimeOffset ConnectedAt);

public sealed class ConnectionException(string message) : Exception(message);

/// <summary>
/// Customer Service Layer connections, one per browser session. Credentials live only inside the
/// <see cref="ServiceLayerClient"/> in memory; they are never persisted, logged or sent back to the browser.
/// </summary>
public sealed class ServiceLayerConnections(
    IOptions<ConnectionOptions> options,
    IOptions<SapB1Options> defaults,
    TimeProvider time,
    ILoggerFactory loggers) : IAsyncDisposable
{
    private sealed class Entry(ServiceLayerClient client, ConnectionInfo info, DateTimeOffset now)
    {
        public ServiceLayerClient Client { get; } = client;
        public ConnectionInfo Info { get; } = info;
        public DateTimeOffset LastUsed { get; set; } = now;
    }

    private readonly ConcurrentDictionary<string, Entry> _bySession = new();
    private readonly ILogger _logger = loggers.CreateLogger<ServiceLayerConnections>();

    public bool Enabled => options.Value.Enabled;

    public async Task<ConnectionInfo> ConnectAsync(string sessionKey, ConnectRequest request, CancellationToken ct = default)
    {
        if (!Enabled) throw new ConnectionException("Connecting your own Service Layer is disabled on this server.");
        await SweepAsync();
        if (_bySession.Count >= options.Value.MaxConnections && !_bySession.ContainsKey(sessionKey))
            throw new ConnectionException("This server has too many open connections right now. Try again in a few minutes.");

        var baseUri = NormaliseBaseUrl(request.BaseUrl);
        if (string.IsNullOrWhiteSpace(request.CompanyDB) || string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
            throw new ConnectionException("Company database, user name and password are required.");

        var settings = new SapB1Options
        {
            Mode = SapB1Mode.ServiceLayer,
            BaseUrl = baseUri.ToString(),
            CompanyDB = request.CompanyDB.Trim(),
            UserName = request.UserName.Trim(),
            Password = request.Password,
            AllowUntrustedCertificate = request.AllowUntrustedCertificate,
            MaxRows = defaults.Value.MaxRows,
            MaxScanRows = defaults.Value.MaxScanRows
        };

        var client = new ServiceLayerClient(CreateHttpClient(settings, options.Value.AllowPrivateNetworks), Options.Create(settings),
            time, loggers.CreateLogger<ServiceLayerClient>(), ownsHttpClient: true);
        try
        {
            await client.TestConnectionAsync(ct);
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            throw new ConnectionException(Describe(ex, baseUri));
        }

        var info = new ConnectionInfo(Guid.NewGuid().ToString("N")[..12], baseUri.Authority, settings.CompanyDB, request.AllowWrites, time.GetUtcNow());
        var previous = _bySession.TryGetValue(sessionKey, out var old) ? old : null;
        _bySession[sessionKey] = new Entry(client, info, time.GetUtcNow());
        if (previous is not null) await previous.Client.DisposeAsync();

        _logger.LogInformation("Session connected to Service Layer {Host} company {CompanyDB} (writes {Writes})",
            info.Host, info.CompanyDB, info.WritesAllowed ? "allowed" : "off");
        return info;
    }

    /// <summary>The session's connection, refreshing its idle timer. Null when the session uses the default source.</summary>
    public (ServiceLayerClient Client, ConnectionInfo Info)? Get(string? sessionKey)
    {
        if (sessionKey is null || !_bySession.TryGetValue(sessionKey, out var entry)) return null;
        if (IsIdle(entry)) return null;
        entry.LastUsed = time.GetUtcNow();
        return (entry.Client, entry.Info);
    }

    public async Task DisconnectAsync(string sessionKey)
    {
        if (_bySession.TryRemove(sessionKey, out var entry))
            await entry.Client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _bySession.Keys) await DisconnectAsync(key);
    }

    private bool IsIdle(Entry entry) => time.GetUtcNow() - entry.LastUsed > TimeSpan.FromMinutes(options.Value.IdleMinutes);

    private async Task SweepAsync()
    {
        foreach (var (key, entry) in _bySession)
            if (IsIdle(entry) && _bySession.TryRemove(key, out _))
                await entry.Client.DisposeAsync();
    }

    /// <summary>Accepts "https://host:50000", ".../b1s/v1" or ".../b1s/v2/" and returns the API root with a trailing slash.</summary>
    public static Uri NormaliseBaseUrl(string raw)
    {
        var text = (raw ?? "").Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ConnectionException("Enter the Service Layer address as https://server:50000/b1s/v1/");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ConnectionException("The Service Layer address must not contain credentials, a query or a fragment.");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path is "" or "/") path = "/b1s/v1";
        if (!path.EndsWith("/b1s/v1", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/b1s/v2", StringComparison.OrdinalIgnoreCase))
            throw new ConnectionException("The address should end with /b1s/v1 (or /b1s/v2).");

        return new UriBuilder(uri) { Path = path + "/" }.Uri;
    }

    private static HttpClient CreateHttpClient(SapB1Options settings, bool allowPrivate)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false, // a redirect could point at an internal address
            UseProxy = false,          // the guard must see the real destination, not a proxy
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (context, ct) => NetworkGuard.ConnectAsync(context.DnsEndPoint, allowPrivate, ct)
        };
        if (settings.AllowUntrustedCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        return new HttpClient(handler) { BaseAddress = new Uri(settings.BaseUrl!), Timeout = TimeSpan.FromSeconds(30) };
    }

    private static string Describe(Exception ex, Uri baseUri)
    {
        // The guard's refusal can arrive wrapped by the HTTP stack; surface it as is.
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message.Contains("private or reserved", StringComparison.Ordinal)) return e.Message;
        return DescribeTop(ex, baseUri);
    }

    private static string DescribeTop(Exception ex, Uri baseUri) => ex switch
    {
        ServiceLayerException { StatusCode: 401 } sl => "Service Layer refused the login. " + Detail(sl.Message),
        ServiceLayerException sl => Detail(sl.Message),
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            $"The TLS certificate of {baseUri.Host} is not trusted. If it is a self-signed test server, tick \"Accept self-signed certificate\".",
        HttpRequestException => $"Could not reach {baseUri.Authority}. Check the address and that it is reachable from the internet.",
        TaskCanceledException => $"{baseUri.Authority} did not answer in time.",
        _ => "Could not connect to Service Layer."
    };

    private static string Detail(string message)
    {
        var i = message.IndexOf(": ", StringComparison.Ordinal);
        return i >= 0 ? message[(i + 2)..] : message;
    }
}
