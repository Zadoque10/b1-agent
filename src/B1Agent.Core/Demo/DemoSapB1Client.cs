using B1Agent.Core.SapB1;

namespace B1Agent.Core.Demo;

/// <summary>
/// In-memory sample company, loosely modelled on the SAP B1 US demo database (OEC Computers).
/// Lets anyone run the agent end to end without a B1 installation. Document dates are relative to
/// "today" so there are always current, due-soon and overdue documents to ask about.
///
/// The data goes through the same <see cref="SlMapper"/> as the real client, so the LLM sees exactly
/// the same shapes in both modes.
/// </summary>
public sealed class DemoSapB1Client : ISapB1Client
{
    private readonly List<SlBusinessPartner> _partners;
    private readonly List<SlItem> _items;
    private readonly List<SlDocument> _orders;
    private readonly List<SlDocument> _invoices;
    private readonly TimeProvider _time;

    public DemoSapB1Client(TimeProvider time)
    {
        _time = time;
        var today = Today;

        _partners =
        [
            Bp("C20000", "Norm Thompson", "cCustomer", balance: 18_450m, limit: 25_000m, openOrders: 4_120m, "503-555-0134", "ap@normthompson.example"),
            Bp("C23900", "Parameter Technology", "cCustomer", balance: 2_310m, limit: 50_000m, openOrders: 11_900m, "781-555-0199", "billing@ptc.example"),
            Bp("C30000", "Microchips", "cCustomer", balance: 31_780m, limit: 30_000m, openOrders: 2_640m, "408-555-0110", "finance@microchips.example"),
            Bp("C40000", "Earthshaker Corporation", "cCustomer", balance: 0m, limit: 0m, openOrders: 7_350m, "212-555-0147", null),
            Bp("C42000", "Mashina Corporation", "cCustomer", balance: 5_600m, limit: 15_000m, openOrders: 0m, "312-555-0182", "accounts@mashina.example"),
            Bp("L10000", "Blue Ridge Office Supply", "cLid", balance: 0m, limit: 0m, openOrders: 0m, "828-555-0105", "hello@blueridge.example"),
            Bp("V10000", "Acme Associates", "cSupplier", balance: -12_400m, limit: 0m, openOrders: 0m, "617-555-0121", "sales@acme.example"),
            Bp("V20000", "Far East Electronics", "cSupplier", balance: -8_950m, limit: 0m, openOrders: 0m, "+852-555-0177", null)
        ];

        _items =
        [
            Item("A00001", "J.B. Officeprint 1420", ("01", 120, 35, 40), ("02", 18, 0, 0)),
            Item("A00002", "J.B. Officeprint 1111", ("01", 64, 60, 0), ("02", 0, 0, 25)),
            Item("A00003", "J.B. Officeprint 1186", ("01", 0, 12, 50)),
            Item("A00004", "Rainbow Color Printer 5.0", ("01", 42, 10, 0), ("02", 30, 5, 0), ("03", 12, 0, 0)),
            Item("A00005", "Rainbow Color Printer 7.5", ("01", 9, 9, 20)),
            Item("C00001", "Motherboard BTX", ("01", 310, 120, 0), ("03", 75, 0, 100)),
            Item("C00002", "Motherboard MicroATX", ("01", 145, 30, 0)),
            Item("C00007", "Hard Disk 3TB", ("01", 520, 260, 300), ("02", 140, 40, 0))
        ];

        _orders =
        [
            Doc(1231, 412, "C20000", "Norm Thompson", today.AddDays(-6), today.AddDays(4), 4_120m, 0m),
            Doc(1232, 413, "C23900", "Parameter Technology", today.AddDays(-3), today.AddDays(12), 7_400m, 0m),
            Doc(1233, 414, "C23900", "Parameter Technology", today.AddDays(-1), today.AddDays(20), 4_500m, 0m),
            Doc(1229, 409, "C30000", "Microchips", today.AddDays(-15), today.AddDays(-2), 2_640m, 0m),
            Doc(1234, 415, "C40000", "Earthshaker Corporation", today, today.AddDays(7), 7_350m, 0m)
        ];

        _invoices =
        [
            Doc(845, 302, "C20000", "Norm Thompson", today.AddDays(-50), today.AddDays(-20), 9_800m, 0m),
            Doc(851, 308, "C20000", "Norm Thompson", today.AddDays(-25), today.AddDays(5), 8_650m, 0m),
            Doc(839, 296, "C30000", "Microchips", today.AddDays(-95), today.AddDays(-65), 14_200m, 2_000m),
            Doc(848, 305, "C30000", "Microchips", today.AddDays(-40), today.AddDays(-10), 19_580m, 0m),
            Doc(853, 310, "C23900", "Parameter Technology", today.AddDays(-12), today.AddDays(18), 2_310m, 0m),
            Doc(850, 307, "C42000", "Mashina Corporation", today.AddDays(-33), today.AddDays(-3), 5_600m, 0m)
        ];
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    public Task<IReadOnlyList<BusinessPartnerSummary>> SearchBusinessPartnersAsync(string query, int top, CancellationToken ct = default)
    {
        IReadOnlyList<BusinessPartnerSummary> result = _partners
            .Where(p => Matches(p.CardName, query) || Matches(p.CardCode, query))
            .OrderBy(p => p.CardName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, top))
            .Select(SlMapper.ToSummary)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<BusinessPartnerDetail?> GetBusinessPartnerAsync(string cardCode, CancellationToken ct = default)
    {
        var bp = _partners.FirstOrDefault(p => p.CardCode.Equals(cardCode.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(bp is null ? null : SlMapper.ToDetail(bp));
    }

    public Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(string query, int top, CancellationToken ct = default)
    {
        IReadOnlyList<ItemSummary> result = _items
            .Where(i => Matches(i.ItemName, query) || Matches(i.ItemCode, query))
            .OrderBy(i => i.ItemCode, StringComparer.Ordinal)
            .Take(Math.Max(1, top))
            .Select(i => new ItemSummary(i.ItemCode, i.ItemName, i.QuantityOnStock ?? 0m))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ItemStock?> GetItemStockAsync(string itemCode, CancellationToken ct = default)
    {
        var item = _items.FirstOrDefault(i => i.ItemCode.Equals(itemCode.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(item is null ? null : SlMapper.ToStock(item));
    }

    public Task<IReadOnlyList<DocumentSummary>> GetOpenSalesOrdersAsync(string? cardCode, int top, CancellationToken ct = default) =>
        Task.FromResult(Open(_orders, cardCode, top));

    public Task<IReadOnlyList<DocumentSummary>> GetOpenInvoicesAsync(string? cardCode, int top, CancellationToken ct = default) =>
        Task.FromResult(Open(_invoices, cardCode, top));

    private IReadOnlyList<DocumentSummary> Open(IEnumerable<SlDocument> docs, string? cardCode, int top)
    {
        var today = Today;
        return docs
            .Where(d => string.IsNullOrWhiteSpace(cardCode) || d.CardCode.Equals(cardCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(d => SlMapper.ToDocument(d, today))
            .OrderBy(d => d.DueDate)
            .Take(Math.Max(1, top))
            .ToList();
    }

    private static bool Matches(string value, string query) =>
        value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SlBusinessPartner Bp(string code, string name, string type, decimal balance, decimal limit, decimal openOrders, string? phone, string? email) =>
        new()
        {
            CardCode = code, CardName = name, CardType = type, CurrentAccountBalance = balance,
            CreditLimit = limit, OpenOrdersBalance = openOrders, Phone1 = phone, EmailAddress = email
        };

    private static SlItem Item(string code, string name, params (string Whs, decimal InStock, decimal Committed, decimal Ordered)[] stock) =>
        new()
        {
            ItemCode = code,
            ItemName = name,
            QuantityOnStock = stock.Sum(s => s.InStock),
            ItemWarehouseInfoCollection = stock
                .Select(s => new SlItemWarehouse { WarehouseCode = s.Whs, InStock = s.InStock, Committed = s.Committed, Ordered = s.Ordered })
                .ToList()
        };

    private static SlDocument Doc(int entry, int num, string cardCode, string cardName, DateOnly date, DateOnly due, decimal total, decimal paid) =>
        new()
        {
            DocEntry = entry, DocNum = num, CardCode = cardCode, CardName = cardName,
            DocDate = date.ToString("yyyy-MM-dd"), DocDueDate = due.ToString("yyyy-MM-dd"),
            DocTotal = total, PaidToDate = paid, DocCurrency = "USD"
        };
}
