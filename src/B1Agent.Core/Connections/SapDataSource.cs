using B1Agent.Core.SapB1;

namespace B1Agent.Core.Connections;

/// <summary>Identifies the browser session making the request. Implemented by the host (a cookie in the web app).</summary>
public interface ISapSessionKey
{
    string? Current { get; }
}

/// <summary>A host without sessions (tests, background jobs): everyone uses the default data source.</summary>
public sealed class NoSessionKey : ISapSessionKey
{
    public string? Current => null;
}

/// <summary>
/// The SAP company this request talks to: the session's own Service Layer when it connected one,
/// otherwise the server's default (demo data or the Service Layer in configuration).
/// </summary>
public sealed record SapDataSource(
    string Id,
    string Kind,
    string Label,
    ISapB1Client Reader,
    ISapB1Writer Writer,
    bool WritesAllowed,
    ConnectionInfo? Connection);
