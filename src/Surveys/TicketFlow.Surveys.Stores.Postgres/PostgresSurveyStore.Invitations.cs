using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;

namespace TicketFlow.Surveys.Stores.Postgres;

public sealed partial class PostgresSurveyStore
{
    private const string InvitationColumns =
        "invitation_id, survey_id, kind, conversation_id, contact_id, inbox_id, team_id, agent_id, locale, created_at, send_after, state, attempts, token_hash, issued_at, expires_at, format_version, revision, lease_until";

    private const string InvitationKeyWhere = "invitation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@invitation::text) AND " + WorkspaceWhere + " AND invitation_id=@invitation";
    private const string ResponseColumns = "invitation_id, survey_id, kind, score, comment, responded_at, conversation_id, contact_id, inbox_id, team_id, agent_id, format_version";
    private static readonly string ResponseColumnsR = string.Join(", ", ResponseColumns.Split(", ").Select(column => "r." + column));

    public async Task<InvitationCreateResult> CreateInvitationAsync(TicketScope scope, SurveyInvitation invitation, TimeSpan contactCooldown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentOutOfRangeException.ThrowIfLessThan(contactCooldown, TimeSpan.Zero);
        if (invitation.State != SurveyInvitationState.Pending)
            throw new ArgumentException("New invitations must be pending.", nameof(invitation));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken, "contact", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value,
            invitation.Survey.Value, invitation.Contact.Value).ConfigureAwait(false);
        if (await FindByConversationAsync(connection, transaction, scope, invitation, cancellationToken).ConfigureAwait(false) is { } duplicate)
            return new InvitationDuplicate(duplicate);
        await using (var last = new NpgsqlCommand("""
            SELECT max(created_at) FROM tf_survey_invitations WHERE contact_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@survey,@contact)
              AND tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace AND survey_id=@survey AND contact_id=@contact
            """, connection, transaction))
        {
            AddScope(last, scope);
            AddText(last, "survey", invitation.Survey.Value);
            AddText(last, "contact", invitation.Contact.Value);
            if (await last.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime previous)
            {
                var lastInvited = new DateTimeOffset(DateTime.SpecifyKind(previous, DateTimeKind.Utc));
                if (lastInvited > invitation.CreatedAt - contactCooldown)
                    return new InvitationCoolingDown(lastInvited);
            }
        }
        try
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO tf_survey_invitations({ScopeColumns}, invitation_id, survey_id, kind, conversation_id, contact_id, inbox_id, team_id, agent_id, locale,
                    created_at, send_after, state, attempts, token_hash, issued_at, expires_at, format_version, revision, lease_until)
                VALUES({ScopeParameters}, @invitation, @survey, @kind, @conversation, @contact, @inbox, @team, @agent, @locale,
                    @created_at, @send_after, @state, @attempts, @token, @issued_at, @expires_at, 1, 1, @lease_until)
                """, connection, transaction);
            BindInvitation(insert, scope, invitation);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == "tf_survey_invitations_conversation")
        {
            // A concurrent create for the same conversation (but another contact lock) won; report it as the duplicate it is.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await using var reread = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new InvitationDuplicate((await FindByConversationAsync(reread, null, scope, invitation, cancellationToken).ConfigureAwait(false))!);
        }
        return new InvitationCreated(new StoredInvitation(scope, invitation, 1));
    }

    public async Task<StoredInvitation?> GetInvitationAsync(TicketScope scope, SurveyInvitationId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT {InvitationColumns} FROM tf_survey_invitations WHERE {InvitationKeyWhere}");
        BindInvitationKey(command, scope, id);
        return await ReadSingleAsync(command, reader => ReadInvitation(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredInvitation?> FindInvitationByTokenAsync(TicketScope scope, SurveyTokenHash tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(tokenHash.IsInitialized, nameof(tokenHash));
        await using var command = source.CreateCommand($"SELECT {InvitationColumns} FROM tf_survey_invitations WHERE token_hash=@token AND {ScopeKeyWhere}");
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Bytea) { Value = tokenHash.ToBytes() });
        return await ReadSingleAsync(command, reader => ReadInvitation(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredInvitation>> ListDueInvitationsAsync(TicketScope scope, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SurveyLimits.MaximumDuePerRead);
        // UNION ALL of two index-ordered branches lets PostgreSQL use both partial indexes instead of an OR scan.
        await using var command = source.CreateCommand($"""
            SELECT {InvitationColumns} FROM (
                (SELECT {InvitationColumns} FROM tf_survey_invitations
                 WHERE {ScopeKeyWhere} AND state=1 AND send_after <= @now AND attempts < {SurveyInvitation.MaximumAttempts}
                 ORDER BY send_after, invitation_id LIMIT @limit)
                UNION ALL
                (SELECT {InvitationColumns} FROM tf_survey_invitations
                 WHERE {ScopeKeyWhere} AND state=6 AND lease_until <= @now AND attempts < {SurveyInvitation.MaximumAttempts}
                 ORDER BY send_after, invitation_id LIMIT @limit)
            ) due
            ORDER BY send_after, invitation_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddTime(command, "now", now);
        command.Parameters.AddWithValue("limit", limit);
        return await ReadListAsync(command, reader => ReadInvitation(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One statement: lock up to <paramref name="limit"/> due rows with <c>FOR UPDATE SKIP LOCKED</c> (rows another dispatcher is
    /// claiming are skipped; rows it already claimed no longer match after re-check) and move them to Sending with a lease.
    /// </summary>
    public async Task<IReadOnlyList<StoredInvitation>> ClaimDueInvitationsAsync(TicketScope scope, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SurveyLimits.MaximumDuePerRead);
        await using var command = source.CreateCommand($"""
            WITH pending AS (
                SELECT invitation_key, send_after, invitation_id FROM tf_survey_invitations
                WHERE {ScopeKeyWhere} AND state=1 AND send_after <= @now AND attempts < {SurveyInvitation.MaximumAttempts}
                ORDER BY send_after, invitation_id LIMIT @limit FOR UPDATE SKIP LOCKED
            ),
            expired AS (
                SELECT invitation_key, send_after, invitation_id FROM tf_survey_invitations
                WHERE {ScopeKeyWhere} AND state=6 AND lease_until <= @now AND attempts < {SurveyInvitation.MaximumAttempts}
                ORDER BY send_after, invitation_id LIMIT @limit FOR UPDATE SKIP LOCKED
            ),
            picked AS (
                SELECT invitation_key FROM (
                    SELECT invitation_key, send_after, invitation_id FROM pending
                    UNION ALL
                    SELECT invitation_key, send_after, invitation_id FROM expired
                ) due
                ORDER BY send_after, invitation_id LIMIT @limit
            )
            UPDATE tf_survey_invitations SET state=6, lease_until=@lease_until, attempts=attempts+1, revision=revision+1
            WHERE invitation_key IN (SELECT invitation_key FROM picked)
            RETURNING {InvitationColumns}
            """);
        AddScope(command, scope);
        AddTime(command, "now", now);
        AddTime(command, "lease_until", SurveyLimits.Truncate(now + lease));
        command.Parameters.AddWithValue("limit", limit);
        var rows = await ReadListAsync(command, reader => ReadInvitation(reader, scope), cancellationToken).ConfigureAwait(false);
        rows.Sort(static (a, b) =>
        {
            var c = a.Invitation.SendAfter.CompareTo(b.Invitation.SendAfter);
            return c != 0 ? c : a.Invitation.Id.CompareTo(b.Invitation.Id);
        });
        return rows;
    }

    public async Task<StoreWriteResult<StoredInvitation, SurveyWriteRejection>> UpdateInvitationAsync(TicketScope scope, SurveyInvitation invitation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        // Single auto-committed conditional UPDATE checking revision and immutable columns in one RTT.
        try
        {
            await using var update = source.CreateCommand($"""
                UPDATE tf_survey_invitations SET inbox_id=@inbox, team_id=@team, agent_id=@agent, locale=@locale, created_at=@created_at, send_after=@send_after,
                    state=@state, attempts=@attempts, token_hash=@token, issued_at=@issued_at, expires_at=@expires_at, lease_until=@lease_until, revision=revision+1
                WHERE {InvitationKeyWhere} AND revision=@expected AND survey_id=@survey AND kind=@kind AND conversation_id=@conversation AND contact_id=@contact
                RETURNING revision
                """);
            BindInvitation(update, scope, invitation);
            update.Parameters.AddWithValue("expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new StoreApplied<StoredInvitation>(new(scope, invitation, revision));
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == "tf_survey_invitations_token")
        {
            return new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.InUse);
        }

        // 0-row miss: fresh-snapshot SELECT to distinguish NotFound vs Conflict vs immutable-column mismatch.
        await using var check = source.CreateCommand($"SELECT revision, survey_id, kind, conversation_id, contact_id FROM tf_survey_invitations WHERE {InvitationKeyWhere}");
        BindInvitationKey(check, scope, invitation.Id);
        await using var reader = await check.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return StoreNotFound.Instance;
        var currentRevision = reader.GetInt64(0);
        if (currentRevision != expectedRevision)
            return new StoreConflict(currentRevision);
        return new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.InUse);
    }

    public async Task<ResponseRecordResult> RecordResponseAsync(TicketScope scope, SurveyResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        StoredInvitation? current;
        await using (var select = new NpgsqlCommand($"SELECT {InvitationColumns} FROM tf_survey_invitations WHERE {InvitationKeyWhere} FOR UPDATE", connection, transaction))
        {
            BindInvitationKey(select, scope, response.Invitation);
            current = await ReadSingleAsync(select, reader => ReadInvitation(reader, scope), cancellationToken).ConfigureAwait(false);
        }
        if (current is null)
            return ResponseInvitationNotFound.Instance;
        var invitation = current.Invitation;
        // Responses are only inserted in the same transaction that transitions the invitation to Responded.
        if (invitation.State == SurveyInvitationState.Responded)
        {
            await using var existing = new NpgsqlCommand($"SELECT {ResponseColumns} FROM tf_survey_responses WHERE {InvitationKeyWhere}", connection, transaction);
            BindInvitationKey(existing, scope, response.Invitation);
            if (await ReadSingleAsync(existing, reader => ReadResponse(reader, scope), cancellationToken).ConfigureAwait(false) is { } recorded)
                return new ResponseAlreadyRecorded(recorded);
        }
        if (invitation.TokenHash is null || invitation.State is SurveyInvitationState.Responded or SurveyInvitationState.Cancelled || invitation.Survey != response.Survey || invitation.Kind != response.Kind
            || invitation.Conversation != response.Conversation || invitation.Contact != response.Contact || invitation.Inbox != response.Inbox
            || invitation.Team != response.Team || invitation.Agent != response.Agent)
            return ResponseInvitationNotFound.Instance;
        if (!invitation.AcceptsResponses(response.RespondedAt))
            return new ResponseInvitationExpired(invitation.ExpiresAt!.Value);
        await using (var write = new NpgsqlCommand($"""
            WITH ins AS (
                INSERT INTO tf_survey_responses({ScopeColumns}, invitation_id, survey_id, kind, score, comment, responded_at, conversation_id, contact_id, inbox_id, team_id, agent_id, format_version)
                VALUES({ScopeParameters}, @invitation, @survey, @kind, @score, @comment, @responded_at, @conversation, @contact, @inbox, @team, @agent, 1)
            )
            UPDATE tf_survey_invitations SET state=3, lease_until=NULL, revision=revision+1 WHERE {InvitationKeyWhere}
            """, connection, transaction))
        {
            BindInvitationKey(write, scope, response.Invitation);
            AddText(write, "survey", response.Survey.Value);
            write.Parameters.AddWithValue("kind", (short)response.Kind);
            write.Parameters.AddWithValue("score", (short)response.Score);
            AddText(write, "comment", response.Comment);
            AddTime(write, "responded_at", response.RespondedAt);
            AddText(write, "conversation", response.Conversation.Value);
            AddText(write, "contact", response.Contact.Value);
            AddText(write, "inbox", response.Inbox?.Value);
            AddText(write, "team", response.Team?.Value);
            AddText(write, "agent", response.Agent?.Value);
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ResponseRecorded(new StoredResponse(scope, response));
    }

    public async Task<StoredResponse?> GetResponseAsync(TicketScope scope, SurveyInvitationId invitation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(invitation.IsInitialized, nameof(invitation));
        await using var command = source.CreateCommand($"SELECT {ResponseColumns} FROM tf_survey_responses WHERE {InvitationKeyWhere}");
        BindInvitationKey(command, scope, invitation);
        return await ReadSingleAsync(command, reader => ReadResponse(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SurveyResponsePage> ListResponsesAsync(TicketScope scope, SurveyResponseQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        await using var command = source.CreateCommand();
        var (from, predicate) = FilteredResponses(filter, command);
        if (predicate is null)
            return new SurveyResponsePage([], null);
        command.CommandText = $"""
            SELECT {ResponseColumnsR} FROM {from}
            WHERE {predicate} AND r.responded_at >= @from AND r.responded_at < @to AND (@survey::text IS NULL OR r.survey_id=@survey::text)
              AND (@after_id::uuid IS NULL OR (r.responded_at, r.invitation_id) < (@after_at::timestamptz, @after_id::uuid))
            ORDER BY r.responded_at DESC, r.invitation_id DESC LIMIT @limit
            """;
        AddScope(command, scope);
        AddTime(command, "from", query.From);
        AddTime(command, "to", query.To);
        AddText(command, "survey", query.Survey?.Value);
        AddTime(command, "after_at", query.After?.RespondedAt);
        command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = query.After is { } after ? after.Invitation.Value : DBNull.Value });
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        var rows = await ReadListAsync(command, reader => ReadResponse(reader, scope), cancellationToken).ConfigureAwait(false);
        if (rows.Count <= query.Limit)
            return new SurveyResponsePage(rows, null);
        var items = rows[..query.Limit];
        return new SurveyResponsePage(items, new SurveyResponseCursor(items[^1].Response.RespondedAt, items[^1].Response.Invitation));
    }

    public async Task<SurveyAggregateResult> AggregateAsync(TicketScope scope, SurveyAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        // Bucket units and dimension columns come from fixed switch arms, never from input text.
        var bucket = query.Bucket switch
        {
            SurveyTimeBucket.None => "@from",
            SurveyTimeBucket.Day => "date_trunc('day', r.responded_at, 'UTC')",
            SurveyTimeBucket.Week => "date_trunc('week', r.responded_at, 'UTC')",
            SurveyTimeBucket.Month => "date_trunc('month', r.responded_at, 'UTC')",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        var group = query.GroupBy switch
        {
            SurveyDimension.None => "NULL::text",
            SurveyDimension.Inbox => "r.inbox_id",
            SurveyDimension.Team => "r.team_id",
            SurveyDimension.Agent => "r.agent_id",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        await using var command = source.CreateCommand();
        var (from, predicate) = FilteredResponses(filter, command);
        if (predicate is null)
            return new SurveyAggregateResult([], false);
        command.CommandText = $"""
            SELECT g.bucket, g.grp, count(*), count(*) FILTER (WHERE g.score >= @positive), count(*) FILTER (WHERE g.score >= @neutral AND g.score < @positive),
                   count(*) FILTER (WHERE g.score < @neutral), sum(g.score)::bigint
            FROM (SELECT {bucket} AS bucket, {group} AS grp, r.score FROM {from}
                  WHERE {predicate} AND r.kind=@kind AND r.responded_at >= @from AND r.responded_at < @to AND (@survey::text IS NULL OR r.survey_id=@survey::text)) AS g
            GROUP BY g.bucket, g.grp
            ORDER BY g.bucket, g.grp COLLATE "C" NULLS FIRST
            LIMIT {SurveyAggregateQuery.MaximumRows + 1}
            """;
        AddScope(command, scope);
        AddTime(command, "from", query.From);
        AddTime(command, "to", query.To);
        AddText(command, "survey", query.Survey?.Value);
        command.Parameters.AddWithValue("kind", (short)query.Kind);
        command.Parameters.AddWithValue("positive", (short)(query.Kind == SurveyKind.Csat ? 4 : 9));
        command.Parameters.AddWithValue("neutral", (short)(query.Kind == SurveyKind.Csat ? 3 : 7));
        var rows = await ReadListAsync(command, reader => new SurveyAggregateRow(query.Kind, reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)), cancellationToken).ConfigureAwait(false);
        var truncated = rows.Count > SurveyAggregateQuery.MaximumRows;
        return new SurveyAggregateResult(truncated ? rows[..SurveyAggregateQuery.MaximumRows] : rows, truncated);
    }

    /// <summary>
    /// A derived table <c>r</c> of the bound scope's responses exposing the access columns of <see cref="SurveyResources.Response"/>
    /// (agent as assignee, unknown labels and attributes), and the translated filter predicate over it. Untranslatable filters
    /// become FALSE.
    /// </summary>
    private static (string From, string? Predicate) FilteredResponses(AccessFilter filter, NpgsqlCommand command)
    {
        var translated = PostgresAccessFilterTranslator.Translate(filter, ResponseAccessColumns, "access_").OrDenyAll();
        if (translated.Sql == "FALSE")
            return (string.Empty, null);
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter.Clone());
        return ("""
            (SELECT t.*, NULL::text[] AS labels, NULL::jsonb AS attributes FROM tf_survey_responses t
             WHERE t.scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND t.tenant_id=@tenant AND t.project_id=@project
               AND t.environment_id=@environment AND t.app_id=@app AND t.workspace_id=@workspace) AS r
            """, $"({translated.Sql})");
    }

    private static readonly PostgresAccessFilterColumns ResponseAccessColumns = new()
    {
        TenantId = "r.tenant_id",
        ProjectId = "r.project_id",
        EnvironmentId = "r.environment_id",
        AppId = "r.app_id",
        WorkspaceId = "r.workspace_id",
        TeamId = "r.team_id",
        InboxId = "r.inbox_id",
        AssigneeId = "r.agent_id",
        Labels = "r.labels",
        Attributes = "r.attributes",
    };

    private static async Task<StoredInvitation?> FindByConversationAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, SurveyInvitation invitation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {InvitationColumns} FROM tf_survey_invitations WHERE conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@survey,@conversation)
              AND {WorkspaceWhere} AND survey_id=@survey AND conversation_id=@conversation
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "survey", invitation.Survey.Value);
        AddText(command, "conversation", invitation.Conversation.Value);
        return await ReadSingleAsync(command, reader => ReadInvitation(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    private static void BindInvitationKey(NpgsqlCommand command, TicketScope scope, SurveyInvitationId id)
    {
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("invitation", NpgsqlDbType.Uuid) { Value = id.Value });
    }

    private static void BindInvitation(NpgsqlCommand command, TicketScope scope, SurveyInvitation invitation)
    {
        BindInvitationKey(command, scope, invitation.Id);
        AddText(command, "survey", invitation.Survey.Value);
        command.Parameters.AddWithValue("kind", (short)invitation.Kind);
        AddText(command, "conversation", invitation.Conversation.Value);
        AddText(command, "contact", invitation.Contact.Value);
        AddText(command, "inbox", invitation.Inbox?.Value);
        AddText(command, "team", invitation.Team?.Value);
        AddText(command, "agent", invitation.Agent?.Value);
        AddText(command, "locale", invitation.Locale.Value);
        AddTime(command, "created_at", invitation.CreatedAt);
        AddTime(command, "send_after", invitation.SendAfter);
        command.Parameters.AddWithValue("state", (short)invitation.State);
        command.Parameters.AddWithValue("attempts", invitation.Attempts);
        command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Bytea) { Value = invitation.TokenHash is { } hash ? hash.ToBytes() : DBNull.Value });
        AddTime(command, "issued_at", invitation.IssuedAt);
        AddTime(command, "expires_at", invitation.ExpiresAt);
        AddTime(command, "lease_until", invitation.LeaseUntil);
    }

    private static StoredInvitation ReadInvitation(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(16) != 1)
            throw new FormatException("Unsupported invitation format version.");
        var invitation = new SurveyInvitation(new SurveyInvitationId(reader.GetGuid(0)), new SurveyId(reader.GetString(1)), Enum<SurveyKind>(reader.GetInt16(2)),
            new ConversationId(reader.GetString(3)), new ContactId(reader.GetString(4)), reader.NullableText(5) is { } inbox ? new InboxId(inbox) : null, reader.NullableText(6) is { } team ? new TeamId(team) : null,
            reader.NullableText(7) is { } agent ? new UserId(agent) : null, new SurveyLocale(reader.GetString(8)), reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10),
            Enum<SurveyInvitationState>(reader.GetInt16(11)), reader.GetInt32(12), reader.IsDBNull(13) ? null : SurveyTokenHash.FromBytes(reader.GetFieldValue<byte[]>(13)), reader.NullableInstant(14), reader.NullableInstant(15),
            reader.NullableInstant(18));
        return new StoredInvitation(scope, invitation, reader.GetInt64(17));
    }, "invitation");

    private static StoredResponse ReadResponse(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(11) != 1)
            throw new FormatException("Unsupported response format version.");
        var response = new SurveyResponse(new SurveyInvitationId(reader.GetGuid(0)), new SurveyId(reader.GetString(1)), Enum<SurveyKind>(reader.GetInt16(2)), reader.GetInt16(3),
            reader.NullableText(4), reader.GetFieldValue<DateTimeOffset>(5), new ConversationId(reader.GetString(6)), new ContactId(reader.GetString(7)),
            reader.NullableText(8) is { } inbox ? new InboxId(inbox) : null, reader.NullableText(9) is { } team ? new TeamId(team) : null, reader.NullableText(10) is { } agent ? new UserId(agent) : null);
        return new StoredResponse(scope, response);
    }, "response");
}
