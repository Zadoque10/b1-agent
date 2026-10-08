namespace B1Agent.Core.SapB1;

/// <summary>
/// Read-only access to SAP Business One. Implemented by <see cref="ServiceLayerClient"/> (real system)
/// and <see cref="Demo.DemoSapB1Client"/> (sample data). The agent only ever depends on this interface.
/// </summary>
public interface ISapB1Client
{
    Task<IReadOnlyList<BusinessPartnerSummary>> SearchBusinessPartnersAsync(string query, int top, CancellationToken ct = default);

    Task<BusinessPartnerDetail?> GetBusinessPartnerAsync(string cardCode, CancellationToken ct = default);

    Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(string query, int top, CancellationToken ct = default);

    Task<ItemStock?> GetItemStockAsync(string itemCode, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentSummary>> GetOpenSalesOrdersAsync(string? cardCode, int top, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentSummary>> GetOpenInvoicesAsync(string? cardCode, int top, CancellationToken ct = default);
}

public sealed class ServiceLayerException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}
