using B1Agent.Core.Insights;
using B1Agent.Core.SapB1;

namespace B1Agent.Tests;

public class InsightsTests
{
    private static readonly DateOnly Today = Demo.Today;
    private static readonly PolicyOptions Policy = new();

    private static DocumentSummary Invoice(string card, decimal open, int daysOverdue) =>
        new(1, 1, card, card + " Inc", Today.AddDays(-40), Today.AddDays(-daysOverdue), open, open, "USD", Math.Max(0, daysOverdue));

    private static BusinessPartnerDetail Customer(decimal balance, decimal limit, decimal openOrders) =>
        new("C1", "Acme Retail", "Customer", balance, limit, openOrders, null, null, null, 1);

    // ---------------------------------------------------------------- aging

    [Fact]
    public void Aging_puts_each_invoice_in_its_bucket_and_sorts_by_overdue_amount()
    {
        var report = Calculations.Aging(
        [
            Invoice("A", 100, 0), Invoice("A", 200, 15), Invoice("A", 300, 45),
            Invoice("B", 1_000, 75), Invoice("B", 50, 120)
        ], Today);

        Assert.Equal(["B", "A"], report.Customers.Select(c => c.CardCode));
        var a = report.Customers[1];
        Assert.Equal((100m, 200m, 300m, 0m, 0m), (a.NotYetDue, a.Days1To30, a.Days31To60, a.Days61To90, a.Over90));
        Assert.Equal(500m, a.TotalOverdue);
        Assert.Equal(1_650m, report.TotalOpen);
        Assert.Equal(1_550m, report.TotalOverdue);
        Assert.Equal(120, report.Customers[0].OldestDaysOverdue);
    }

    // ---------------------------------------------------------------- credit

    [Fact]
    public void Credit_approves_an_order_within_the_limit_with_nothing_overdue()
    {
        var check = Calculations.Credit(Customer(balance: 5_000, limit: 20_000, openOrders: 2_000), [], 3_000, Policy);

        Assert.Equal("Approve", check.Verdict);
        Assert.Equal(10_000m, check.ExposureAfterOrder);
        Assert.Equal(10_000m, check.HeadroomAfterOrder);
    }

    [Fact]
    public void Credit_blocks_an_order_that_would_exceed_the_limit()
    {
        var check = Calculations.Credit(Customer(balance: 15_000, limit: 20_000, openOrders: 4_000), [], 3_000, Policy);

        Assert.Equal("Block", check.Verdict);
        Assert.Contains(check.Reasons, r => r.Contains("2,000.00 over"));
    }

    [Theory]
    [InlineData(10, "Approve")]
    [InlineData(31, "Review")]
    [InlineData(61, "Block")]
    public void Credit_follows_the_overdue_policy(int daysOverdue, string expected)
    {
        var check = Calculations.Credit(Customer(1_000, 50_000, 0), [Invoice("C1", 1_000, daysOverdue)], 500, Policy);
        Assert.Equal(expected, check.Verdict);
    }

    [Fact]
    public void Credit_asks_for_review_when_no_limit_is_set()
    {
        var check = Calculations.Credit(Customer(0, 0, 0), [], 1_000, Policy);

        Assert.Equal("Review", check.Verdict);
        Assert.Null(check.HeadroomAfterOrder);
    }

    // ---------------------------------------------------------------- availability

    private static readonly ItemStock Printer = new("A1", "Printer", 30,
    [
        new WarehouseStock("01", 40, 20, 0, 20),
        new WarehouseStock("02", 10, 0, 0, 10)
    ]);

    private static readonly IncomingSupply[] Incoming =
    [
        new(50, "Acme", "01", 25, Today.AddDays(5)),
        new(51, "Acme", "02", 40, Today.AddDays(12))
    ];

    [Fact]
    public void Availability_says_yes_when_enough_is_available_now()
    {
        var result = Calculations.Availability(Printer, Incoming, 30, null, Today);

        Assert.True(result.CanShipNow);
        Assert.Equal(Today, result.FullQuantityDate);
    }

    [Fact]
    public void Availability_finds_the_date_incoming_purchase_orders_cover_the_quantity()
    {
        var result = Calculations.Availability(Printer, Incoming, 50, null, Today);

        Assert.False(result.CanShipNow);
        Assert.Equal(Today.AddDays(5), result.FullQuantityDate); // 30 now + 25 arriving on day 5
    }

    [Fact]
    public void Availability_respects_the_warehouse()
    {
        var result = Calculations.Availability(Printer, Incoming, 40, "01", Today);

        Assert.Equal(20m, result.AvailableNow);
        Assert.Equal(Today.AddDays(5), result.FullQuantityDate);
        Assert.Single(result.Incoming);
    }

    [Fact]
    public void Availability_reports_the_shortfall_when_purchase_orders_are_not_enough()
    {
        var result = Calculations.Availability(Printer, Incoming, 200, null, Today);

        Assert.Null(result.FullQuantityDate);
        Assert.Equal(105m, result.ShortfallAfterIncoming);
        Assert.Contains("A new purchase is needed", result.Summary);
    }

    // ---------------------------------------------------------------- reorder

    [Fact]
    public void Reorder_suggests_buying_up_to_the_maximum_when_projected_stock_is_below_the_minimum()
    {
        var suggestions = Calculations.Reorder(
        [
            new ItemStockLevel("A1", "Low", "01", InStock: 64, Committed: 60, Ordered: 0, MinimumStock: 40, MaximumStock: 120),
            new ItemStockLevel("A2", "Covered by PO", "01", InStock: 0, Committed: 12, Ordered: 50, MinimumStock: 20, MaximumStock: 80),
            new ItemStockLevel("A3", "No planning", "01", InStock: 0, Committed: 0, Ordered: 0, MinimumStock: 0, MaximumStock: 0),
            new ItemStockLevel("A4", "No max", "02", InStock: 5, Committed: 0, Ordered: 0, MinimumStock: 10, MaximumStock: 0)
        ]);

        Assert.Equal(["A1", "A4"], suggestions.Select(s => s.ItemCode));
        Assert.Equal(116m, suggestions[0].SuggestedQuantity);
        Assert.Equal(5m, suggestions[1].SuggestedQuantity);
    }

    // ---------------------------------------------------------------- pricing

    [Fact]
    public void Price_uses_the_customer_price_list_and_falls_back_to_the_default()
    {
        var pricing = new ItemPricing("A1", "Printer", [new PriceListPrice(1, 450, "USD"), new PriceListPrice(2, 410, "USD")]);

        Assert.Equal(410m, Calculations.Price(pricing, Customer(0, 0, 0) with { PriceList = 2 }, Policy)!.Price);
        Assert.Equal(450m, Calculations.Price(pricing, Customer(0, 0, 0) with { PriceList = 7 }, Policy)!.Price);
        Assert.Equal(450m, Calculations.Price(pricing, null, Policy)!.Price);
    }

    // ---------------------------------------------------------------- demo company, end to end

    [Fact]
    public async Task Daily_brief_on_the_demo_company_matches_its_data()
    {
        var brief = await new Demo().Insights.GetDailyBriefAsync();

        Assert.Equal(47_180m, brief.ReceivablesOverdue);
        Assert.Equal(4, brief.OverdueInvoices);
        Assert.Equal("C30000", brief.TopOverdueCustomers[0].CardCode);
        Assert.Equal(1, brief.LateOrders);
        Assert.Equal(["A00002", "C00002"], brief.TopReorders.Select(r => r.ItemCode));
        var alert = Assert.Single(brief.CustomersOverLimit);
        Assert.Equal(("C30000", 4_420m), (alert.CardCode, alert.OverBy));
    }

    [Fact]
    public async Task Availability_on_the_demo_company_uses_its_open_purchase_orders()
    {
        // A00003: 0 in stock, 12 committed, POs of 30 (day 3) and 20 (day 12).
        var result = await new Demo().Insights.CheckAvailabilityAsync("A00003", 15, null);

        Assert.False(result!.CanShipNow);
        Assert.Equal(Today.AddDays(3), result.FullQuantityDate);
    }
}
