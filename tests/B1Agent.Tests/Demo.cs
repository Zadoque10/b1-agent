using B1Agent.Core.Actions;
using B1Agent.Core.Agent;
using B1Agent.Core.Connections;
using B1Agent.Core.Demo;
using B1Agent.Core.Insights;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace B1Agent.Tests;

/// <summary>The demo company wired up the same way the app does it, on a fixed clock.</summary>
internal sealed class Demo
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(Now);
    public DemoSapB1Client Sap { get; }
    public PolicyOptions Policy { get; } = new();
    public B1Insights Insights { get; }
    public PendingActionStore Store { get; }
    public QuotationService Quotations { get; }
    public B1Tools Tools { get; }

    public SapDataSource Source { get; }

    public Demo(ISapB1Client? sapOverride = null, string? session = "browser-1", bool writesAllowed = true)
    {
        Sap = new DemoSapB1Client(Time);
        var sap = sapOverride ?? Sap;
        Insights = new B1Insights(sap, Options.Create(Policy), Time);
        Store = new PendingActionStore(Time);
        Source = new SapDataSource("demo", "Demo", "Demo company", sap, Sap, writesAllowed, null);
        Quotations = new QuotationService(Source, Insights, Store, new FixedSession(session), Options.Create(Policy), Time);
        Tools = new B1Tools(sap, Insights, Quotations, NullLogger<B1Tools>.Instance);
    }

    public static DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>The same company and store, seen from another browser session or another data source.</summary>
    public QuotationService As(string? session, string? sourceId = null, bool writesAllowed = true) =>
        new(Source with { Id = sourceId ?? Source.Id, WritesAllowed = writesAllowed }, Insights, Store, new FixedSession(session), Options.Create(Policy), Time);
}

internal sealed class FixedSession(string? key) : ISapSessionKey
{
    public string? Current => key;
}
