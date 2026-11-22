using TicketFlow.Core;

namespace TicketFlow.Access.Stores.Postgres;

public sealed partial class PostgresAccessStore
{
    private const string AuditColumns =
        "event_id, format_version, occurred_at, kind, outcome, actor_kind, actor_id, permission, resource_id, team_id, inbox_id, grant_id, role_id, field, used_break_glass, denial_reason, justification";

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
        command.Parameters.AddWithValue("id", auditEvent.Id);
        AddInstant(command, "occurred_at", auditEvent.OccurredAt);
        command.Parameters.AddWithValue("kind", (short)auditEvent.Kind);
        command.Parameters.AddWithValue("outcome", (short)auditEvent.Outcome);
        command.Parameters.AddWithValue("actor_kind", (short)auditEvent.Actor.Kind);
        AddText(command, "actor_id", auditEvent.Actor.Id);
        AddText(command, "permission", auditEvent.Permission?.Value);
        AddText(command, "resource_id", auditEvent.ResourceId);
        AddText(command, "team", auditEvent.TeamId?.Value);
        AddText(command, "inbox", auditEvent.InboxId?.Value);
        AddText(command, "grant", auditEvent.GrantId?.Value);
        AddText(command, "role", auditEvent.RoleId?.Value);
        AddText(command, "field", auditEvent.Field?.Value);
        command.Parameters.AddWithValue("used_break_glass", auditEvent.UsedBreakGlass);
        command.Parameters.Add(new Npgsql.NpgsqlParameter("denial_reason", NpgsqlTypes.NpgsqlDbType.Smallint)
        {
            Value = auditEvent.DenialReason is { } reason ? (short)reason : DBNull.Value,
        });
        AddText(command, "justification", auditEvent.Justification);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Audit events of one workspace scope in (occurred, id) order, after an optional event, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<AccessAuditEvent>> ReadAuditAsync(TicketScope scope, int limit = 100, Guid? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumAuditPage);
        await using var command = source.CreateCommand($"""
            SELECT {AuditColumns} FROM tf_access_audit
            WHERE {ScopeKeyWhere} AND (@after::uuid IS NULL OR (occurred_at, event_id) > (SELECT occurred_at, event_id FROM tf_access_audit WHERE event_id=@after::uuid AND {ScopeKeyWhere}))
            ORDER BY occurred_at, event_id LIMIT @limit
            """);
        AddScope(command, scope);
        command.Parameters.Add(new Npgsql.NpgsqlParameter("after", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var events = new List<AccessAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(Decode(() =>
            {
                if (reader.GetInt16(1) != 1)
                    throw new FormatException("Unsupported audit format version.");
                string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                return new AccessAuditEvent(
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    scope,
                    (AccessAuditKind)reader.GetInt16(3),
                    (AccessAuditOutcome)reader.GetInt16(4),
                    new AccessActor((AccessActorKind)reader.GetInt16(5), reader.GetString(6)),
                    Text(7) is { } permission ? Permission.Parse(permission) : null,
                    Text(8),
                    Text(9) is { } team ? new TeamId(team) : null,
                    Text(10) is { } inbox ? new InboxId(inbox) : null,
                    Text(11) is { } grant ? new GrantId(grant) : null,
                    Text(12) is { } role ? new RoleId(role) : null,
                    Text(13) is { } field ? new FieldPath(field) : null,
                    reader.GetBoolean(14),
                    reader.IsDBNull(15) ? null : (AccessDenialReason)reader.GetInt16(15),
                    Text(16));
            }, "audit event"));
        }
        return events;
    }
}
