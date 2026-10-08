namespace B1Agent.Core.SapB1;

// These records are the agent's view of SAP B1 data: what the tools return and what the LLM sees.
// They are deliberately smaller than the Service Layer entities so that tool results stay compact
// and only carry fields the assistant is allowed to talk about.

public sealed record BusinessPartnerSummary(string CardCode, string CardName, string Type);

public sealed record BusinessPartnerDetail(
    string CardCode,
    string CardName,
    string Type,
    decimal AccountBalance,
    decimal CreditLimit,
    decimal OpenOrdersBalance,
    decimal? AvailableCredit,
    string? Phone,
    string? Email);

public sealed record ItemSummary(string ItemCode, string ItemName, decimal TotalInStock);

public sealed record WarehouseStock(
    string WarehouseCode,
    decimal InStock,
    decimal Committed,
    decimal Ordered,
    decimal Available);

public sealed record ItemStock(
    string ItemCode,
    string ItemName,
    decimal TotalAvailable,
    IReadOnlyList<WarehouseStock> Warehouses);

public sealed record DocumentSummary(
    int DocEntry,
    int DocNum,
    string CardCode,
    string CardName,
    DateOnly DocDate,
    DateOnly DueDate,
    decimal Total,
    decimal OpenBalance,
    string Currency,
    int DaysOverdue);
