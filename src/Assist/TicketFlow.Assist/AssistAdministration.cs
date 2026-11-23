using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Assist;

/// <summary>Default <see cref="IAssistAdministration"/>: every call authorizes <c>assist:manage</c> on the workspace before the store is used.</summary>
public sealed class AssistAdministration : IAssistAdministration
{
    private readonly IAssistStore store;
    private readonly IAssistModelRegistry registry;
    private readonly IAccessEvaluator evaluator;
    private readonly AssistOptions options;

    public AssistAdministration(IAssistStore store, IAssistModelRegistry registry, IAccessEvaluator evaluator, IOptions<AssistOptions> options)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
    }

    public async Task<TicketResult<StoredAssistSettings>> GetSettingsAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        if (await AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        var stored = await store.GetSettingsAsync(actor.Scope, cancellationToken).ConfigureAwait(false) ?? new StoredAssistSettings(actor.Scope, options.DefaultSettings(), 0);
        return new TicketValue<StoredAssistSettings>(stored);
    }

    public async Task<TicketResult<StoredAssistSettings>> UpdateSettingsAsync(PrincipalRef actor, AssistSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (await AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (expectedRevision < 0)
            return TicketFailure.Invalid("Expected revisions are 0 (create) or the current revision.");
        foreach (var (feature, model) in settings.Models)
            if (!registry.TryGet(model, out _))
                return TicketFailure.Invalid($"Model '{model}' for {feature} is not registered.");
        return await store.PutSettingsAsync(actor.Scope, settings, expectedRevision, cancellationToken).ConfigureAwait(false) switch
        {
            StoreApplied<StoredAssistSettings> applied => new TicketValue<StoredAssistSettings>(applied.Value),
            StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision, ["The settings changed or already exist."]),
        };
    }

    public async Task<TicketResult<AssistUsageSummary>> GetUsageAsync(PrincipalRef actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (await AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (to <= from || to - from > AssistLimits.MaximumSummaryRange)
            return TicketFailure.Invalid($"Usage ranges must be non-empty and at most {AssistLimits.MaximumSummaryRange.TotalDays} days.");
        return new TicketValue<AssistUsageSummary>(await store.SummarizeAsync(actor.Scope, from, to, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TicketFailure?> AuthorizeAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, AssistPermissions.Manage, AssistPermissions.Workspace(actor.Scope)), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted ? null : TicketFailure.ForbiddenWith([$"Permission '{AssistPermissions.Manage}' is required."]);
    }
}
