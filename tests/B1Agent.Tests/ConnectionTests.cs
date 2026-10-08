using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B1Agent.Core.Connections;
using Microsoft.AspNetCore.Mvc.Testing;

namespace B1Agent.Tests;

public class ConnectionTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.18.21", false)]
    [InlineData("169.254.169.254", false)]   // cloud metadata endpoint
    [InlineData("100.70.155.122", false)]    // CGNAT / Tailscale
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("200.147.3.157", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_allowed(string address, bool expected) =>
        Assert.Equal(expected, NetworkGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://b1.example.com:50000", "https://b1.example.com:50000/b1s/v1/")]
    [InlineData("b1.example.com:50000/b1s/v1", "https://b1.example.com:50000/b1s/v1/")]
    [InlineData("https://b1.example.com:50000/b1s/v2/", "https://b1.example.com:50000/b1s/v2/")]
    public void Normalises_the_Service_Layer_address(string input, string expected) =>
        Assert.Equal(expected, ServiceLayerConnections.NormaliseBaseUrl(input).ToString());

    [Theory]
    [InlineData("http://b1.example.com:50000/b1s/v1")]
    [InlineData("https://user:pass@b1.example.com:50000/b1s/v1")]
    [InlineData("https://b1.example.com:50000/admin")]
    [InlineData("https://b1.example.com:50000/b1s/v1?x=1")]
    public void Rejects_addresses_that_are_not_a_Service_Layer_root(string input) =>
        Assert.Throws<ConnectionException>(() => ServiceLayerConnections.NormaliseBaseUrl(input));

    private static HttpClient Browser(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    [Fact]
    public async Task Connecting_to_a_private_address_is_refused_before_any_login()
    {
        await using var app = new WebApplicationFactory<Program>();
        var response = await Browser(app).PostAsJsonAsync("/api/connection", new
        {
            baseUrl = "https://127.0.0.1:50000/b1s/v1", companyDB = "SBODEMOUS", userName = "manager", password = "x"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("private or reserved", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_session_starts_on_demo_data()
    {
        await using var app = new WebApplicationFactory<Program>();
        var view = await Browser(app).GetFromJsonAsync<JsonElement>("/api/connection");

        Assert.Equal("Demo", view.GetProperty("kind").GetString());
        Assert.True(view.GetProperty("connectionsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Session_cookie_is_HttpOnly_Secure_and_SameSite_strict()
    {
        await using var app = new WebApplicationFactory<Program>();
        var response = await Browser(app).GetAsync("/api/health");

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("b1agent.sid="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
