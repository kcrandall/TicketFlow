using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace TicketFlow.Access.Stores.Postgres;

/// <summary>
/// Column mapping for translating <see cref="AccessFilter"/> over an application table. Scope columns are required; optional
/// columns left <see langword="null"/> make nodes that need them untranslatable (and so the whole filter deny-all). Column names
/// are validated identifiers (optionally <c>alias.column</c>) and are quoted; they are never user input.
/// </summary>
public sealed record PostgresAccessFilterColumns
{
    public string TenantId { get; init; } = "tenant_id";
    public string ProjectId { get; init; } = "project_id";
    public string EnvironmentId { get; init; } = "environment_id";
    public string AppId { get; init; } = "app_id";
    public string WorkspaceId { get; init; } = "workspace_id";

    /// <summary>Nullable text column; NULL means no team.</summary>
    public string? TeamId { get; init; } = "team_id";

    /// <summary>Nullable text column; NULL means no inbox.</summary>
    public string? InboxId { get; init; } = "inbox_id";

    /// <summary>Nullable text column; NULL means unassigned.</summary>
    public string? AssigneeId { get; init; } = "assignee_id";

    /// <summary>Nullable <c>text[]</c> column without NULL elements; NULL means labels are unknown.</summary>
    public string? Labels { get; init; }

    /// <summary>Nullable <c>jsonb</c> object of string values; a missing key (or NULL column) is unknown.</summary>
    public string? Attributes { get; init; }
}

/// <summary>Translated SQL boolean expression and its parameters. Include it as <c>WHERE (Sql)</c>.</summary>
public sealed record PostgresAccessFilterSql(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)
{
    /// <summary>The fail-closed predicate.</summary>
    public static PostgresAccessFilterSql DenyAll { get; } = new("FALSE", []);
}

/// <summary>The filter needs a column the mapping does not provide. Callers must deny all rows.</summary>
public sealed record PostgresAccessFilterUntranslatable(string Reason);

/// <summary>Outcome of translating a filter. Switch over every case without a discard arm.</summary>
public union PostgresAccessFilterTranslation(PostgresAccessFilterSql, PostgresAccessFilterUntranslatable)
{
    /// <summary>The translated SQL, or <see cref="PostgresAccessFilterSql.DenyAll"/> when untranslatable. Never allow-all by fallback.</summary>
    public PostgresAccessFilterSql OrDenyAll() => this switch
    {
        PostgresAccessFilterSql sql => sql,
        PostgresAccessFilterUntranslatable => PostgresAccessFilterSql.DenyAll,
    };
}

/// <summary>
/// Translates <see cref="AccessFilter"/> into a parameterized PostgreSQL predicate with identical three-valued semantics: SQL
/// <c>NULL</c> is unknown and rows are included only when the predicate is true. Values are always bound as parameters.
/// </summary>
public static class PostgresAccessFilterTranslator
{
    /// <summary>Maximum bound parameters per translation.</summary>
    public const int MaximumParameters = 8192;

    public static PostgresAccessFilterTranslation Translate(AccessFilter filter, PostgresAccessFilterColumns? columns = null, string parameterPrefix = "access_")
    {
        ArgumentNullException.ThrowIfNull(filter);
        columns ??= new();
        if (parameterPrefix.Length is 0 or > 32 || !parameterPrefix.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') || parameterPrefix[0] is >= '0' and <= '9')
            throw new ArgumentException("Parameter prefixes must be 1-32 characters of a-z, 0-9 or '_' and not start with a digit.", nameof(parameterPrefix));
        var mapped = new Mapped(columns);
        var context = new Context(parameterPrefix);
        var sql = new StringBuilder();
        var reason = Write(filter, mapped, context, sql);
        if (reason is not null)
            return new PostgresAccessFilterUntranslatable(reason);
        return new PostgresAccessFilterSql(sql.ToString(), context.Parameters);
    }

    private sealed class Context(string prefix)
    {
        public List<NpgsqlParameter> Parameters { get; } = [];

        public string Add(object value, NpgsqlDbType type)
        {
            if (Parameters.Count >= MaximumParameters)
                throw new InvalidOperationException("The access filter needs too many parameters.");
            var name = prefix + Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            return "@" + name;
        }
    }

    private sealed class Mapped(PostgresAccessFilterColumns columns)
    {
        public string Tenant { get; } = Quote(columns.TenantId, nameof(columns.TenantId))!;
        public string Project { get; } = Quote(columns.ProjectId, nameof(columns.ProjectId))!;
        public string Environment { get; } = Quote(columns.EnvironmentId, nameof(columns.EnvironmentId))!;
        public string App { get; } = Quote(columns.AppId, nameof(columns.AppId))!;
        public string Workspace { get; } = Quote(columns.WorkspaceId, nameof(columns.WorkspaceId))!;
        public string? Team { get; } = Quote(columns.TeamId, nameof(columns.TeamId));
        public string? Inbox { get; } = Quote(columns.InboxId, nameof(columns.InboxId));
        public string? Assignee { get; } = Quote(columns.AssigneeId, nameof(columns.AssigneeId));
        public string? Labels { get; } = Quote(columns.Labels, nameof(columns.Labels));
        public string? Attributes { get; } = Quote(columns.Attributes, nameof(columns.Attributes));

        private static string? Quote(string? column, string name)
        {
            if (column is null)
                return null;
            var parts = column.Split('.');
            if (parts.Length > 2 || parts.Any(part => part.Length is 0 or > 63 || part[0] is not ((>= 'a' and <= 'z') or '_')
                || !part.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')))
                throw new ArgumentException($"Column mapping {name} must be a lowercase identifier or alias.identifier.", name);
            return string.Join('.', parts.Select(part => "\"" + part + "\""));
        }
    }

    /// <summary>Appends the predicate for <paramref name="filter"/>; returns a reason when a needed column is unmapped (the caller then denies all).</summary>
    private static string? Write(AccessFilter filter, Mapped columns, Context context, StringBuilder sql) => filter switch
    {
        AllowAllFilter => Append(sql, "TRUE"),
        DenyAllFilter => Append(sql, "FALSE"),
        UnknownFilter => Append(sql, "CAST(NULL AS boolean)"),
        ScopeFilter scope => Append(sql, "(" + string.Join(" AND ",
            Equal(columns.Tenant, scope.Scope.TenantId, context),
            Equal(columns.Project, scope.Scope.ProjectId, context),
            Equal(columns.Environment, scope.Scope.EnvironmentId, context),
            Equal(columns.App, scope.Scope.AppId, context),
            Equal(columns.Workspace, scope.Scope.WorkspaceId.Value, context)) + ")"),
        AndFilter and => Join(and.Operands, " AND ", columns, context, sql),
        OrFilter or => Join(or.Operands, " OR ", columns, context, sql),
        NotFilter not => Negate(not.Operand, columns, context, sql),
        TeamInFilter team => Membership(columns.Team, "team", team.Teams.Select(id => id.Value), context, sql),
        InboxInFilter inbox => Membership(columns.Inbox, "inbox", inbox.Inboxes.Select(id => id.Value), context, sql),
        AssigneeIsFilter assignee => columns.Assignee is null
            ? Missing("assignee")
            : Append(sql, $"({columns.Assignee} IS NOT NULL AND {columns.Assignee} = {context.Add(assignee.Assignee.Value, NpgsqlDbType.Text)})"),
        // NULL labels (unknown) yield NULL; an empty array yields false.
        HasLabelFilter label => columns.Labels is null
            ? Missing("labels")
            : Append(sql, $"({context.Add(label.Label.Value, NpgsqlDbType.Text)} = ANY({columns.Labels}))"),
        AttributeEqualsFilter equals => columns.Attributes is null
            ? Missing("attributes")
            : Append(sql, $"(({columns.Attributes} ->> {context.Add(equals.Key, NpgsqlDbType.Text)}) = {context.Add(equals.Value, NpgsqlDbType.Text)})"),
        // The value list is never empty, so a missing key compares NULL = ANY(...) and stays unknown.
        AttributeInFilter @in => columns.Attributes is null
            ? Missing("attributes")
            : Append(sql, $"(({columns.Attributes} ->> {context.Add(@in.Key, NpgsqlDbType.Text)}) = ANY({context.Add(@in.Values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)}))"),
    };

    private static string? Append(StringBuilder sql, string text)
    {
        sql.Append(text);
        return null;
    }

    private static string Equal(string column, string value, Context context) => $"{column} = {context.Add(value, NpgsqlDbType.Text)}";

    private static string Missing(string column) => $"The column mapping does not provide {column}.";

    private static string? Negate(AccessFilter operand, Mapped columns, Context context, StringBuilder sql)
    {
        sql.Append("(NOT ");
        var reason = Write(operand, columns, context, sql);
        sql.Append(')');
        return reason;
    }

    private static string? Membership(string? column, string name, IEnumerable<string> values, Context context, StringBuilder sql)
    {
        if (column is null)
            return Missing(name);
        var array = values.Order(StringComparer.Ordinal).ToArray();
        // Two-valued: a NULL column (no team/inbox) is false, never unknown.
        return Append(sql, array.Length == 0
            ? "FALSE"
            : $"({column} IS NOT NULL AND {column} = ANY({context.Add(array, NpgsqlDbType.Array | NpgsqlDbType.Text)}))");
    }

    private static string? Join(IReadOnlyList<AccessFilter> operands, string separator, Mapped columns, Context context, StringBuilder sql)
    {
        sql.Append('(');
        for (var index = 0; index < operands.Count; index++)
        {
            if (index > 0)
                sql.Append(separator);
            if (Write(operands[index], columns, context, sql) is { } reason)
                return reason;
        }
        sql.Append(')');
        return null;
    }
}
