using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Access.Stores.Postgres;

public sealed partial class PostgresAccessStore
{
    private const string AuditColumns =
        "event_id, format_version, occurred_at, kind, outcome, actor_kind, actor_id, permission, resource_id, team_id, inbox_id, grant_id, role_id, field, used_break_glass, denial_reason, justification";

    private const string ReadAuditSql = $"""
        SELECT {AuditColumns} FROM tf_access_audit
        WHERE {ScopeKeyWhere}
        ORDER BY occurred_at, event_id LIMIT @limit
        """;

    private const string ReadAuditAfterSql = $"""
        SELECT {AuditColumns} FROM tf_access_audit
        WHERE {ScopeKeyWhere}
          AND (occurred_at, event_id) > (SELECT occurred_at, event_id FROM tf_access_audit WHERE event_id=@after AND {ScopeKeyWhere})
        ORDER BY occurred_at, event_id LIMIT @limit
        """;

    /// <summary>Maximum events returned by one <see cref="ReadAuditAsync"/> call.</summary>
    public const int MaximumAuditPage = 1000;

    public async Task RecordAsync(AccessAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var command = source.CreateCommand($"""
            INSERT INTO tf_access_audit(tenant_id, project_id, environment_id, app_id, workspace_id, {AuditColumns})
            VALUES(@tenant, @project, @environment, @app, @workspace, @id, 1, @occurred_at, @kind, @outcome, @actor_kind, @actor_id, @permission,
                @resource_id, @team, @inbox, @grant, @role, @field, @used_break_glass, @denial_reason, @justification)
            """);
        AddScope(command, auditEvent.Scope);
        AddGuid(command, "id", auditEvent.Id);
        AddInstant(command, "occurred_at", auditEvent.OccurredAt);
        AddShort(command, "kind", (short)auditEvent.Kind);
        AddShort(command, "outcome", (short)auditEvent.Outcome);
        AddShort(command, "actor_kind", (short)auditEvent.Actor.Kind);
        AddText(command, "actor_id", auditEvent.Actor.Id);
        AddText(command, "permission", auditEvent.Permission?.Value);
        AddText(command, "resource_id", auditEvent.ResourceId);
        AddText(command, "team", auditEvent.TeamId?.Value);
        AddText(command, "inbox", auditEvent.InboxId?.Value);
        AddText(command, "grant", auditEvent.GrantId?.Value);
        AddText(command, "role", auditEvent.RoleId?.Value);
        AddText(command, "field", auditEvent.Field?.Value);
        AddBool(command, "used_break_glass", auditEvent.UsedBreakGlass);
        AddNullableShort(command, "denial_reason", auditEvent.DenialReason is { } reason ? (short)reason : null);
        AddText(command, "justification", auditEvent.Justification);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Audit events of one workspace scope in (occurred, id) order, after an optional event, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<AccessAuditEvent>> ReadAuditAsync(TicketScope scope, int limit = 100, Guid? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumAuditPage);
        await using var command = source.CreateCommand(after is null ? ReadAuditSql : ReadAuditAfterSql);
        AddScope(command, scope);
        if (after is { } cursor)
            AddGuid(command, "after", cursor);
        AddInt(command, "limit", limit);
        var events = new List<AccessAuditEvent>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(ReadAuditEvent(reader, scope));
        return events;
    }

    private static AccessAuditEvent ReadAuditEvent(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported audit format version.");
            return new AccessAuditEvent(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(2),
                scope,
                (AccessAuditKind)reader.GetInt16(3),
                (AccessAuditOutcome)reader.GetInt16(4),
                new AccessActor((AccessActorKind)reader.GetInt16(5), reader.GetString(6)),
                reader.NullableText(7) is { } permission ? Permission.Parse(permission) : null,
                reader.NullableText(8),
                reader.NullableText(9) is { } team ? new TeamId(team) : null,
                reader.NullableText(10) is { } inbox ? new InboxId(inbox) : null,
                reader.NullableText(11) is { } grant ? new GrantId(grant) : null,
                reader.NullableText(12) is { } role ? new RoleId(role) : null,
                reader.NullableText(13) is { } field ? new FieldPath(field) : null,
                reader.GetBoolean(14),
                reader.IsDBNull(15) ? null : (AccessDenialReason)reader.GetInt16(15),
                reader.NullableText(16));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "audit event");
        }
    }
}
