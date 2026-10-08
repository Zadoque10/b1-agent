using System.ComponentModel;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace B1Agent.Core.Agent;

/// <summary>
/// The functions the LLM is allowed to call. Each one is a thin, read-only wrapper over <see cref="ISapB1Client"/>.
///
/// Design rules:
///  - Read-only. Nothing here creates or changes documents in B1.
///  - The model never builds queries. It passes plain values; the client builds and escapes the OData itself.
///  - Row counts are capped in the client, whatever the model asks for.
///  - Failures come back as a short message the model can relay, instead of an exception that ends the turn.
/// </summary>
public sealed class B1Tools(ISapB1Client sap, ILogger<B1Tools> logger)
{
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

    /// <summary>The tools as <see cref="AITool"/>s, named in snake_case, which most models follow most reliably.</summary>
    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(SearchBusinessPartners, "search_business_partners"),
        AIFunctionFactory.Create(GetBusinessPartner, "get_business_partner"),
        AIFunctionFactory.Create(SearchItems, "search_items"),
        AIFunctionFactory.Create(GetItemStock, "get_item_stock"),
        AIFunctionFactory.Create(GetOpenSalesOrders, "get_open_sales_orders"),
        AIFunctionFactory.Create(GetOpenInvoices, "get_open_invoices")
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
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Tool {Tool} could not reach Service Layer", tool);
            return "SAP Business One is not reachable right now.";
        }
    }
}
