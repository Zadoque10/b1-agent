using B1Agent.Core.Agent;
using B1Agent.Core.Demo;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace B1Agent.Tests;

public class ToolsAndMappingTests
{
    private static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    private static B1Tools Tools(ISapB1Client? sap = null) => new Demo(sap).Tools;

    [Fact]
    public void Available_credit_is_unknown_when_no_credit_limit_is_set()
    {
        var detail = SlMapper.ToDetail(new SlBusinessPartner { CardCode = "C1", CardName = "X", CreditLimit = 0, CurrentAccountBalance = 500 });
        Assert.Null(detail.AvailableCredit);
    }

    [Fact]
    public void Available_credit_subtracts_balance_and_open_orders()
    {
        var detail = SlMapper.ToDetail(new SlBusinessPartner
        {
            CardCode = "C1", CardName = "X", CardType = "cCustomer",
            CreditLimit = 30_000, CurrentAccountBalance = 31_780, OpenOrdersBalance = 2_640
        });

        Assert.Equal(-4_420m, detail.AvailableCredit);
        Assert.Equal("Customer", detail.Type);
    }

    [Fact]
    public void Available_stock_is_in_stock_minus_committed_and_empty_warehouses_are_hidden()
    {
        var stock = SlMapper.ToStock(new SlItem
        {
            ItemCode = "A1", ItemName = "Printer",
            ItemWarehouseInfoCollection =
            [
                new() { WarehouseCode = "02", InStock = 18, Committed = 0, Ordered = 0 },
                new() { WarehouseCode = "01", InStock = 120, Committed = 35, Ordered = 40 },
                new() { WarehouseCode = "99", InStock = 0, Committed = 0, Ordered = 0 }
            ]
        });

        Assert.Equal(["01", "02"], stock.Warehouses.Select(w => w.WarehouseCode));
        Assert.Equal(85m, stock.Warehouses[0].Available);
        Assert.Equal(103m, stock.TotalAvailable);
    }

    [Theory]
    [InlineData("2026-09-28", 10)]
    [InlineData("2026-09-28T00:00:00Z", 10)]
    [InlineData("2026-10-08", 0)]
    [InlineData("2026-11-01", 0)]
    public void Days_overdue_is_counted_from_the_due_date(string dueDate, int expected)
    {
        var doc = SlMapper.ToDocument(new SlDocument { DocDueDate = dueDate, DocDate = "2026-09-01", DocTotal = 10 }, new DateOnly(2026, 10, 8));
        Assert.Equal(expected, doc.DaysOverdue);
    }

    [Fact]
    public async Task Overdue_filter_returns_only_invoices_past_due()
    {
        var result = await Tools().GetOpenInvoices(overdueOnly: true, maxResults: 20);

        var invoices = Assert.IsAssignableFrom<IEnumerable<DocumentSummary>>(result).ToList();
        Assert.NotEmpty(invoices);
        Assert.All(invoices, i => Assert.True(i.DaysOverdue > 0));
    }

    [Fact]
    public async Task Unknown_codes_come_back_as_a_message_for_the_model()
    {
        var result = await Tools().GetItemStock("ZZZ999");
        Assert.Equal("No item with code 'ZZZ999'.", result);
    }

    [Fact]
    public async Task Service_Layer_errors_come_back_as_a_message_instead_of_an_exception()
    {
        var result = await Tools(new FailingClient()).GetBusinessPartner("C20000");
        Assert.Equal("SAP Business One returned an error: Login failed", result);
    }

    [Fact]
    public void Exposes_tools_with_descriptions_and_none_that_writes_to_SAP()
    {
        var tools = Tools().AsAITools();
        Assert.Equal(14, tools.Count);
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.DoesNotContain(tools, t => t.Name.StartsWith("create") || t.Name.StartsWith("update") ||
                                          t.Name.StartsWith("delete") || t.Name.Contains("confirm"));
    }

    private sealed class FailingClient : ISapB1Client
    {
        private static Exception Fail() => new ServiceLayerException("Login failed", 401);
        public Task<IReadOnlyList<BusinessPartnerSummary>> SearchBusinessPartnersAsync(string q, int t, CancellationToken ct) => throw Fail();
        public Task<BusinessPartnerDetail?> GetBusinessPartnerAsync(string c, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(string q, int t, CancellationToken ct) => throw Fail();
        public Task<ItemStock?> GetItemStockAsync(string c, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<DocumentSummary>> GetOpenSalesOrdersAsync(string? c, int t, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<DocumentSummary>> GetOpenInvoicesAsync(string? c, int t, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<DocumentSummary>> GetAllOpenInvoicesAsync(string? c, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<DocumentSummary>> GetLateSalesOrdersAsync(int t, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<IncomingSupply>> GetIncomingSupplyAsync(string i, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<ItemStockLevel>> GetStockLevelsAsync(CancellationToken ct) => throw Fail();
        public Task<ItemPricing?> GetItemPricingAsync(string i, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<BusinessPartnerDetail>> GetCustomersWithBalanceAsync(CancellationToken ct) => throw Fail();
    }
}
