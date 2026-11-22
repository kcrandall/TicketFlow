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

    public async Task<AssistAdminResult<StoredAssistSettings>> GetSettingsAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        if (await AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        var stored = await store.GetSettingsAsync(actor.Scope, cancellationToken).ConfigureAwait(false) ?? new StoredAssistSettings(actor.Scope, options.DefaultSettings(), 0);
        return new AssistValue<StoredAssistSettings>(stored);
    }

    public async Task<AssistAdminResult<StoredAssistSettings>> UpdateSettingsAsync(PrincipalRef actor, AssistSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (await AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (expectedRevision < 0)
            return new AssistRefused(AssistRefusal.Invalid, ["Expected revisions are 0 (create) or the current revision."]);
        foreach (var (feature, model) in settings.Models)
            if (!registry.TryGet(model, out _))
                return new AssistRefused(AssistRefusal.Invalid, [$"Model '{model}' for {feature} is not registered."]);
        return await store.PutSettingsAsync(actor.Scope, settings, expectedRevision, cancellationToken).ConfigureAwait(false) switch
        {
            AssistWriteApplied<StoredAssistSettings> applied => new AssistValue<StoredAssistSettings>(applied.Value),
            AssistWriteConflict conflict => new AssistRefused(AssistRefusal.Conflict, ["The settings changed or already exist."], CurrentRevision: conflict.CurrentRevision),
        };
    }

    public async Task<AssistAdminResult<AssistUsageSummary>> GetUsageAsync(PrincipalRef actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (await AuthorizeAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (to <= from || to - from > AssistLimits.MaximumSummaryRange)
            return new AssistRefused(AssistRefusal.Invalid, [$"Usage ranges must be non-empty and at most {AssistLimits.MaximumSummaryRange.TotalDays} days."]);
        return new AssistValue<AssistUsageSummary>(await store.SummarizeAsync(actor.Scope, from, to, cancellationToken).ConfigureAwait(false));
    }

    private async Task<AssistRefused?> AuthorizeAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, AssistPermissions.Manage, AssistPermissions.Workspace(actor.Scope)), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted ? null : new AssistRefused(AssistRefusal.Forbidden, [$"Permission '{AssistPermissions.Manage}' is required."]);
    }
}
