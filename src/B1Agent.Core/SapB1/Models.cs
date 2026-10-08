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
    string? Email,
    int PriceList);

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

/// <summary>One item in one warehouse, with the planning levels set in the item master data.</summary>
public sealed record ItemStockLevel(
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal InStock,
    decimal Committed,
    decimal Ordered,
    decimal MinimumStock,
    decimal MaximumStock);

/// <summary>An open purchase order line that will bring stock in.</summary>
public sealed record IncomingSupply(
    int DocNum,
    string Supplier,
    string WarehouseCode,
    decimal Quantity,
    DateOnly ExpectedDate);

public sealed record PriceListPrice(int PriceList, decimal Price, string Currency);

public sealed record ItemPricing(string ItemCode, string ItemName, IReadOnlyList<PriceListPrice> Prices);

/// <summary>What the agent is allowed to write: a sales quotation, and only after a person confirms it.</summary>
public sealed record QuotationDraft(
    string CardCode,
    DateOnly ValidUntil,
    IReadOnlyList<QuotationDraftLine> Lines,
    string Comments);

public sealed record QuotationDraftLine(string ItemCode, decimal Quantity, decimal UnitPrice);

public sealed record CreatedDocument(int DocEntry, int DocNum);
