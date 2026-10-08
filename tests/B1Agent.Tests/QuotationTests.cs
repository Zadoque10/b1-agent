using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B1Agent.Core.Actions;
using B1Agent.Core.Agent;
using B1Agent.Core.SapB1;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace B1Agent.Tests;

public class QuotationTests
{
    private static readonly List<QuotationLineRequest> TwoPrinters = [new("A00001", 2), new("A00005", 20)];

    [Fact]
    public async Task Prepare_prices_from_the_customer_list_and_creates_nothing_in_SAP()
    {
        var demo = new Demo();

        // Parameter Technology is on price list 2 (wholesale).
        var proposal = await demo.Quotations.PrepareAsync("C23900", TwoPrinters);

        Assert.Equal(410m, proposal.Lines[0].UnitPrice);
        Assert.Equal(2 * 410m + 20 * 1_040m, proposal.Total);
        Assert.True(proposal.Lines[0].CanShipNow);
        Assert.False(proposal.Lines[1].CanShipNow);           // only 0 available, 20 arriving
        Assert.NotNull(proposal.Lines[1].FullQuantityDate);
        Assert.Empty(demo.Sap.CreatedQuotations);
        Assert.Equal(ActionStatus.Pending, demo.Store.Get(proposal.ActionId)!.Status);
    }

    [Fact]
    public async Task Prepare_includes_the_credit_verdict_for_the_quotation_value()
    {
        var demo = new Demo();
        var proposal = await demo.Quotations.PrepareAsync("C30000", [new("A00004", 1)]);

        Assert.Equal("Block", proposal.CreditVerdict); // Microchips is already over its limit.
        Assert.NotEmpty(proposal.CreditReasons);
    }

    [Fact]
    public async Task Confirm_creates_the_quotation_exactly_once()
    {
        var demo = new Demo();
        var proposal = await demo.Quotations.PrepareAsync("C20000", [new("A00001", 3)]);

        var created = await demo.Quotations.ConfirmAsync(proposal.ActionId);

        var draft = demo.Sap.CreatedQuotations[created.DocNum];
        Assert.Equal("C20000", draft.CardCode);
        Assert.Equal(450m, draft.Lines.Single().UnitPrice);
        Assert.Contains(proposal.ActionId, draft.Comments);
        await Assert.ThrowsAsync<ActionException>(() => demo.Quotations.ConfirmAsync(proposal.ActionId));
        Assert.Single(demo.Sap.CreatedQuotations);
    }

    [Fact]
    public async Task Expired_and_cancelled_proposals_cannot_be_confirmed()
    {
        var demo = new Demo();
        var expiring = await demo.Quotations.PrepareAsync("C20000", [new("A00001", 1)]);
        var cancelled = await demo.Quotations.PrepareAsync("C20000", [new("A00001", 1)]);

        demo.Quotations.Cancel(cancelled.ActionId);
        demo.Time.Advance(TimeSpan.FromMinutes(demo.Policy.ProposalLifetimeMinutes + 1));

        Assert.Equal(ActionStatus.Expired, demo.Quotations.Get(expiring.ActionId)!.Status);
        await Assert.ThrowsAsync<ActionException>(() => demo.Quotations.ConfirmAsync(expiring.ActionId));
        await Assert.ThrowsAsync<ActionException>(() => demo.Quotations.ConfirmAsync(cancelled.ActionId));
        Assert.Empty(demo.Sap.CreatedQuotations);
    }

    [Theory]
    [InlineData("V10000", "A00001", 1, "supplier")]
    [InlineData("C20000", "NOPE", 1, "does not exist")]
    [InlineData("C20000", "A00001", 0, "greater than zero")]
    [InlineData("ZZZ", "A00001", 1, "No business partner")]
    public async Task Prepare_rejects_invalid_requests_with_a_clear_message(string card, string item, decimal qty, string expected)
    {
        var ex = await Assert.ThrowsAsync<ActionException>(() => new Demo().Quotations.PrepareAsync(card, [new(item, qty)]));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Agent_returns_the_proposal_from_the_server_store_and_the_model_cannot_confirm_it()
    {
        var demo = new Demo();
        var llm = new ScriptedChatClient(
            ScriptedChatClient.ToolCall(B1Tools.PrepareQuotationTool, new()
            {
                ["cardCode"] = "C20000",
                ["lines"] = new[] { new { itemCode = "A00001", quantity = 5 } }
            }),
            ScriptedChatClient.Text("Your quotation for Norm Thompson is ready for review: USD 2,250.00."));

        var agent = new B1ChatAgent(new ChatClientBuilder(llm).UseFunctionInvocation().Build(), demo.Tools, demo.Store);
        var reply = await agent.AskAsync([new ChatTurn("user", "Quote 5 A00001 for Norm Thompson")]);

        var proposal = Assert.Single(reply.PendingActions);
        Assert.Equal(2_250m, proposal.Total);
        Assert.Empty(demo.Sap.CreatedQuotations);
        Assert.DoesNotContain(demo.Tools.AsAITools(), t => t.Name.Contains("confirm"));
    }

    [Fact]
    public async Task Confirm_endpoint_creates_the_quotation_and_rejects_a_second_click()
    {
        await using var app = new WebApplicationFactory<Program>();
        var quotations = app.Services.GetRequiredService<QuotationService>();
        var proposal = await quotations.PrepareAsync("C42000", [new("C00007", 10)]);
        var http = app.CreateClient();

        var first = await http.PostAsync($"/api/actions/{proposal.ActionId}/confirm", null);
        var second = await http.PostAsync($"/api/actions/{proposal.ActionId}/confirm", null);

        first.EnsureSuccessStatusCode();
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("docNum").GetInt32() > 2000);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Brief_endpoint_works_without_an_LLM()
    {
        await using var app = new WebApplicationFactory<Program>();
        var brief = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/brief");

        Assert.Equal(4, brief.GetProperty("overdueInvoices").GetInt32());
    }
}
