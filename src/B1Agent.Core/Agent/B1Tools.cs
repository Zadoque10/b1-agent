using System.ComponentModel;
using B1Agent.Core.Actions;
using B1Agent.Core.Insights;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace B1Agent.Core.Agent;

/// <summary>
/// The functions the LLM is allowed to call: thin wrappers over <see cref="ISapB1Client"/> (lookups),
/// <see cref="B1Insights"/> (calculated answers) and <see cref="QuotationService"/> (proposals).
///
/// Design rules:
///  - Nothing here writes to SAP. The quotation tool only prepares a proposal; creating it needs a person
///    to confirm through the API, and the writer is not reachable from this class.
///  - The model never builds queries. It passes plain values; the client builds and escapes the OData itself.
///  - Row counts are capped in the client, whatever the model asks for.
///  - Failures come back as a short message the model can relay, instead of an exception that ends the turn.
/// </summary>
public sealed class B1Tools(ISapB1Client sap, B1Insights insights, QuotationService quotations, ILogger<B1Tools> logger)
{
    public const string PrepareQuotationTool = "prepare_sales_quotation";

    [Description("Search customers, suppliers and leads by part of their name or code. Use this to find the CardCode before calling other tools.")]
    public Task<object> SearchBusinessPartners(
        [Description("Part of the business partner name or code, e.g. 'micro' or 'C200'")] string query,
        [Description("Maximum number of results (1-20)")] int maxResults = 10,
        CancellationToken ct = default) =>
        Run(nameof(SearchBusinessPartners), async () =>
        {
            var found = await sap.SearchBusinessPartnersAsync(query, maxResults, ct);
            return found.Count == 0 ? (object)$"No business partner matches '{query}'." : found;
        });

    [Description("Get a business partner's account balance, credit limit, open orders balance, available credit and contact details.")]
    public Task<object> GetBusinessPartner(
        [Description("Exact CardCode, e.g. 'C20000'")] string cardCode,
        CancellationToken ct = default) =>
        Run(nameof(GetBusinessPartner), async () =>
            (object?)await sap.GetBusinessPartnerAsync(cardCode, ct) ?? $"No business partner with code '{cardCode}'.");

    [Description("Search items (products) by part of their description or code.")]
    public Task<object> SearchItems(
        [Description("Part of the item description or code, e.g. 'printer' or 'A0000'")] string query,
        [Description("Maximum number of results (1-20)")] int maxResults = 10,
        CancellationToken ct = default) =>
        Run(nameof(SearchItems), async () =>
        {
            var found = await sap.SearchItemsAsync(query, maxResults, ct);
            return found.Count == 0 ? (object)$"No item matches '{query}'." : found;
        });

    [Description("Get stock for one item per warehouse: in stock, committed to sales orders, ordered from suppliers, and available (in stock minus committed).")]
    public Task<object> GetItemStock(
        [Description("Exact ItemCode, e.g. 'A00001'")] string itemCode,
        CancellationToken ct = default) =>
        Run(nameof(GetItemStock), async () =>
            (object?)await sap.GetItemStockAsync(itemCode, ct) ?? $"No item with code '{itemCode}'.");

    [Description("List open sales orders, soonest due date first. Optionally for a single customer.")]
    public Task<object> GetOpenSalesOrders(
        [Description("CardCode to filter by. Leave empty for all customers.")] string? cardCode = null,
        [Description("Maximum number of orders (1-20)")] int maxResults = 10,
        CancellationToken ct = default) =>
        Run(nameof(GetOpenSalesOrders), async () =>
        {
            var docs = await sap.GetOpenSalesOrdersAsync(cardCode, maxResults, ct);
            return docs.Count == 0 ? (object)"No open sales orders found." : docs;
        });

    [Description("List open A/R invoices with their unpaid balance and days overdue, soonest due date first. Optionally for a single customer, or only overdue ones.")]
    public Task<object> GetOpenInvoices(
        [Description("CardCode to filter by. Leave empty for all customers.")] string? cardCode = null,
        [Description("If true, return only invoices past their due date.")] bool overdueOnly = false,
        [Description("Maximum number of invoices (1-20)")] int maxResults = 10,
        CancellationToken ct = default) =>
        Run(nameof(GetOpenInvoices), async () =>
        {
            var docs = await sap.GetOpenInvoicesAsync(cardCode, maxResults, ct);
            if (overdueOnly) docs = docs.Where(d => d.DaysOverdue > 0).ToList();
            return docs.Count == 0 ? (object)"No matching open invoices found." : docs;
        });


    // ---------------------------------------------------------------- calculated answers

    [Description("A/R aging: open receivables per customer in buckets (not yet due, 1-30, 31-60, 61-90, over 90 days overdue), largest overdue first. Use for collections questions.")]
    public Task<object> GetArAging(
        [Description("CardCode for a single customer. Leave empty for all customers.")] string? cardCode = null,
        [Description("Maximum number of customers to list (1-20)")] int maxCustomers = 10,
        CancellationToken ct = default) =>
        Run(nameof(GetArAging), async () =>
        {
            var report = await insights.GetAgingAsync(cardCode, ct);
            return report.Customers.Count == 0
                ? "There are no open receivables."
                : report with { Customers = report.Customers.Take(Math.Clamp(maxCustomers, 1, 20)).ToList() };
        });

    [Description("Credit check before accepting an order: compares balance + open orders + the new order with the credit limit, and looks at overdue invoices. Returns Approve, Review or Block with reasons.")]
    public Task<object> CheckCreditForOrder(
        [Description("Exact CardCode of the customer")] string cardCode,
        [Description("Value of the new order, in the customer's currency")] decimal orderAmount,
        CancellationToken ct = default) =>
        Run(nameof(CheckCreditForOrder), async () =>
            (object?)await insights.CheckCreditAsync(cardCode, orderAmount, ct) ?? $"No business partner with code '{cardCode}'.");

    [Description("Available to promise: can a quantity of an item ship now, and if not, from which date, using open purchase orders. Use for 'can we ship' and 'when will we have' questions.")]
    public Task<object> CheckItemAvailability(
        [Description("Exact ItemCode, e.g. 'A00001'")] string itemCode,
        [Description("Quantity the customer wants")] decimal quantity,
        [Description("Warehouse code to ship from. Leave empty to consider all warehouses.")] string? warehouseCode = null,
        CancellationToken ct = default) =>
        Run(nameof(CheckItemAvailability), async () =>
            (object?)await insights.CheckAvailabilityAsync(itemCode, quantity, warehouseCode, ct) ?? $"No item with code '{itemCode}'.");

    [Description("Open sales orders already past their due date, most overdue first, with days late.")]
    public Task<object> GetLateSalesOrders(
        [Description("Maximum number of orders (1-20)")] int maxResults = 10,
        CancellationToken ct = default) =>
        Run(nameof(GetLateSalesOrders), async () =>
        {
            var docs = await sap.GetLateSalesOrdersAsync(maxResults, ct);
            return docs.Count == 0 ? (object)"No sales orders are late." : docs;
        });

    [Description("Items whose projected stock (in stock - committed + on order) is below the minimum set in the item master, with a suggested purchase quantity up to the maximum level.")]
    public Task<object> GetReorderSuggestions(
        [Description("Warehouse code. Leave empty for all warehouses.")] string? warehouseCode = null,
        [Description("Maximum number of suggestions (1-20)")] int maxResults = 10,
        CancellationToken ct = default) =>
        Run(nameof(GetReorderSuggestions), async () =>
        {
            var list = await insights.GetReorderSuggestionsAsync(warehouseCode, ct);
            return list.Count == 0 ? (object)"No items are below their minimum stock." : list.Take(Math.Clamp(maxResults, 1, 20)).ToList();
        });

    [Description("Unit price of an item from the customer's price list (or the default list when no customer is given).")]
    public Task<object> GetItemPrice(
        [Description("Exact ItemCode")] string itemCode,
        [Description("CardCode of the customer, to use their price list. Optional.")] string? cardCode = null,
        CancellationToken ct = default) =>
        Run(nameof(GetItemPrice), async () =>
            (object?)await insights.GetPriceAsync(itemCode, cardCode, ct) ?? $"No price found for item '{itemCode}'.");

    [Description("Today's overview: overdue receivables, late sales orders, items below minimum stock and customers over their credit limit.")]
    public Task<object> GetDailyBrief(CancellationToken ct = default) =>
        Run(nameof(GetDailyBrief), async () => await insights.GetDailyBriefAsync(ct));

    // ---------------------------------------------------------------- proposal (needs human confirmation)

    [Description("Prepare a sales quotation with prices from the customer's price list, stock availability and a credit check. " +
                 "This does NOT create anything in SAP: the user sees the proposal and must click Confirm. " +
                 "Never tell the user the quotation was created.")]
    public Task<object> PrepareSalesQuotation(
        [Description("Exact CardCode of the customer or lead")] string cardCode,
        [Description("Lines of the quotation: item codes and quantities")] List<QuotationLineRequest> lines,
        CancellationToken ct = default) =>
        Run(nameof(PrepareSalesQuotation), async () =>
        {
            var proposal = await quotations.PrepareAsync(cardCode, lines, ct);
            return new
            {
                proposal.ActionId,
                status = "Awaiting the user's confirmation. Nothing has been created in SAP yet.",
                proposal = proposal
            };
        });

    /// <summary>The tools as <see cref="AITool"/>s, named in snake_case, which most models follow most reliably.</summary>
    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(SearchBusinessPartners, "search_business_partners"),
        AIFunctionFactory.Create(GetBusinessPartner, "get_business_partner"),
        AIFunctionFactory.Create(SearchItems, "search_items"),
        AIFunctionFactory.Create(GetItemStock, "get_item_stock"),
        AIFunctionFactory.Create(GetOpenSalesOrders, "get_open_sales_orders"),
        AIFunctionFactory.Create(GetOpenInvoices, "get_open_invoices"),
        AIFunctionFactory.Create(GetArAging, "get_ar_aging"),
        AIFunctionFactory.Create(CheckCreditForOrder, "check_credit_for_order"),
        AIFunctionFactory.Create(CheckItemAvailability, "check_item_availability"),
        AIFunctionFactory.Create(GetLateSalesOrders, "get_late_sales_orders"),
        AIFunctionFactory.Create(GetReorderSuggestions, "get_reorder_suggestions"),
        AIFunctionFactory.Create(GetItemPrice, "get_item_price"),
        AIFunctionFactory.Create(GetDailyBrief, "get_daily_brief"),
        AIFunctionFactory.Create(PrepareSalesQuotation, PrepareQuotationTool)
    ];

    private async Task<object> Run(string tool, Func<Task<object>> action)
    {
        try
        {
            return await action();
        }
        catch (ServiceLayerException ex)
        {
            logger.LogWarning(ex, "Tool {Tool} failed", tool);
            return $"SAP Business One returned an error: {ex.Message}";
        }
        catch (ActionException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Tool {Tool} could not reach Service Layer", tool);
            return "SAP Business One is not reachable right now.";
        }
    }
}
