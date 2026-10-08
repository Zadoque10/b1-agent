namespace B1Agent.Core.SapB1;

/// <summary>
/// Read-only access to SAP Business One. Implemented by <see cref="ServiceLayerClient"/> (real system)
/// and <see cref="Demo.DemoSapB1Client"/> (sample data). The LLM tools only ever depend on this interface.
/// </summary>
public interface ISapB1Client
{
    Task<IReadOnlyList<BusinessPartnerSummary>> SearchBusinessPartnersAsync(string query, int top, CancellationToken ct = default);

    Task<BusinessPartnerDetail?> GetBusinessPartnerAsync(string cardCode, CancellationToken ct = default);

    Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(string query, int top, CancellationToken ct = default);

    Task<ItemStock?> GetItemStockAsync(string itemCode, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentSummary>> GetOpenSalesOrdersAsync(string? cardCode, int top, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentSummary>> GetOpenInvoicesAsync(string? cardCode, int top, CancellationToken ct = default);

    // ---- Used by the insight calculations. These scan more rows (up to SapB1Options.MaxScanRows) but never
    // ---- hand the raw rows to the model: the model only sees the computed summary.

    /// <summary>Every open A/R invoice (paged), for aging and collections.</summary>
    Task<IReadOnlyList<DocumentSummary>> GetAllOpenInvoicesAsync(string? cardCode, CancellationToken ct = default);

    /// <summary>Open sales orders whose due date has already passed.</summary>
    Task<IReadOnlyList<DocumentSummary>> GetLateSalesOrdersAsync(int top, CancellationToken ct = default);

    /// <summary>Open purchase order lines for an item, soonest expected date first.</summary>
    Task<IReadOnlyList<IncomingSupply>> GetIncomingSupplyAsync(string itemCode, CancellationToken ct = default);

    /// <summary>Stock and planning levels (minimum/maximum) for inventory items, per warehouse.</summary>
    Task<IReadOnlyList<ItemStockLevel>> GetStockLevelsAsync(CancellationToken ct = default);

    Task<ItemPricing?> GetItemPricingAsync(string itemCode, CancellationToken ct = default);

    /// <summary>Customers with a non-zero account balance, for credit monitoring.</summary>
    Task<IReadOnlyList<BusinessPartnerDetail>> GetCustomersWithBalanceAsync(CancellationToken ct = default);
}

/// <summary>
/// The only write path into SAP. Deliberately a separate interface: it is never given to the LLM tools,
/// only to the confirmation endpoint, so the model has no way to reach it.
/// </summary>
public interface ISapB1Writer
{
    Task<CreatedDocument> CreateSalesQuotationAsync(QuotationDraft draft, CancellationToken ct = default);
}

public sealed class ServiceLayerException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}
