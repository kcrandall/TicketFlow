using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// Default <see cref="ISurveyService"/>. Definitions authorize on the survey's inbox (or workspace) before the store is written;
/// moving a survey between inboxes needs <c>survey:manage</c> on both. Invitation and response reads authorize on the record's
/// inbox/team/agent snapshot; response lists and aggregates push the planner's <see cref="AccessFilter"/> into the store and apply
/// the <c>survey.comment</c> field policy per returned row.
/// </summary>
public sealed class SurveyService : ISurveyService
{
    private readonly ISurveyStore store;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;

    public SurveyService(ISurveyStore store, IAccessEvaluator evaluator, IAccessQueryPlanner planner)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
    }

    public async Task<TicketResult<StoredSurvey>> GetSurveyAsync(PrincipalRef actor, SurveyId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        var stored = await store.GetSurveyAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return await AuthorizeAsync(actor, SurveyPermissions.Read, SurveyResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false) ?? NotFound;
        return await AuthorizeAsync(actor, SurveyPermissions.Read, SurveyResources.Survey(actor.Scope, stored.Survey), cancellationToken).ConfigureAwait(false)
            ?? (TicketResult<StoredSurvey>)new TicketValue<StoredSurvey>(stored);
    }

    public async Task<TicketResult<IReadOnlyList<StoredSurvey>>> ListSurveysAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var filter = await planner.PlanAsync(new AccessQuery(actor, SurveyPermissions.Read, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new TicketValue<IReadOnlyList<StoredSurvey>>([]);
        // Bounded (≤ 64 per workspace), so filtering the full list in memory is not an unbounded post-filter.
        var surveys = await store.ListSurveysAsync(actor.Scope, cancellationToken).ConfigureAwait(false);
        return new TicketValue<IReadOnlyList<StoredSurvey>>([.. surveys.Where(stored => AccessFilterEvaluator.Matches(filter, SurveyResources.Survey(actor.Scope, stored.Survey)))]);
    }

    public async Task<TicketResult<StoredSurvey>> CreateSurveyAsync(PrincipalRef actor, SurveyDefinition survey, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(survey);
        if (await AuthorizeAsync(actor, SurveyPermissions.Manage, SurveyResources.Survey(actor.Scope, survey), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return Map(await store.CreateSurveyAsync(actor.Scope, survey, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredSurvey>> UpdateSurveyAsync(PrincipalRef actor, SurveyDefinition survey, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(survey);
        if (await AuthorizeAsync(actor, SurveyPermissions.Manage, SurveyResources.Survey(actor.Scope, survey), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (expectedRevision < 1)
            return Invalid("Expected revisions start at 1.");
        var existing = await store.GetSurveyAsync(actor.Scope, survey.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return NotFound;
        if (existing.Survey.Inbox != survey.Inbox
            && await AuthorizeAsync(actor, SurveyPermissions.Manage, SurveyResources.Survey(actor.Scope, existing.Survey), cancellationToken).ConfigureAwait(false) is not null)
            return NotFound;
        if (existing.Revision != expectedRevision)
            return Conflict(existing.Revision);
        if (existing.Survey.Kind != survey.Kind)
            return Invalid("A survey's kind cannot change; create a new survey instead.");
        return Map(await store.UpdateSurveyAsync(actor.Scope, survey, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredSurvey>> DeleteSurveyAsync(PrincipalRef actor, SurveyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        if (expectedRevision < 1)
            return Invalid("Expected revisions start at 1.");
        var existing = await store.GetSurveyAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        var resource = existing is null ? SurveyResources.Workspace(actor.Scope) : SurveyResources.Survey(actor.Scope, existing.Survey);
        if (await AuthorizeAsync(actor, SurveyPermissions.Manage, resource, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return Map(await store.DeleteSurveyAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredInvitation>> GetInvitationAsync(PrincipalRef actor, SurveyInvitationId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        var stored = await store.GetInvitationAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return await AuthorizeAsync(actor, SurveyPermissions.Read, SurveyResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false) ?? NotFound;
        return await AuthorizeAsync(actor, SurveyPermissions.Read, SurveyResources.Invitation(actor.Scope, stored.Invitation), cancellationToken).ConfigureAwait(false)
            ?? (TicketResult<StoredInvitation>)new TicketValue<StoredInvitation>(stored);
    }

    public async Task<TicketResult<SurveyResponseView>> GetResponseAsync(PrincipalRef actor, SurveyInvitationId invitation, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(invitation.IsInitialized, nameof(invitation));
        var stored = await store.GetResponseAsync(actor.Scope, invitation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return await AuthorizeAsync(actor, SurveyPermissions.Read, SurveyResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false) ?? NotFound;
        var view = await ViewAsync(actor, stored.Response, cancellationToken).ConfigureAwait(false);
        return view is null ? Forbidden(SurveyPermissions.Read) : new TicketValue<SurveyResponseView>(view);
    }

    private static readonly Permission[] ReadPermissions = [SurveyPermissions.Read];

    public async Task<TicketResult<SurveyResponseViewPage>> ListResponsesAsync(PrincipalRef actor, SurveyResponseQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var filter = await planner.PlanAsync(new AccessQuery(actor, SurveyPermissions.Read, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new TicketValue<SurveyResponseViewPage>(new([], null));
        var page = await store.ListResponsesAsync(actor.Scope, query, filter, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count == 0)
            return new TicketValue<SurveyResponseViewPage>(new([], page.Next));
        var access = await evaluator.LoadDecisionContextAsync(actor, actor.Scope, ReadPermissions, cancellationToken).ConfigureAwait(false);
        var views = new List<SurveyResponseView>(page.Items.Count);
        foreach (var stored in page.Items)
        {
            if (stored.Scope != actor.Scope)
                throw new InvalidOperationException("The survey store returned a response outside the requested scope.");
            // Masking depends on the grants that apply to each row, so every row is decided against one access snapshot (no store
            // round trip per row); the filter and the decision agree.
            var decision = await access.DecideAsync(SurveyPermissions.Read, SurveyResources.Response(actor.Scope, stored.Response), cancellationToken).ConfigureAwait(false);
            if (View(stored.Response, decision) is { } view)
                views.Add(view);
        }
        return new TicketValue<SurveyResponseViewPage>(new(views, page.Next));
    }

    public async Task<TicketResult<SurveyAggregateResult>> AggregateAsync(PrincipalRef actor, SurveyAggregateQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        // Like other list/search paths (conversations, inboxes, help center), a denied plan yields an empty result, not Forbidden.
        var filter = await planner.PlanAsync(new AccessQuery(actor, SurveyPermissions.ReportView, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new TicketValue<SurveyAggregateResult>(new([], false));
        return new TicketValue<SurveyAggregateResult>(await store.AggregateAsync(actor.Scope, query, filter, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The response with its comment redacted when the <c>survey.comment</c> field is masked; null when reading is denied.</summary>
    private async Task<SurveyResponseView?> ViewAsync(PrincipalRef actor, SurveyResponse response, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, SurveyPermissions.Read, SurveyResources.Response(actor.Scope, response)), cancellationToken).ConfigureAwait(false);
        return View(response, decision);
    }

    private static SurveyResponseView? View(SurveyResponse response, AccessDecision decision)
    {
        return decision switch
        {
            AccessAllowed => new SurveyResponseView(response, response.Comment, false, false),
            AccessMasked masked => masked.Fields.FirstOrDefault(field => field.Field == SurveyFieldPaths.Comment) is { } field
                ? new SurveyResponseView(response, null, response.Comment is not null, response.Comment is not null && field.Revealable)
                : new SurveyResponseView(response, response.Comment, false, false),
            AccessDenied => null,
        };
    }

    private async Task<TicketFailure?> AuthorizeAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted ? null : Forbidden(permission);
    }

    private static TicketFailure Forbidden(Permission permission) => new(TicketFailureKind.Forbidden, [$"Permission '{permission}' is required."]);

    private static TicketFailure Invalid(string error) => new(TicketFailureKind.Invalid, [error]);

    private static TicketFailure NotFound { get; } = new(TicketFailureKind.NotFound, ["The record does not exist."]);

    private static TicketFailure Conflict(long? revision) => new(TicketFailureKind.Conflict, ["The record changed or already exists."], revision);

    private static TicketResult<T> Map<T>(StoreWriteResult<T, SurveyWriteRejection> result) => result switch
    {
        StoreApplied<T> applied => new TicketValue<T>(applied.Value),
        StoreNotFound => NotFound,
        StoreConflict conflict => Conflict(conflict.CurrentRevision),
        StoreRejected<SurveyWriteRejection> rejected => new TicketFailure(TicketFailureKind.Rejected, [rejected.Reason switch
        {
            SurveyWriteRejection.CapacityExceeded => "A survey capacity limit would be exceeded.",
            SurveyWriteRejection.InUse => "The survey has invitations; disable it instead of deleting it.",
            _ => "The store rejected the write.",
        }]),
    };

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    private static void Require(bool initialized, string parameterName)
    {
        if (!initialized)
            throw new ArgumentException("Identifiers must be initialized.", parameterName);
    }
}
