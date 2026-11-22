using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Permissions used by the conversations feature. <c>conversation:read|reply|assign|resolve|manage|delete</c> and
/// <c>note:read|write</c> are Access built-ins (so built-in roles hold them); <c>conversation:create</c>, <c>conversation:ingest</c>
/// and <c>conversation-attribute:manage</c> are registered by <c>AddConversations()</c>.
/// </summary>
public static class ConversationPermissions
{
    /// <summary>Get, list and read timelines (notes additionally need <see cref="NoteRead"/>).</summary>
    public static Permission Read => AccessPermissions.ConversationRead;

    /// <summary>Send outgoing replies.</summary>
    public static Permission Reply => AccessPermissions.ConversationReply;

    /// <summary>Assign/unassign agents and set the team.</summary>
    public static Permission Assign => AccessPermissions.ConversationAssign;

    /// <summary>Change status (open, pending, snoozed, resolved, closed).</summary>
    public static Permission Resolve => AccessPermissions.ConversationResolve;

    /// <summary>Change priority, labels, attributes and participants; append extension events.</summary>
    public static Permission Manage => AccessPermissions.ConversationManage;

    /// <summary>Delete a conversation and its timeline (sensitive).</summary>
    public static Permission Delete => AccessPermissions.ConversationDelete;

    /// <summary>See private notes in timelines.</summary>
    public static Permission NoteRead => AccessPermissions.NoteRead;

    /// <summary>Add private notes.</summary>
    public static Permission NoteWrite => AccessPermissions.NoteWrite;

    /// <summary>Start conversations.</summary>
    public static Permission Create { get; } = new("conversation:create");

    /// <summary>Channel ingestion: record incoming messages, delivery status updates and look up conversations by channel thread.</summary>
    public static Permission Ingest { get; } = new("conversation:ingest");

    /// <summary>Define conversation attributes and their sensitivity (sensitive).</summary>
    public static Permission ManageAttributes { get; } = new("conversation-attribute:manage");

    /// <summary>The feature-owned definitions registered in the permission catalog (built-ins are already cataloged).</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Create, false, "Start conversations."),
        new(Ingest, false, "Record channel messages and delivery status."),
        new(ManageAttributes, true, "Define conversation attributes and their sensitivity."),
    ];
}

/// <summary>
/// Field paths masked through Access field policies for <c>conversation:read</c>: <see cref="Channel"/> covers the channel thread
/// identifier and external message identifiers (correlation evidence that may contain addresses); attribute values use
/// <c>conversation.attribute.&lt;key&gt;</c>.
/// </summary>
public static class ConversationFieldPaths
{
    public static FieldPath Channel { get; } = new("conversation.channel");

    public const string AttributePrefix = "conversation.attribute.";

    /// <summary>The masking path of attribute <paramref name="key"/>.</summary>
    public static FieldPath Attribute(string key) => new(AttributePrefix + ConversationAttributeDefinition.RequireKey(key, nameof(key)));

    /// <summary>A field policy for a sensitive attribute: masked for <c>conversation:read</c> unless the grant uses one of <paramref name="unmaskedRoles"/>.</summary>
    public static AccessFieldPolicy SensitivePolicy(FieldPath field, IEnumerable<RoleId>? unmaskedRoles = null, Permission? revealPermission = null) =>
        new(field, [ConversationPermissions.Read], unmaskedRoles, revealPermission);
}

/// <summary>
/// The Access resource for a conversation: scope, identifier, team, inbox, assignee, labels and the condition form of its attributes
/// (<see cref="ConversationAttributeValue.ConditionValue"/>; list attributes are omitted, so conditions on them are unknown). Point
/// checks and list filters use exactly this shape, so a filtered list returns exactly the conversations whose point check passes.
/// </summary>
public static class ConversationAccess
{
    public static AccessResource Resource(TicketScope scope, Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(conversation);
        return new(scope, conversation.Id.Value, conversation.Team, conversation.Inbox, conversation.Assignee, conversation.Labels,
            conversation.Attributes
                .Where(pair => pair.Value.ConditionValue is not null)
                .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.ConditionValue!)));
    }

    public static AccessResource Resource(StoredConversation stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return Resource(stored.Scope, stored.Conversation);
    }
}
