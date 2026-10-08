using B1Agent.Core.SapB1;

namespace B1Agent.Core.Insights;

/// <summary>
/// The business rules, as pure functions over data already read from SAP. No I/O here, so every rule
/// is unit-tested directly and the LLM never does arithmetic: it receives finished numbers.
/// </summary>
public static class Calculations
{
    // ---------------------------------------------------------------- aging

    public static AgingReport Aging(IEnumerable<DocumentSummary> openInvoices, DateOnly asOf)
    {
        var customers = openInvoices
            .Where(i => i.OpenBalance != 0)
            .GroupBy(i => (i.CardCode, i.CardName))
            .Select(g =>
            {
                decimal Bucket(Func<int, bool> inBucket) => g.Where(i => inBucket(i.DaysOverdue)).Sum(i => i.OpenBalance);
                var total = g.Sum(i => i.OpenBalance);
                var notDue = Bucket(d => d <= 0);
                return new CustomerAging(
                    g.Key.CardCode, g.Key.CardName,
                    notDue, Bucket(d => d is >= 1 and <= 30), Bucket(d => d is >= 31 and <= 60),
                    Bucket(d => d is >= 61 and <= 90), Bucket(d => d > 90),
                    total, total - notDue, g.Max(i => i.DaysOverdue), g.Count());
            })
            .OrderByDescending(c => c.TotalOverdue)
            .ThenByDescending(c => c.TotalOpen)
            .ToList();

        return new AgingReport(asOf,
            customers.Sum(c => c.TotalOpen), customers.Sum(c => c.TotalOverdue),
            customers.Sum(c => c.NotYetDue), customers.Sum(c => c.Days1To30), customers.Sum(c => c.Days31To60),
            customers.Sum(c => c.Days61To90), customers.Sum(c => c.Over90),
            customers);
    }

    // ---------------------------------------------------------------- credit

    public static CreditCheck Credit(BusinessPartnerDetail bp, IEnumerable<DocumentSummary> openInvoices, decimal orderAmount, PolicyOptions policy)
    {
        var invoices = openInvoices.ToList();
        var overdue = invoices.Where(i => i.DaysOverdue > 0).ToList();
        var overdueAmount = overdue.Sum(i => i.OpenBalance);
        var oldest = overdue.Count == 0 ? 0 : overdue.Max(i => i.DaysOverdue);

        var exposure = bp.AccountBalance + bp.OpenOrdersBalance;
        var after = exposure + orderAmount;
        decimal? headroom = bp.CreditLimit > 0 ? bp.CreditLimit - after : null;

        var verdict = CreditVerdict.Approve;
        var reasons = new List<string>();
        void Raise(CreditVerdict v, string reason) { if (v > verdict) verdict = v; reasons.Add(reason); }

        if (bp.CreditLimit <= 0)
            Raise(CreditVerdict.Review, "No credit limit is set for this customer.");
        else if (after > bp.CreditLimit)
            Raise(CreditVerdict.Block, $"The order would take exposure to {after:N2}, {after - bp.CreditLimit:N2} over the {bp.CreditLimit:N2} credit limit.");

        if (oldest > policy.BlockWhenOverdueDays)
            Raise(CreditVerdict.Block, $"An invoice is {oldest} days overdue (block above {policy.BlockWhenOverdueDays}).");
        else if (oldest > policy.ReviewWhenOverdueDays)
            Raise(CreditVerdict.Review, $"An invoice is {oldest} days overdue (review above {policy.ReviewWhenOverdueDays}).");
        else if (overdueAmount > 0)
            reasons.Add($"{overdueAmount:N2} is overdue, the oldest by {oldest} days, within policy.");

        if (verdict == CreditVerdict.Approve && headroom is not null)
            reasons.Add($"Within the credit limit, with {headroom:N2} of headroom after this order.");

        return new CreditCheck(bp.CardCode, bp.CardName, orderAmount, bp.CreditLimit, exposure, after, headroom,
            overdueAmount, oldest, verdict.ToString(), reasons);
    }

    // ---------------------------------------------------------------- availability to promise

    public static Availability Availability(ItemStock stock, IEnumerable<IncomingSupply> incoming, decimal requested, string? warehouseCode, DateOnly today)
    {
        var whs = string.IsNullOrWhiteSpace(warehouseCode) ? null : warehouseCode.Trim();
        var availableNow = whs is null
            ? stock.TotalAvailable
            : stock.Warehouses.FirstOrDefault(w => w.WarehouseCode.Equals(whs, StringComparison.OrdinalIgnoreCase))?.Available ?? 0m;

        var supply = incoming
            .Where(s => whs is null || s.WarehouseCode.Equals(whs, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.ExpectedDate)
            .ToList();

        DateOnly? fullDate = availableNow >= requested ? today : null;
        var cumulative = availableNow;
        foreach (var s in supply)
        {
            if (fullDate is not null) break;
            cumulative += s.Quantity;
            if (cumulative >= requested) fullDate = s.ExpectedDate < today ? today : s.ExpectedDate;
        }

        var shortfall = Math.Max(0m, requested - (availableNow + supply.Sum(s => s.Quantity)));
        var where = whs is null ? "across all warehouses" : $"in warehouse {whs}";
        var canShipNow = availableNow >= requested;

        var summary = canShipNow
            ? $"Yes: {availableNow:N0} available {where} now, enough for {requested:N0}."
            : fullDate is not null
                ? $"Not today: {Math.Max(0, availableNow):N0} available {where} now. The full {requested:N0} can ship from {fullDate:yyyy-MM-dd}, when incoming purchase orders arrive."
                : $"Not enough: {Math.Max(0, availableNow):N0} available {where} now and open purchase orders still leave {shortfall:N0} short. A new purchase is needed.";

        return new Availability(stock.ItemCode, stock.ItemName, whs, requested, availableNow, canShipNow, fullDate, shortfall, supply, summary);
    }

    // ---------------------------------------------------------------- replenishment

    public static IReadOnlyList<ReorderSuggestion> Reorder(IEnumerable<ItemStockLevel> levels, string? warehouseCode = null)
    {
        return levels
            .Where(l => l.MinimumStock > 0)
            .Where(l => string.IsNullOrWhiteSpace(warehouseCode) || l.WarehouseCode.Equals(warehouseCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(l =>
            {
                var projected = l.InStock - l.Committed + l.Ordered;
                var target = l.MaximumStock > l.MinimumStock ? l.MaximumStock : l.MinimumStock;
                return new ReorderSuggestion(l.ItemCode, l.ItemName, l.WarehouseCode, l.InStock, l.Committed, l.Ordered,
                    projected, l.MinimumStock, target, Math.Ceiling(target - projected));
            })
            .Where(s => s.Projected < s.MinimumStock)
            .OrderBy(s => s.Projected / s.MinimumStock)
            .ToList();
    }

    // ---------------------------------------------------------------- pricing

    public static CustomerPrice? Price(ItemPricing pricing, BusinessPartnerDetail? customer, PolicyOptions policy)
    {
        var wanted = customer is { PriceList: > 0 } ? customer.PriceList : policy.DefaultPriceList;
        var price = pricing.Prices.FirstOrDefault(p => p.PriceList == wanted);
        var note = customer is null
            ? $"Price list {wanted} (default)."
            : customer.PriceList > 0 ? $"Price list {wanted}, assigned to {customer.CardName}." : $"{customer.CardName} has no price list; using default list {wanted}.";

        if (price is null)
        {
            price = pricing.Prices.FirstOrDefault(p => p.PriceList == policy.DefaultPriceList);
            if (price is null) return null;
            note = $"No price on list {wanted}; using default list {policy.DefaultPriceList}.";
        }

        return new CustomerPrice(pricing.ItemCode, pricing.ItemName, customer?.CardCode, customer?.CardName,
            price.PriceList, price.Price, price.Currency, note + " Special prices and discount groups are not applied.");
    }

    // ---------------------------------------------------------------- credit monitoring

    public static IReadOnlyList<CreditAlert> OverLimit(IEnumerable<BusinessPartnerDetail> customers) =>
        customers
            .Where(c => c.CreditLimit > 0 && c.AccountBalance + c.OpenOrdersBalance > c.CreditLimit)
            .Select(c =>
            {
                var exposure = c.AccountBalance + c.OpenOrdersBalance;
                return new CreditAlert(c.CardCode, c.CardName, c.CreditLimit, exposure, exposure - c.CreditLimit);
            })
            .OrderByDescending(a => a.OverBy)
            .ToList();
}
