using System.Net;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace B1Agent.Tests;

public class ServiceLayerClientTests
{
    private const string LoginOk = """{"SessionId":"abc","Version":"1000190","SessionTimeout":30}""";
    private const string OnePartner = """{"value":[{"CardCode":"C20000","CardName":"Norm Thompson","CardType":"cCustomer"}]}""";

    private static (ServiceLayerClient Client, FakeServiceLayerHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeServiceLayerHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://b1:50000/b1s/v1/") };
        var options = Options.Create(new SapB1Options
        {
            Mode = SapB1Mode.ServiceLayer, BaseUrl = "https://b1:50000/b1s/v1/",
            CompanyDB = "SBODEMOUS", UserName = "manager", Password = "secret", MaxRows = 20
        });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        return (new ServiceLayerClient(http, options, time, NullLogger<ServiceLayerClient>.Instance), handler);
    }

    [Fact]
    public async Task Logs_in_once_and_reuses_the_session()
    {
        var (client, handler) = Create(r => r.Method == HttpMethod.Post
            ? FakeServiceLayerHandler.Json(LoginOk)
            : FakeServiceLayerHandler.Json(OnePartner));

        await client.SearchBusinessPartnersAsync("norm", 5);
        await client.SearchBusinessPartnersAsync("norm", 5);

        Assert.Equal(1, handler.Logins);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Logs_in_again_when_the_session_expires_and_retries_the_request()
    {
        var gets = 0;
        var (client, handler) = Create(r =>
        {
            if (r.Method == HttpMethod.Post) return FakeServiceLayerHandler.Json(LoginOk);
            return ++gets == 1
                ? FakeServiceLayerHandler.Json("""{"error":{"code":301,"message":{"value":"Invalid session."}}}""", HttpStatusCode.Unauthorized)
                : FakeServiceLayerHandler.Json(OnePartner);
        });

        var result = await client.SearchBusinessPartnersAsync("norm", 5);

        Assert.Single(result);
        Assert.Equal(2, handler.Logins);
        Assert.Equal(2, gets);
    }

    [Fact]
    public async Task Escapes_quotes_so_user_text_cannot_break_out_of_the_OData_filter()
    {
        var (client, handler) = Create(r => r.Method == HttpMethod.Post
            ? FakeServiceLayerHandler.Json(LoginOk)
            : FakeServiceLayerHandler.Json("""{"value":[]}"""));

        await client.SearchBusinessPartnersAsync("O'Brien') or (1 eq 1", 5);

        var query = Uri.UnescapeDataString(handler.Requests.Last().RequestUri!.Query);
        Assert.Contains("contains(CardName,'O''Brien'') or (1 eq 1')", query);
    }

    [Fact]
    public async Task Caps_row_count_and_asks_Service_Layer_for_a_matching_page_size()
    {
        var (client, handler) = Create(r => r.Method == HttpMethod.Post
            ? FakeServiceLayerHandler.Json(LoginOk)
            : FakeServiceLayerHandler.Json("""{"value":[]}"""));

        await client.GetOpenInvoicesAsync(null, 500);

        var request = handler.Requests.Last();
        Assert.Contains("$top=20", request.RequestUri!.Query);
        Assert.Equal("odata.maxpagesize=20", string.Join(",", request.Headers.GetValues("Prefer")));
    }

    [Fact]
    public async Task Returns_null_for_unknown_codes()
    {
        var (client, _) = Create(r => r.Method == HttpMethod.Post
            ? FakeServiceLayerHandler.Json(LoginOk)
            : FakeServiceLayerHandler.Json("""{"error":{"code":-2028,"message":{"value":"No matching records found"}}}""", HttpStatusCode.NotFound));

        Assert.Null(await client.GetBusinessPartnerAsync("NOPE"));
        Assert.Null(await client.GetItemStockAsync("NOPE"));
    }

    [Fact]
    public async Task Surfaces_the_Service_Layer_error_message()
    {
        var (client, _) = Create(r => r.Method == HttpMethod.Post
            ? FakeServiceLayerHandler.Json("""{"error":{"code":-304,"message":{"value":"Fail to get DB Credentials from SLD"}}}""", HttpStatusCode.Unauthorized)
            : FakeServiceLayerHandler.Json(OnePartner));

        var ex = await Assert.ThrowsAsync<ServiceLayerException>(() => client.SearchBusinessPartnersAsync("x", 5));
        Assert.Contains("Fail to get DB Credentials from SLD", ex.Message);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Maps_open_invoices_with_balance_and_days_overdue()
    {
        const string invoices = """
            {"value":[{"DocEntry":839,"DocNum":296,"CardCode":"C30000","CardName":"Microchips",
                       "DocDate":"2026-07-05","DocDueDate":"2026-09-28","DocTotal":14200.0,"PaidToDate":2000.0,"DocCurrency":"USD"}]}
            """;
        var (client, handler) = Create(r => r.Method == HttpMethod.Post
            ? FakeServiceLayerHandler.Json(LoginOk)
            : FakeServiceLayerHandler.Json(invoices));

        var doc = Assert.Single(await client.GetOpenInvoicesAsync("C30000", 10));

        Assert.Equal(12_200m, doc.OpenBalance);
        Assert.Equal(10, doc.DaysOverdue);
        Assert.Contains("CardCode eq 'C30000'", Uri.UnescapeDataString(handler.Requests.Last().RequestUri!.Query));
    }
}
