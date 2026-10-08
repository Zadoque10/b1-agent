using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B1Agent.Core.SapB1;

/// <summary>
/// SAP Business One Service Layer client.
///
/// Session handling: Service Layer authenticates with POST /Login and then tracks the session through the
/// B1SESSION (and, behind the load balancer, ROUTEID) cookies. The HttpClient passed in must therefore use a
/// handler with a CookieContainer, and this class must be a singleton so the session is reused across requests.
/// When a session expires (HTTP 401) the client logs in again once and retries the request.
/// </summary>
public sealed class ServiceLayerClient : ISapB1Client, ISapB1Writer
{
    // Reading is case-insensitive. Writing keeps .NET's PascalCase, because Service Layer property names
    // (CardCode, DocumentLines, CompanyDB...) are case-sensitive.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions WriteJson = new(JsonSerializerDefaults.General);

    private readonly HttpClient _http;
    private readonly SapB1Options _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ServiceLayerClient> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private const string PartnerDetailFields =
        "CardCode,CardName,CardType,CurrentAccountBalance,CreditLimit,OpenOrdersBalance,Phone1,EmailAddress,PriceListNum";
    private const string DocumentFields = "DocEntry,DocNum,CardCode,CardName,DocDate,DocDueDate,DocTotal,PaidToDate,DocCurrency";
    private const int ScanPageSize = 100;

    // Incremented on every successful login. Lets concurrent requests that all hit a 401 trigger only one re-login.
    private int _sessionVersion;

    public ServiceLayerClient(HttpClient http, IOptions<SapB1Options> options, TimeProvider time, ILogger<ServiceLayerClient> logger)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BusinessPartnerSummary>> SearchBusinessPartnersAsync(string query, int top, CancellationToken ct = default)
    {
        var term = ODataQuery.Literal(query.Trim());
        var url = ODataQuery.Build("BusinessPartners",
            select: "CardCode,CardName,CardType",
            filter: $"contains(CardName,{term}) or contains(CardCode,{term})",
            orderBy: "CardName",
            top: Clamp(top));

        var result = await GetAsync<SlCollection<SlBusinessPartner>>(url, ct);
        return result!.Value.Select(SlMapper.ToSummary).ToList();
    }

    public async Task<BusinessPartnerDetail?> GetBusinessPartnerAsync(string cardCode, CancellationToken ct = default)
    {
        var url = ODataQuery.Key("BusinessPartners", cardCode.Trim()) +
                  "?$select=" + PartnerDetailFields;

        var bp = await GetAsync<SlBusinessPartner>(url, ct, notFoundIsNull: true);
        return bp is null ? null : SlMapper.ToDetail(bp);
    }

    public async Task<IReadOnlyList<ItemSummary>> SearchItemsAsync(string query, int top, CancellationToken ct = default)
    {
        var term = ODataQuery.Literal(query.Trim());
        var url = ODataQuery.Build("Items",
            select: "ItemCode,ItemName,QuantityOnStock",
            filter: $"contains(ItemName,{term}) or contains(ItemCode,{term})",
            orderBy: "ItemCode",
            top: Clamp(top));

        var result = await GetAsync<SlCollection<SlItem>>(url, ct);
        return result!.Value.Select(i => new ItemSummary(i.ItemCode, i.ItemName, i.QuantityOnStock ?? 0m)).ToList();
    }

    public async Task<ItemStock?> GetItemStockAsync(string itemCode, CancellationToken ct = default)
    {
        var url = ODataQuery.Key("Items", itemCode.Trim()) + "?$select=ItemCode,ItemName,ItemWarehouseInfoCollection";

        var item = await GetAsync<SlItem>(url, ct, notFoundIsNull: true);
        return item is null ? null : SlMapper.ToStock(item);
    }

    public Task<IReadOnlyList<DocumentSummary>> GetOpenSalesOrdersAsync(string? cardCode, int top, CancellationToken ct = default) =>
        GetOpenDocumentsAsync("Orders", cardCode, top, ct);

    public Task<IReadOnlyList<DocumentSummary>> GetOpenInvoicesAsync(string? cardCode, int top, CancellationToken ct = default) =>
        GetOpenDocumentsAsync("Invoices", cardCode, top, ct);

    private async Task<IReadOnlyList<DocumentSummary>> GetOpenDocumentsAsync(string resource, string? cardCode, int top, CancellationToken ct)
    {
        var filter = "DocumentStatus eq 'bost_Open'";
        if (!string.IsNullOrWhiteSpace(cardCode))
            filter += " and CardCode eq " + ODataQuery.Literal(cardCode.Trim());

        var url = ODataQuery.Build(resource,
            select: DocumentFields,
            filter: filter,
            orderBy: "DocDueDate asc",
            top: Clamp(top));

        var today = Today();
        var result = await GetAsync<SlCollection<SlDocument>>(url, ct);
        return result!.Value.Select(d => SlMapper.ToDocument(d, today)).ToList();
    }


    // ---------------------------------------------------------------- scans used by the insights

    public async Task<IReadOnlyList<DocumentSummary>> GetAllOpenInvoicesAsync(string? cardCode, CancellationToken ct = default)
    {
        var filter = "DocumentStatus eq 'bost_Open'";
        if (!string.IsNullOrWhiteSpace(cardCode))
            filter += " and CardCode eq " + ODataQuery.Literal(cardCode.Trim());

        var url = ODataQuery.Build("Invoices", select: DocumentFields, filter: filter, orderBy: "DocDueDate asc");
        var today = Today();
        return (await ScanAsync<SlDocument>(url, ct)).Select(d => SlMapper.ToDocument(d, today)).ToList();
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetLateSalesOrdersAsync(int top, CancellationToken ct = default)
    {
        var today = Today();
        var url = ODataQuery.Build("Orders",
            select: DocumentFields,
            filter: $"DocumentStatus eq 'bost_Open' and DocDueDate lt {ODataQuery.Literal(today.ToString("yyyy-MM-dd"))}",
            orderBy: "DocDueDate asc",
            top: Clamp(top));

        var result = await GetAsync<SlCollection<SlDocument>>(url, ct);
        return result!.Value.Select(d => SlMapper.ToDocument(d, today)).ToList();
    }

    // Header and lines in one round trip with $crossjoin, so we read only the open lines for this item.
    public async Task<IReadOnlyList<IncomingSupply>> GetIncomingSupplyAsync(string itemCode, CancellationToken ct = default)
    {
        var expand = "PurchaseOrders($select=DocNum,CardName,DocDueDate)," +
                     "PurchaseOrders/DocumentLines($select=DocEntry,ItemCode,RemainingOpenQuantity,ShipDate,WarehouseCode)";
        var filter = "PurchaseOrders/DocEntry eq PurchaseOrders/DocumentLines/DocEntry" +
                     " and PurchaseOrders/DocumentStatus eq 'bost_Open'" +
                     " and PurchaseOrders/DocumentLines/LineStatus eq 'bost_Open'" +
                     " and PurchaseOrders/DocumentLines/ItemCode eq " + ODataQuery.Literal(itemCode.Trim());

        var url = "$crossjoin(PurchaseOrders,PurchaseOrders/DocumentLines)" +
                  "?$expand=" + Uri.EscapeDataString(expand) + "&$filter=" + Uri.EscapeDataString(filter);

        return (await ScanAsync<SlPurchaseOrderLineRow>(url, ct))
            .Select(SlMapper.ToSupply)
            .Where(s => s.Quantity > 0)
            .OrderBy(s => s.ExpectedDate)
            .ToList();
    }

    public async Task<IReadOnlyList<ItemStockLevel>> GetStockLevelsAsync(CancellationToken ct = default)
    {
        var url = ODataQuery.Build("Items",
            select: "ItemCode,ItemName,ItemWarehouseInfoCollection",
            filter: "InventoryItem eq 'tYES' and Valid eq 'tYES'",
            orderBy: "ItemCode");

        return (await ScanAsync<SlItem>(url, ct)).SelectMany(SlMapper.ToStockLevels).ToList();
    }

    public async Task<ItemPricing?> GetItemPricingAsync(string itemCode, CancellationToken ct = default)
    {
        var url = ODataQuery.Key("Items", itemCode.Trim()) + "?$select=ItemCode,ItemName,ItemPrices";
        var item = await GetAsync<SlItem>(url, ct, notFoundIsNull: true);
        return item is null ? null : SlMapper.ToPricing(item);
    }

    public async Task<IReadOnlyList<BusinessPartnerDetail>> GetCustomersWithBalanceAsync(CancellationToken ct = default)
    {
        var url = ODataQuery.Build("BusinessPartners",
            select: PartnerDetailFields,
            filter: "CardType eq 'cCustomer' and CurrentAccountBalance gt 0",
            orderBy: "CardCode");

        return (await ScanAsync<SlBusinessPartner>(url, ct)).Select(SlMapper.ToDetail).ToList();
    }

    // ---------------------------------------------------------------- the one write

    public async Task<CreatedDocument> CreateSalesQuotationAsync(QuotationDraft draft, CancellationToken ct = default)
    {
        var body = new
        {
            draft.CardCode,
            DocDueDate = draft.ValidUntil.ToString("yyyy-MM-dd"),
            draft.Comments,
            DocumentLines = draft.Lines.Select(l => new { l.ItemCode, l.Quantity, l.UnitPrice }).ToList()
        };

        var created = await SendAsync<SlCreated>(() => new HttpRequestMessage(HttpMethod.Post, "Quotations")
        {
            Content = JsonContent.Create(body, options: WriteJson)
        }, "POST Quotations", notFoundIsNull: false, ct);

        _logger.LogInformation("Created sales quotation {DocNum} for {CardCode}", created!.DocNum, draft.CardCode);
        return new CreatedDocument(created.DocEntry, created.DocNum);
    }

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private int Clamp(int top) => Math.Clamp(top, 1, _options.MaxRows);

    // ---------------------------------------------------------------- HTTP + session

    private Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct, bool notFoundIsNull = false, int? pageSize = null) where T : class =>
        SendAsync<T>(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            // Without this, Service Layer pages at 20 rows regardless of $top.
            request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={pageSize ?? _options.MaxRows}");
            return request;
        }, "GET " + relativeUrl, notFoundIsNull, ct);

    /// <summary>Follows nextLink pages until the collection ends or MaxScanRows is reached.</summary>
    private async Task<List<T>> ScanAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var rows = new List<T>();
        string? next = relativeUrl;
        while (next is not null && rows.Count < _options.MaxScanRows)
        {
            var page = await GetAsync<SlCollection<T>>(next, ct, pageSize: ScanPageSize);
            rows.AddRange(page!.Value);
            next = page.NextLink;
        }

        if (next is not null)
            _logger.LogWarning("Stopped reading {Url} at {Rows} rows (SapB1:MaxScanRows)", relativeUrl, rows.Count);

        return rows.Count > _options.MaxScanRows ? rows[.._options.MaxScanRows] : rows;
    }

    // Requests are built by a factory because an HttpRequestMessage cannot be sent twice (we may retry after re-login).
    private async Task<T?> SendAsync<T>(Func<HttpRequestMessage> createRequest, string operation, bool notFoundIsNull, CancellationToken ct) where T : class
    {
        var version = await EnsureSessionAsync(ct);

        using (var first = await _http.SendAsync(createRequest(), ct))
        {
            if (first.StatusCode != HttpStatusCode.Unauthorized)
                return await ReadAsync<T>(first, operation, notFoundIsNull, ct);
        }

        _logger.LogInformation("Service Layer session expired, logging in again");
        await RenewSessionAsync(version, ct);

        using var retry = await _http.SendAsync(createRequest(), ct);
        return await ReadAsync<T>(retry, operation, notFoundIsNull, ct);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, string operation, bool notFoundIsNull, CancellationToken ct) where T : class
    {
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
            throw await ToExceptionAsync(response, operation, ct);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new ServiceLayerException($"Empty response from Service Layer for {operation}");
    }

    private async Task<int> EnsureSessionAsync(CancellationToken ct)
    {
        var version = Volatile.Read(ref _sessionVersion);
        if (version > 0) return version;

        await RenewSessionAsync(0, ct);
        return Volatile.Read(ref _sessionVersion);
    }

    private async Task RenewSessionAsync(int expiredVersion, CancellationToken ct)
    {
        await _loginLock.WaitAsync(ct);
        try
        {
            // Another request already logged in again while we were waiting.
            if (_sessionVersion != expiredVersion) return;

            var body = new { _options.CompanyDB, _options.UserName, _options.Password };
            using var response = await _http.PostAsJsonAsync("Login", body, WriteJson, ct);
            if (!response.IsSuccessStatusCode)
                throw await ToExceptionAsync(response, "Login", ct);

            _sessionVersion++;
            _logger.LogInformation("Logged in to Service Layer company {CompanyDB}", _options.CompanyDB);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static async Task<ServiceLayerException> ToExceptionAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<SlError>(Json, ct);
            detail = error?.Error?.Message?.Value;
        }
        catch (JsonException)
        {
            // Not a Service Layer error body (proxy page, empty body...). Fall back to the status code.
        }

        var message = $"Service Layer {operation} failed with {(int)response.StatusCode} {response.ReasonPhrase}";
        if (!string.IsNullOrWhiteSpace(detail)) message += $": {detail}";
        return new ServiceLayerException(message, (int)response.StatusCode);
    }
}
