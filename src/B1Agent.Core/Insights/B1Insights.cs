using B1Agent.Core.SapB1;
using Microsoft.Extensions.Options;

namespace B1Agent.Core.Insights;

/// <summary>Reads what each insight needs from SAP and hands it to <see cref="Calculations"/>.</summary>
public sealed class B1Insights(ISapB1Client sap, IOptions<PolicyOptions> policy, TimeProvider time)
{
    private PolicyOptions Policy => policy.Value;

    public DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public async Task<AgingReport> GetAgingAsync(string? cardCode, CancellationToken ct = default) =>
        Calculations.Aging(await sap.GetAllOpenInvoicesAsync(cardCode, ct), Today);

    /// <returns>Null when the customer does not exist.</returns>
    public async Task<CreditCheck?> CheckCreditAsync(string cardCode, decimal orderAmount, CancellationToken ct = default)
    {
        var bp = await sap.GetBusinessPartnerAsync(cardCode, ct);
        if (bp is null) return null;

        var invoices = await sap.GetAllOpenInvoicesAsync(bp.CardCode, ct);
        return Calculations.Credit(bp, invoices, orderAmount, Policy);
    }

    /// <returns>Null when the item does not exist.</returns>
    public async Task<Availability?> CheckAvailabilityAsync(string itemCode, decimal quantity, string? warehouseCode, CancellationToken ct = default)
    {
        var stock = await sap.GetItemStockAsync(itemCode, ct);
        if (stock is null) return null;

        var incoming = await sap.GetIncomingSupplyAsync(stock.ItemCode, ct);
        return Calculations.Availability(stock, incoming, quantity, warehouseCode, Today);
    }

    public async Task<IReadOnlyList<ReorderSuggestion>> GetReorderSuggestionsAsync(string? warehouseCode, CancellationToken ct = default) =>
        Calculations.Reorder(await sap.GetStockLevelsAsync(ct), warehouseCode);

    /// <returns>Null when the item does not exist or has no price on the relevant lists.</returns>
    public async Task<CustomerPrice?> GetPriceAsync(string itemCode, string? cardCode, CancellationToken ct = default)
    {
        var pricing = await sap.GetItemPricingAsync(itemCode, ct);
        if (pricing is null) return null;

        var customer = string.IsNullOrWhiteSpace(cardCode) ? null : await sap.GetBusinessPartnerAsync(cardCode, ct);
        return Calculations.Price(pricing, customer, Policy);
    }

    /// <summary>The morning view for a finance or operations lead. Deterministic: no LLM involved.</summary>
    public async Task<DailyBrief> GetDailyBriefAsync(CancellationToken ct = default)
    {
        var invoicesTask = sap.GetAllOpenInvoicesAsync(null, ct);
        var lateTask = sap.GetLateSalesOrdersAsync(int.MaxValue, ct);
        var stockTask = sap.GetStockLevelsAsync(ct);
        var customersTask = sap.GetCustomersWithBalanceAsync(ct);
        await Task.WhenAll(invoicesTask, lateTask, stockTask, customersTask);

        var invoices = invoicesTask.Result;
        var aging = Calculations.Aging(invoices, Today);
        var late = lateTask.Result;
        var reorders = Calculations.Reorder(stockTask.Result);

        return new DailyBrief(
            Today,
            aging.TotalOpen,
            aging.TotalOverdue,
            new AgingTotals(aging.NotYetDue, aging.Days1To30, aging.Days31To60, aging.Days61To90, aging.Over90),
            invoices.Count(i => i.DaysOverdue > 0 && i.OpenBalance > 0),
            aging.Customers.Where(c => c.TotalOverdue > 0).Take(5).ToList(),
            late.Count,
            late.Sum(o => o.Total),
            late.Take(5).ToList(),
            reorders.Count,
            reorders.Take(5).ToList(),
            Calculations.OverLimit(customersTask.Result));
    }
}
