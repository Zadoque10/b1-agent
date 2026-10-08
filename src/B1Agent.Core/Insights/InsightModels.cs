using B1Agent.Core.SapB1;

namespace B1Agent.Core.Insights;

// ---------------------------------------------------------------- receivables

public sealed record CustomerAging(
    string CardCode,
    string CardName,
    decimal NotYetDue,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    decimal TotalOpen,
    decimal TotalOverdue,
    int OldestDaysOverdue,
    int OpenInvoices);

public sealed record AgingReport(
    DateOnly AsOf,
    decimal TotalOpen,
    decimal TotalOverdue,
    decimal NotYetDue,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    IReadOnlyList<CustomerAging> Customers);

// ---------------------------------------------------------------- credit

public enum CreditVerdict { Approve, Review, Block }

public sealed record CreditCheck(
    string CardCode,
    string CardName,
    decimal OrderAmount,
    decimal CreditLimit,
    decimal CurrentExposure,
    decimal ExposureAfterOrder,
    decimal? HeadroomAfterOrder,
    decimal OverdueAmount,
    int OldestDaysOverdue,
    string Verdict,
    IReadOnlyList<string> Reasons);

// ---------------------------------------------------------------- availability (ATP)

public sealed record Availability(
    string ItemCode,
    string ItemName,
    string? WarehouseCode,
    decimal Requested,
    decimal AvailableNow,
    bool CanShipNow,
    DateOnly? FullQuantityDate,
    decimal ShortfallAfterIncoming,
    IReadOnlyList<IncomingSupply> Incoming,
    string Summary);

// ---------------------------------------------------------------- replenishment

public sealed record ReorderSuggestion(
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal InStock,
    decimal Committed,
    decimal OnOrder,
    decimal Projected,
    decimal MinimumStock,
    decimal TargetStock,
    decimal SuggestedQuantity);

// ---------------------------------------------------------------- pricing

public sealed record CustomerPrice(
    string ItemCode,
    string ItemName,
    string? CardCode,
    string? CardName,
    int PriceList,
    decimal Price,
    string Currency,
    string Note);

// ---------------------------------------------------------------- daily brief

public sealed record CreditAlert(string CardCode, string CardName, decimal CreditLimit, decimal Exposure, decimal OverBy);

public sealed record AgingTotals(decimal NotYetDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90);

public sealed record DailyBrief(
    DateOnly Date,
    decimal ReceivablesOpen,
    decimal ReceivablesOverdue,
    AgingTotals ReceivablesByAge,
    int OverdueInvoices,
    IReadOnlyList<CustomerAging> TopOverdueCustomers,
    int LateOrders,
    decimal LateOrdersValue,
    IReadOnlyList<DocumentSummary> LateOrderList,
    int ItemsBelowMinimum,
    IReadOnlyList<ReorderSuggestion> TopReorders,
    IReadOnlyList<CreditAlert> CustomersOverLimit);
