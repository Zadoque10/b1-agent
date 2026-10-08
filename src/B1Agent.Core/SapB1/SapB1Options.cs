namespace B1Agent.Core.SapB1;

/// <summary>Where the agent reads SAP Business One data from.</summary>
public enum SapB1Mode
{
    /// <summary>In-memory sample data. No SAP installation needed.</summary>
    Demo,

    /// <summary>A real SAP Business One Service Layer (REST/OData).</summary>
    ServiceLayer
}

public sealed class SapB1Options
{
    public const string SectionName = "SapB1";

    public SapB1Mode Mode { get; set; } = SapB1Mode.Demo;

    /// <summary>Service Layer root, e.g. https://b1server:50000/b1s/v1/</summary>
    public string? BaseUrl { get; set; }

    public string? CompanyDB { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }

    /// <summary>Service Layer usually ships with a self-signed certificate. Only enable this outside production.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>Hard cap on rows returned by any single query, regardless of what the LLM asks for.</summary>
    public int MaxRows { get; set; } = 20;
}
