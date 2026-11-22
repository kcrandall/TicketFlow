using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Permissions registered by <c>AddHelpCenter()</c>. Reading drafts and private portals needs <c>helpcenter:read</c>; editing
/// collections and article drafts needs <c>helpcenter:write</c>; publishing, unpublishing, archiving and rollback need
/// <c>helpcenter:publish</c>; portal administration and article deletion need <c>helpcenter:manage</c>. Anonymous visitors use
/// <see cref="IHelpCenterPublicReader"/> and never need a permission. Built-in Owner holds all of them; other roles receive them
/// through custom roles.
/// </summary>
public static class HelpCenterPermissions
{
    public static Permission Read { get; } = new("helpcenter:read");
    public static Permission Write { get; } = new("helpcenter:write");
    public static Permission Publish { get; } = new("helpcenter:publish");
    public static Permission Manage { get; } = new("helpcenter:manage");

    /// <summary>The feature-owned catalog definitions, ordered by permission.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Manage, true, "Manage help center portals, visibility and article deletion."),
        new(Publish, false, "Publish, unpublish, archive and roll back help center articles."),
        new(Read, false, "Read help center drafts, revisions and private portals."),
        new(Write, false, "Edit help center collections and article drafts."),
    ];
}

/// <summary>
/// The access snapshots help center records are authorized against. Portals, collections and articles carry no team, inbox,
/// assignee, labels (unknown) or attributes (unknown), so every record of a workspace has the same access shape: an access plan is
/// constant over rows and label/attribute conditions fail closed.
/// </summary>
public static class HelpCenterResources
{
    public static AccessResource Workspace(TicketScope scope) => new(scope);

    public static AccessResource Portal(TicketScope scope, PortalId portal) => new(scope, portal.Value);

    public static AccessResource Article(TicketScope scope, ArticleId article) => new(scope, article.Value);

    public static AccessResource Collection(TicketScope scope, CollectionId collection) => new(scope, collection.Value);
}
