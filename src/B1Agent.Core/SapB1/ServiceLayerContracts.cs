using System.Globalization;
using System.Text.Json.Serialization;

namespace B1Agent.Core.SapB1;

// Raw Service Layer payloads (only the fields we $select) and the mapping to the agent models.
// Field names follow the Service Layer metadata, so System.Text.Json binds them without attributes.

internal sealed class SlCollection<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];
}

internal sealed class SlBusinessPartner
{
    public string CardCode { get; set; } = "";
    public string CardName { get; set; } = "";
    public string? CardType { get; set; }
    public decimal? CurrentAccountBalance { get; set; }
    public decimal? CreditLimit { get; set; }
    public decimal? OpenOrdersBalance { get; set; }
    public string? Phone1 { get; set; }
    public string? EmailAddress { get; set; }
}

internal sealed class SlItem
{
    public string ItemCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public decimal? QuantityOnStock { get; set; }
    public List<SlItemWarehouse>? ItemWarehouseInfoCollection { get; set; }
}

internal sealed class SlItemWarehouse
{
    public string WarehouseCode { get; set; } = "";
    public decimal? InStock { get; set; }
    public decimal? Committed { get; set; }
    public decimal? Ordered { get; set; }
}

internal sealed class SlDocument
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string CardCode { get; set; } = "";
    public string CardName { get; set; } = "";
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public decimal? DocTotal { get; set; }
    public decimal? PaidToDate { get; set; }
    public string? DocCurrency { get; set; }
}

internal sealed class SlError
{
    [JsonPropertyName("error")]
    public SlErrorBody? Error { get; set; }
}

internal sealed class SlErrorBody
{
    [JsonPropertyName("code")]
    public object? Code { get; set; }

    [JsonPropertyName("message")]
    public SlErrorMessage? Message { get; set; }
}

internal sealed class SlErrorMessage
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal static class SlMapper
{
    public static string MapCardType(string? cardType) => cardType switch
    {
        "cCustomer" => "Customer",
        "cSupplier" => "Supplier",
        "cLid" => "Lead",
        _ => cardType ?? "Unknown"
    };

    public static BusinessPartnerSummary ToSummary(SlBusinessPartner bp) =>
        new(bp.CardCode, bp.CardName, MapCardType(bp.CardType));

    public static BusinessPartnerDetail ToDetail(SlBusinessPartner bp)
    {
        var balance = bp.CurrentAccountBalance ?? 0m;
        var limit = bp.CreditLimit ?? 0m;
        var openOrders = bp.OpenOrdersBalance ?? 0m;

        // A credit limit of 0 in B1 means "no limit configured", so available credit is unknown rather than negative.
        decimal? available = limit > 0 ? limit - balance - openOrders : null;

        return new BusinessPartnerDetail(
            bp.CardCode, bp.CardName, MapCardType(bp.CardType),
            balance, limit, openOrders, available, bp.Phone1, bp.EmailAddress);
    }

    public static ItemStock ToStock(SlItem item)
    {
        var warehouses = (item.ItemWarehouseInfoCollection ?? [])
            .Select(w =>
            {
                var inStock = w.InStock ?? 0m;
                var committed = w.Committed ?? 0m;
                return new WarehouseStock(w.WarehouseCode, inStock, committed, w.Ordered ?? 0m, inStock - committed);
            })
            .Where(w => w.InStock != 0 || w.Committed != 0 || w.Ordered != 0)
            .OrderBy(w => w.WarehouseCode, StringComparer.Ordinal)
            .ToList();

        return new ItemStock(item.ItemCode, item.ItemName, warehouses.Sum(w => w.Available), warehouses);
    }

    public static DocumentSummary ToDocument(SlDocument doc, DateOnly today)
    {
        var total = doc.DocTotal ?? 0m;
        var due = ParseDate(doc.DocDueDate);
        var overdue = due < today ? today.DayNumber - due.DayNumber : 0;

        return new DocumentSummary(
            doc.DocEntry, doc.DocNum, doc.CardCode, doc.CardName,
            ParseDate(doc.DocDate), due,
            total, total - (doc.PaidToDate ?? 0m),
            doc.DocCurrency ?? "", overdue);
    }

    // Service Layer returns dates as "yyyy-MM-dd" (v1) or "yyyy-MM-ddT00:00:00Z" depending on version.
    internal static DateOnly ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length < 10
            ? DateOnly.MinValue
            : DateOnly.ParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
