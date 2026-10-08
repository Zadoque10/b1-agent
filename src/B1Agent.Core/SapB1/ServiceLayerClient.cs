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
public sealed class ServiceLayerClient : ISapB1Client
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SapB1Options _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ServiceLayerClient> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

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
                  "?$select=CardCode,CardName,CardType,CurrentAccountBalance,CreditLimit,OpenOrdersBalance,Phone1,EmailAddress";

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
            select: "DocEntry,DocNum,CardCode,CardName,DocDate,DocDueDate,DocTotal,PaidToDate,DocCurrency",
            filter: filter,
            orderBy: "DocDueDate asc",
            top: Clamp(top));

        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var result = await GetAsync<SlCollection<SlDocument>>(url, ct);
        return result!.Value.Select(d => SlMapper.ToDocument(d, today)).ToList();
    }

    private int Clamp(int top) => Math.Clamp(top, 1, _options.MaxRows);

    // ---------------------------------------------------------------- HTTP + session

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct, bool notFoundIsNull = false) where T : class
    {
        var version = await EnsureSessionAsync(ct);

        using var first = await SendGetAsync(relativeUrl, ct);
        if (first.StatusCode != HttpStatusCode.Unauthorized)
            return await ReadAsync<T>(first, relativeUrl, notFoundIsNull, ct);

        _logger.LogInformation("Service Layer session expired, logging in again");
        await RenewSessionAsync(version, ct);

        using var retry = await SendGetAsync(relativeUrl, ct);
        return await ReadAsync<T>(retry, relativeUrl, notFoundIsNull, ct);
    }

    private Task<HttpResponseMessage> SendGetAsync(string relativeUrl, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        // Without this, Service Layer pages at 20 rows regardless of $top.
        request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={_options.MaxRows}");
        return _http.SendAsync(request, ct);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, string url, bool notFoundIsNull, CancellationToken ct) where T : class
    {
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
            throw await ToExceptionAsync(response, $"GET {url}", ct);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new ServiceLayerException($"Empty response from Service Layer for GET {url}");
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
            using var response = await _http.PostAsJsonAsync("Login", body, Json, ct);
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
