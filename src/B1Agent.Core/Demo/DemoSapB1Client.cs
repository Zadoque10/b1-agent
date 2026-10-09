using System.Collections.Concurrent;
using B1Agent.Core.SapB1;

namespace B1Agent.Core.Demo;

/// <summary>
/// In-memory sample company, loosely modelled on the SAP B1 US demo database (OEC Computers).
/// Lets anyone run the agent end to end without a B1 installation. Document dates are relative to
/// "today" so there are always current, due-soon and overdue documents to ask about.
///
/// The data goes through the same <see cref="SlMapper"/> as the real client, so the LLM sees exactly
/// the same shapes in both modes. Each business partner's current balance and open-orders balance
/// are auto-derived from the invoice and order lists at construction time, so numbers stay consistent.
/// </summary>
public sealed class DemoSapB1Client : ISapB1Client, ISapB1Writer
{
    private readonly List<SlBusinessPartner> _partners;
    private readonly List<SlItem> _items;
    private readonly List<SlDocument> _orders;
    private readonly List<SlDocument> _invoices;
    private readonly List<(int DocNum, string Supplier, string ItemCode, string Whs, decimal Qty, int InDays)> _purchaseLines;
    private readonly ConcurrentDictionary<int, QuotationDraft> _quotations = new();
    private readonly TimeProvider _time;
    private int _nextQuotation = 2000;

    public DemoSapB1Client(TimeProvider time)
    {
        _time = time;
        var today = Today;

        // Credit limits and price lists are the stable facts; account balance and open-orders balance
        // are computed below from the invoice and order rows, so they can never drift.
        _partners =
        [
            Bp("C20000", "Norm Thompson",                 "cCustomer", limit: 25_000m,  priceList: 1, "503-555-0134", "ap@normthompson.example"),
            Bp("C23900", "Parameter Technology",          "cCustomer", limit: 50_000m,  priceList: 2, "781-555-0199", "billing@ptc.example"),
            Bp("C30000", "Microchips Semiconductor",      "cCustomer", limit: 30_000m,  priceList: 1, "408-555-0110", "finance@microchips.example"),
            Bp("C40000", "Earthshaker Corporation",       "cCustomer", limit: 0m,       priceList: 2, "212-555-0147", null),
            Bp("C42000", "Mashina Corporation",           "cCustomer", limit: 15_000m,  priceList: 1, "312-555-0182", "accounts@mashina.example"),
            Bp("C50000", "Boeing Industrial Procurement", "cCustomer", limit: 500_000m, priceList: 2, "206-555-0180", "ap@boeing-indproc.example"),
            Bp("C51000", "Pacific Northwest Logistics",   "cCustomer", limit: 150_000m, priceList: 1, "503-555-0145", "finance@pnwlogistics.example"),
            Bp("C52000", "Global Semiconductor Holdings", "cCustomer", limit: 200_000m, priceList: 2, "408-555-0137", "procurement@globalsemi.example"),
            Bp("C53000", "Northwind Traders",             "cCustomer", limit: 100_000m, priceList: 1, "212-555-0168", "orders@northwind.example"),
            Bp("C54000", "Atlas Technology Group",        "cCustomer", limit: 150_000m, priceList: 2, "617-555-0192", "ap@atlastech.example"),
            Bp("C55000", "Horizon Analytics",             "cCustomer", limit: 50_000m,  priceList: 1, "415-555-0133", "billing@horizonanalytics.example"),
            Bp("C56000", "Fortune Dynamics",              "cCustomer", limit: 60_000m,  priceList: 2, "713-555-0141", "ar@fortunedynamics.example"),
            Bp("C57000", "Keystone Manufacturing",        "cCustomer", limit: 150_000m, priceList: 1, "414-555-0126", "ap@keystonemfg.example"),
            Bp("C58000", "Meridian Capital Partners",     "cCustomer", limit: 25_000m,  priceList: 1, "212-555-0129", "finance@meridiancp.example"),
            Bp("L10000", "Blue Ridge Office Supply",      "cLid",      limit: 0m,       priceList: 1, "828-555-0105", "hello@blueridge.example"),
            Bp("L11000", "Prairie Software Co.",          "cLid",      limit: 0m,       priceList: 1, "515-555-0117", "contact@prairiesw.example"),
            Bp("V10000", "Acme Associates",               "cSupplier", limit: 0m,       priceList: 0, "617-555-0121", "sales@acme.example"),
            Bp("V20000", "Far East Electronics",          "cSupplier", limit: 0m,       priceList: 0, "+852-555-0177", null),
            Bp("V21000", "Zenith Components",             "cSupplier", limit: 0m,       priceList: 0, "+886-555-0184", "sales@zenith-comp.example")
        ];

        //                                   warehouse, in stock, committed, ordered, min, max         base price, wholesale
        _items =
        [
            Item("A00001", "J.B. Officeprint 1420",           (450m, 410m),    ("01", 120, 35, 40, 50, 200), ("02", 18, 0, 0, 10, 40)),
            Item("A00002", "J.B. Officeprint 1111",           (350m, 315m),    ("01", 64, 60, 0, 40, 120), ("02", 0, 0, 25, 10, 30)),
            Item("A00003", "J.B. Officeprint 1186",           (520m, 470m),    ("01", 0, 12, 50, 20, 80)),
            Item("A00004", "Rainbow Color Printer 5.0",       (780m, 700m),    ("01", 42, 10, 0, 20, 60), ("02", 30, 5, 0, 10, 40), ("03", 12, 0, 0, 0, 0)),
            Item("A00005", "Rainbow Color Printer 7.5",       (1_150m, 1_040m),("01", 9, 9, 20, 15, 40)),
            Item("C00001", "Motherboard BTX",                 (210m, 190m),    ("01", 310, 120, 0, 100, 400), ("03", 75, 0, 100, 50, 200)),
            Item("C00002", "Motherboard MicroATX",            (185m, 165m),    ("01", 145, 30, 0, 150, 300)),
            Item("C00007", "Hard Disk 3TB",                   (95m, 86m),      ("01", 520, 260, 300, 200, 800), ("02", 140, 40, 0, 50, 150)),
            Item("S10001", "PowerEdge Rack Server R760",      (18_500m, 16_800m), ("01", 24, 8, 0, 10, 50)),
            Item("S10002", "Precision Workstation 7960",      (6_450m, 5_900m),   ("01", 48, 12, 0, 20, 80)),
            Item("S10003", "Nexus 9336 Switch 36-port",       (24_200m, 22_100m), ("01", 12, 2, 0, 5, 25)),
            Item("S10004", "APC Smart-UPS 3000VA",            (2_850m, 2_560m),   ("01", 90, 15, 0, 30, 150), ("02", 20, 0, 0, 10, 40)),
            Item("S10005", "Enterprise SAN Array 48TB",       (42_500m, 38_900m), ("01", 6, 0, 0, 2, 10)),
            Item("S10006", "FortiGate 100F Firewall",         (3_980m, 3_600m),   ("01", 32, 8, 0, 10, 60)),
            Item("S10007", "HP ProLiant DL380 Gen11",         (12_750m, 11_500m), ("01", 18, 4, 0, 8, 40))
        ];

        _purchaseLines =
        [
            (77, "Acme Associates",      "A00003", "01", 30,  3),
            (77, "Acme Associates",      "A00001", "01", 40,  5),
            (79, "Acme Associates",      "A00002", "02", 25,  7),
            (78, "Far East Electronics", "A00005", "01", 20,  9),
            (78, "Far East Electronics", "A00003", "01", 20, 12),
            (79, "Acme Associates",      "C00001", "03", 100, 14),
            (78, "Far East Electronics", "C00007", "01", 300, 21),
            (80, "Zenith Components",    "S10001", "01", 10,  11),
            (80, "Zenith Components",    "S10002", "01", 15,  18),
            (81, "Far East Electronics", "S10004", "01", 40,   8)
        ];

        _orders =
        [
            // Original demo orders — keep the anchors.
            Doc(1231, 412, "C20000", "Norm Thompson",                 today.AddDays(-6),  today.AddDays(4),   4_120m,   0m),
            Doc(1232, 413, "C23900", "Parameter Technology",          today.AddDays(-3),  today.AddDays(12),  7_400m,   0m),
            Doc(1233, 414, "C23900", "Parameter Technology",          today.AddDays(-1),  today.AddDays(20),  4_500m,   0m),
            Doc(1229, 409, "C30000", "Microchips Semiconductor",      today.AddDays(-15), today.AddDays(-2),  2_640m,   0m),
            Doc(1234, 415, "C40000", "Earthshaker Corporation",       today,              today.AddDays(7),   7_350m,   0m),
            // Enterprise-scale orders.
            Doc(1240, 421, "C50000", "Boeing Industrial Procurement", today.AddDays(-4),  today.AddDays(8),   68_400m,  0m),
            Doc(1241, 422, "C50000", "Boeing Industrial Procurement", today.AddDays(-18), today.AddDays(-4),  142_000m, 0m),
            Doc(1242, 423, "C51000", "Pacific Northwest Logistics",   today.AddDays(-2),  today.AddDays(3),   12_500m,  0m),
            Doc(1243, 424, "C52000", "Global Semiconductor Holdings", today.AddDays(-7),  today.AddDays(15),  95_000m,  0m),
            Doc(1244, 425, "C52000", "Global Semiconductor Holdings", today.AddDays(-5),  today.AddDays(30),  48_500m,  0m),
            Doc(1245, 426, "C53000", "Northwind Traders",             today.AddDays(-3),  today.AddDays(10),  8_900m,   0m),
            Doc(1246, 427, "C54000", "Atlas Technology Group",        today.AddDays(-1),  today.AddDays(6),   24_000m,  0m),
            Doc(1247, 428, "C55000", "Horizon Analytics",             today,              today.AddDays(25),  3_200m,   0m),
            Doc(1248, 429, "C56000", "Fortune Dynamics",              today.AddDays(-2),  today.AddDays(11),  15_800m,  0m),
            Doc(1249, 430, "C57000", "Keystone Manufacturing",        today.AddDays(-6),  today.AddDays(14),  11_400m,  0m),
            Doc(1250, 431, "C58000", "Meridian Capital Partners",     today.AddDays(-1),  today.AddDays(18),  4_800m,   0m),
            Doc(1251, 432, "C50000", "Boeing Industrial Procurement", today.AddDays(-28), today.AddDays(-11), 185_000m, 0m)
        ];

        _invoices =
        [
            // Original anchors.
            Doc(845, 302, "C20000", "Norm Thompson",                 today.AddDays(-50),  today.AddDays(-20), 9_800m,   0m),
            Doc(851, 308, "C20000", "Norm Thompson",                 today.AddDays(-25),  today.AddDays(5),   8_650m,   0m),
            Doc(839, 296, "C30000", "Microchips Semiconductor",      today.AddDays(-95),  today.AddDays(-65), 14_200m,  2_000m),
            Doc(848, 305, "C30000", "Microchips Semiconductor",      today.AddDays(-40),  today.AddDays(-10), 19_580m,  0m),
            Doc(853, 310, "C23900", "Parameter Technology",          today.AddDays(-12),  today.AddDays(18),  2_310m,   0m),
            Doc(850, 307, "C42000", "Mashina Corporation",           today.AddDays(-33),  today.AddDays(-3),  5_600m,   0m),
            // Enterprise-scale A/R.
            Doc(870, 318, "C50000", "Boeing Industrial Procurement", today.AddDays(-10),  today.AddDays(20),  48_500m,  0m),
            Doc(871, 319, "C50000", "Boeing Industrial Procurement", today.AddDays(-75),  today.AddDays(-45), 94_150m,  0m),
            Doc(872, 320, "C54000", "Atlas Technology Group",        today.AddDays(-150), today.AddDays(-120),128_200m, 0m),
            Doc(873, 321, "C54000", "Atlas Technology Group",        today.AddDays(-38),  today.AddDays(-8),  85_600m,  0m),
            Doc(874, 322, "C56000", "Fortune Dynamics",              today.AddDays(-63),  today.AddDays(-33), 76_200m,  0m),
            Doc(875, 323, "C51000", "Pacific Northwest Logistics",   today.AddDays(-45),  today.AddDays(-15), 87_500m,  0m),
            Doc(876, 324, "C53000", "Northwind Traders",             today.AddDays(-22),  today.AddDays(8),   45_200m,  0m),
            Doc(877, 325, "C55000", "Horizon Analytics",             today.AddDays(-20),  today.AddDays(10),  18_750m,  0m),
            Doc(878, 326, "C57000", "Keystone Manufacturing",        today.AddDays(-24),  today.AddDays(6),   42_300m,  0m),
            Doc(879, 327, "C57000", "Keystone Manufacturing",        today.AddDays(-8),   today.AddDays(22),  20_000m,  0m)
        ];

        // Derive each BP's current balance from open invoices (DocTotal minus paid), and the
        // open-orders balance from the orders list. Keeps the demo self-consistent.
        foreach (var bp in _partners)
        {
            bp.CurrentAccountBalance = _invoices
                .Where(d => SameCode(d.CardCode, bp.CardCode))
                .Sum(d => d.DocTotal - d.PaidToDate);
            bp.OpenOrdersBalance = _orders
                .Where(d => SameCode(d.CardCode, bp.CardCode))
                .Sum(d => d.DocTotal - d.PaidToDate);
        }

        // Suppliers carry a negative "balance" in SAP convention (money we owe them).
        _partners.First(p => p.CardCode == "V10000").CurrentAccountBalance = -12_400m;
        _partners.First(p => p.CardCode == "V20000").CurrentAccountBalance = -8_950m;
        _partners.First(p => p.CardCode == "V21000").CurrentAccountBalance = -24_750m;
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>Quotations "created" in the demo company, for tests and the UI.</summary>
    public IReadOnlyDictionary<int, QuotationDraft> CreatedQuotations => _quotations;

    public Task<IReadOnlyList<BusinessPartnerSummary>> SearchBusinessPartnersAsync(string query, int top, CancellationToken ct = default) =>
        Done<IReadOnlyList<BusinessPartnerSummary>>(_partners
            .Where(p => Matches(p.CardName, query) || Matches(p.CardCode, query))
            .OrderBy(p => p.CardName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, top))
            .Select(SlMapper.ToSummary)
            .ToList());

    public Task<BusinessPartnerDetail?> GetBusinessPartnerAsync(string cardCode, CancellationToken ct = default)
    {
        var bp = _partners.FirstOrDefault(p => SameCode(p.CardCode, cardCode));
        return Done(bp is null ? null : SlMapper.ToDetail(bp));
    }

    public Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(string query, int top, CancellationToken ct = default) =>
        Done<IReadOnlyList<ItemSummary>>(_items
            .Where(i => Matches(i.ItemName, query) || Matches(i.ItemCode, query))
            .OrderBy(i => i.ItemCode, StringComparer.Ordinal)
            .Take(Math.Max(1, top))
            .Select(i => new ItemSummary(i.ItemCode, i.ItemName, i.QuantityOnStock ?? 0m))
            .ToList());

    public Task<ItemStock?> GetItemStockAsync(string itemCode, CancellationToken ct = default)
    {
        var item = FindItem(itemCode);
        return Done(item is null ? null : SlMapper.ToStock(item));
    }

    public Task<IReadOnlyList<DocumentSummary>> GetOpenSalesOrdersAsync(string? cardCode, int top, CancellationToken ct = default) =>
        Done(Open(_orders, cardCode, top));

    public Task<IReadOnlyList<DocumentSummary>> GetOpenInvoicesAsync(string? cardCode, int top, CancellationToken ct = default) =>
        Done(Open(_invoices, cardCode, top));

    public Task<IReadOnlyList<DocumentSummary>> GetAllOpenInvoicesAsync(string? cardCode, CancellationToken ct = default) =>
        Done(Open(_invoices, cardCode, int.MaxValue));

    public Task<IReadOnlyList<DocumentSummary>> GetLateSalesOrdersAsync(int top, CancellationToken ct = default) =>
        Done<IReadOnlyList<DocumentSummary>>(Open(_orders, null, int.MaxValue).Where(o => o.DaysOverdue > 0).Take(Math.Max(1, top)).ToList());

    public Task<IReadOnlyList<IncomingSupply>> GetIncomingSupplyAsync(string itemCode, CancellationToken ct = default)
    {
        var today = Today;
        return Done<IReadOnlyList<IncomingSupply>>(_purchaseLines
            .Where(l => SameCode(l.ItemCode, itemCode))
            .Select(l => new IncomingSupply(l.DocNum, l.Supplier, l.Whs, l.Qty, today.AddDays(l.InDays)))
            .OrderBy(s => s.ExpectedDate)
            .ToList());
    }

    public Task<IReadOnlyList<ItemStockLevel>> GetStockLevelsAsync(CancellationToken ct = default) =>
        Done<IReadOnlyList<ItemStockLevel>>(_items.SelectMany(SlMapper.ToStockLevels).ToList());

    public Task<ItemPricing?> GetItemPricingAsync(string itemCode, CancellationToken ct = default)
    {
        var item = FindItem(itemCode);
        return Done(item is null ? null : SlMapper.ToPricing(item));
    }

    public Task<IReadOnlyList<BusinessPartnerDetail>> GetCustomersWithBalanceAsync(CancellationToken ct = default) =>
        Done<IReadOnlyList<BusinessPartnerDetail>>(_partners
            .Where(p => p.CardType == "cCustomer" && p.CurrentAccountBalance > 0)
            .Select(SlMapper.ToDetail)
            .ToList());

    public Task<CreatedDocument> CreateSalesQuotationAsync(QuotationDraft draft, CancellationToken ct = default)
    {
        var docNum = Interlocked.Increment(ref _nextQuotation);
        _quotations[docNum] = draft;
        return Task.FromResult(new CreatedDocument(DocEntry: docNum + 5_000, DocNum: docNum));
    }

    // ---------------------------------------------------------------- helpers

    private static Task<T> Done<T>(T value) => Task.FromResult(value);

    private SlItem? FindItem(string itemCode) => _items.FirstOrDefault(i => SameCode(i.ItemCode, itemCode));

    private IReadOnlyList<DocumentSummary> Open(IEnumerable<SlDocument> docs, string? cardCode, int top)
    {
        var today = Today;
        return docs
            .Where(d => string.IsNullOrWhiteSpace(cardCode) || SameCode(d.CardCode, cardCode))
            .Select(d => SlMapper.ToDocument(d, today))
            .OrderBy(d => d.DueDate)
            .Take(Math.Max(1, top))
            .ToList();
    }

    private static bool SameCode(string a, string b) => a.Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string value, string query) =>
        value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SlBusinessPartner Bp(string code, string name, string type, decimal limit, int priceList, string? phone, string? email) =>
        new()
        {
            CardCode = code, CardName = name, CardType = type, CurrentAccountBalance = 0m,
            CreditLimit = limit, OpenOrdersBalance = 0m, PriceListNum = priceList, Phone1 = phone, EmailAddress = email
        };

    private static SlItem Item(string code, string name, (decimal Base, decimal Wholesale) prices,
        params (string Whs, decimal InStock, decimal Committed, decimal Ordered, decimal Min, decimal Max)[] stock) =>
        new()
        {
            ItemCode = code,
            ItemName = name,
            QuantityOnStock = stock.Sum(s => s.InStock),
            ItemPrices =
            [
                new() { PriceList = 1, Price = prices.Base, Currency = "USD" },
                new() { PriceList = 2, Price = prices.Wholesale, Currency = "USD" }
            ],
            ItemWarehouseInfoCollection = stock
                .Select(s => new SlItemWarehouse
                {
                    WarehouseCode = s.Whs, InStock = s.InStock, Committed = s.Committed, Ordered = s.Ordered,
                    MinimalStock = s.Min, MaximalStock = s.Max
                })
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
