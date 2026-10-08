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

    /// <summary>
    /// Cap on rows read for calculations (aging, reorder, daily brief). These rows are summarised in code
    /// and never sent to the model. For very large companies, back these with a SQL view instead.
    /// </summary>
    public int MaxScanRows { get; set; } = 2_000;
}

/// <summary>Business rules the insights apply. Every company tunes these differently.</summary>
public sealed class PolicyOptions
{
    public const string SectionName = "Policy";

    /// <summary>Send an order to review when the customer has an invoice overdue by more than this.</summary>
    public int ReviewWhenOverdueDays { get; set; } = 30;

    /// <summary>Block an order when the customer has an invoice overdue by more than this.</summary>
    public int BlockWhenOverdueDays { get; set; } = 60;

    /// <summary>Price list used when a customer has none (B1's default is list 1).</summary>
    public int DefaultPriceList { get; set; } = 1;

    /// <summary>How long a quotation proposal waits for confirmation before it expires.</summary>
    public int ProposalLifetimeMinutes { get; set; } = 30;

    /// <summary>Default validity of a quotation, in days.</summary>
    public int QuotationValidityDays { get; set; } = 15;
}
