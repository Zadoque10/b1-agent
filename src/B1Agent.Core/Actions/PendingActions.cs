using System.Collections.Concurrent;
using B1Agent.Core.Connections;
using B1Agent.Core.Insights;
using B1Agent.Core.SapB1;
using Microsoft.Extensions.Options;

namespace B1Agent.Core.Actions;

public sealed record QuotationProposalLine(
    string ItemCode,
    string ItemName,
    decimal Quantity,
    decimal UnitPrice,
    string Currency,
    decimal LineTotal,
    int PriceList,
    decimal AvailableNow,
    bool CanShipNow,
    DateOnly? FullQuantityDate);

/// <summary>
/// A sales quotation the agent has prepared but not created. It only reaches SAP when a person confirms it
/// through the API; the model has no tool that can do that.
/// </summary>
public sealed record QuotationProposal(
    string ActionId,
    string CardCode,
    string CardName,
    DateOnly ValidUntil,
    IReadOnlyList<QuotationProposalLine> Lines,
    decimal Total,
    string Currency,
    string CreditVerdict,
    IReadOnlyList<string> CreditReasons,
    DateTimeOffset ExpiresAt,
    string Target,
    bool WritesAllowed);

public enum ActionStatus { Pending, Confirmed, Cancelled, Expired }

public sealed record ActionState(QuotationProposal Proposal, ActionStatus Status, CreatedDocument? Created)
{
    /// <summary>The browser session that prepared it. Only that session can confirm or cancel it.</summary>
    internal string Owner { get; init; } = "";

    /// <summary>The SAP company it was priced against. It can only be created in that same company.</summary>
    internal string SourceId { get; init; } = "";
}

public sealed class ActionException(string message) : Exception(message);

/// <summary>
/// In-memory store of proposals waiting for confirmation. Good enough for a single instance; a deployment with
/// several instances would keep these in Redis or a database table.
/// </summary>
public sealed class PendingActionStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, ActionState> _actions = new();
    private readonly Lock _gate = new();

    public void Add(QuotationProposal proposal, string owner, string sourceId)
    {
        PurgeExpired();
        _actions[proposal.ActionId] = new ActionState(proposal, ActionStatus.Pending, null) { Owner = owner, SourceId = sourceId };
    }

    /// <returns>The proposal, or null when it does not exist or belongs to another session.</returns>
    public ActionState? Get(string actionId, string owner)
    {
        if (!_actions.TryGetValue(actionId, out var state) || state.Owner != owner) return null;
        return state.Status == ActionStatus.Pending && state.Proposal.ExpiresAt <= time.GetUtcNow()
            ? state with { Status = ActionStatus.Expired }
            : state;
    }

    /// <summary>
    /// Moves a pending proposal to "in progress" exactly once, so a double click cannot create two quotations.
    /// The caller must then call <see cref="Complete"/> or <see cref="Release"/>.
    /// </summary>
    public QuotationProposal Claim(string actionId, string owner, string sourceId)
    {
        lock (_gate)
        {
            var state = Get(actionId, owner) ?? throw new ActionException("This proposal does not exist. Ask the assistant to prepare it again.");
            if (state.SourceId != sourceId)
                throw new ActionException("This proposal was prepared against a different SAP company. Ask the assistant to prepare it again.");
            if (state.Status != ActionStatus.Pending)
                throw new ActionException($"This proposal is already {state.Status.ToString().ToLowerInvariant()}.");

            _actions[actionId] = state with { Status = ActionStatus.Confirmed };
            return state.Proposal;
        }
    }

    public void Complete(string actionId, CreatedDocument created) =>
        _actions.AddOrUpdate(actionId, _ => throw new ActionException("Unknown proposal."), (_, s) => s with { Created = created });

    /// <summary>Puts a claimed proposal back to pending, e.g. when SAP rejected the document.</summary>
    public void Release(string actionId) =>
        _actions.AddOrUpdate(actionId, _ => throw new ActionException("Unknown proposal."), (_, s) => s with { Status = ActionStatus.Pending });

    public ActionState Cancel(string actionId, string owner)
    {
        lock (_gate)
        {
            var state = Get(actionId, owner) ?? throw new ActionException("This proposal does not exist.");
            if (state.Status != ActionStatus.Pending)
                throw new ActionException($"This proposal is already {state.Status.ToString().ToLowerInvariant()}.");
            return _actions[actionId] = state with { Status = ActionStatus.Cancelled };
        }
    }

    private void PurgeExpired()
    {
        var cutoff = time.GetUtcNow().AddHours(-6);
        foreach (var (id, state) in _actions)
            if (state.Proposal.ExpiresAt < cutoff) _actions.TryRemove(id, out _);
    }
}

public sealed record QuotationLineRequest(string ItemCode, decimal Quantity);

/// <summary>Builds quotation proposals (read-only) and, on human confirmation, creates them in SAP.</summary>
public sealed class QuotationService(
    SapDataSource source,
    B1Insights insights,
    PendingActionStore store,
    ISapSessionKey session,
    IOptions<PolicyOptions> policy,
    TimeProvider time)
{
    public const int MaxLines = 20;

    private string Owner => session.Current ?? "default";

    public async Task<QuotationProposal> PrepareAsync(string cardCode, IReadOnlyList<QuotationLineRequest> requested, CancellationToken ct = default)
    {
        if (requested.Count == 0) throw new ActionException("A quotation needs at least one item.");
        if (requested.Count > MaxLines) throw new ActionException($"A quotation can have at most {MaxLines} lines here.");
        if (requested.Any(l => l.Quantity <= 0)) throw new ActionException("Quantities must be greater than zero.");

        var bp = await source.Reader.GetBusinessPartnerAsync(cardCode, ct)
                 ?? throw new ActionException($"No business partner with code '{cardCode}'.");
        if (bp.Type == "Supplier")
            throw new ActionException($"{bp.CardName} is a supplier. Sales quotations are for customers and leads.");

        var lines = new List<QuotationProposalLine>();
        foreach (var r in requested)
        {
            var price = await insights.GetPriceAsync(r.ItemCode, bp.CardCode, ct)
                        ?? throw new ActionException($"Item '{r.ItemCode}' does not exist or has no price.");
            var availability = await insights.CheckAvailabilityAsync(price.ItemCode, r.Quantity, null, ct);

            lines.Add(new QuotationProposalLine(
                price.ItemCode, price.ItemName, r.Quantity, price.Price, price.Currency,
                Math.Round(price.Price * r.Quantity, 2), price.PriceList,
                availability?.AvailableNow ?? 0, availability?.CanShipNow ?? false, availability?.FullQuantityDate));
        }

        var total = lines.Sum(l => l.LineTotal);
        var credit = await insights.CheckCreditAsync(bp.CardCode, total, ct);

        var proposal = new QuotationProposal(
            ActionId: Guid.NewGuid().ToString("N")[..12],
            bp.CardCode, bp.CardName,
            insights.Today.AddDays(policy.Value.QuotationValidityDays),
            lines, total, lines[0].Currency,
            credit?.Verdict ?? "Unknown", credit?.Reasons ?? [],
            time.GetUtcNow().AddMinutes(policy.Value.ProposalLifetimeMinutes),
            source.Label,
            source.WritesAllowed);

        store.Add(proposal, Owner, source.Id);
        return proposal;
    }

    /// <summary>Called by the API when a person clicks Confirm. Never exposed as an LLM tool.</summary>
    public async Task<CreatedDocument> ConfirmAsync(string actionId, CancellationToken ct = default)
    {
        if (!source.WritesAllowed)
            throw new ActionException($"This connection to {source.Label} is read-only. Reconnect with \"Allow creating quotations\" ticked to create it.");

        var proposal = store.Claim(actionId, Owner, source.Id);
        try
        {
            var draft = new QuotationDraft(
                proposal.CardCode,
                proposal.ValidUntil,
                proposal.Lines.Select(l => new QuotationDraftLine(l.ItemCode, l.Quantity, l.UnitPrice)).ToList(),
                $"Prepared by B1 Agent and confirmed by a user (ref {proposal.ActionId}).");

            var created = await source.Writer.CreateSalesQuotationAsync(draft, ct);
            store.Complete(actionId, created);
            return created;
        }
        catch
        {
            store.Release(actionId);
            throw;
        }
    }

    public ActionState Cancel(string actionId) => store.Cancel(actionId, Owner);

    public ActionState? Get(string actionId) => store.Get(actionId, Owner);
}
