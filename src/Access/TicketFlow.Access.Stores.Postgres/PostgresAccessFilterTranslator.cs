using System.Buffers;
using System.Globalization;
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
    public static PostgresAccessFilterColumns Default { get; } = new();

    private PostgresAccessFilterTranslator.Mapped? cachedMapped;

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

    internal PostgresAccessFilterTranslator.Mapped GetMapped() => cachedMapped ??= new PostgresAccessFilterTranslator.Mapped(this);
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
    private static readonly SearchValues<char> IdentifierChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary>Maximum bound parameters per translation.</summary>
    public const int MaximumParameters = 8192;

    public static PostgresAccessFilterTranslation Translate(AccessFilter filter, PostgresAccessFilterColumns? columns = null, string parameterPrefix = "access_")
    {
        ArgumentNullException.ThrowIfNull(filter);
        columns ??= PostgresAccessFilterColumns.Default;
        if (parameterPrefix.Length is 0 or > 32 || parameterPrefix[0] is >= '0' and <= '9' || parameterPrefix.AsSpan().ContainsAnyExcept(IdentifierChars))
            throw new ArgumentException("Parameter prefixes must be 1-32 characters of a-z, 0-9 or '_' and not start with a digit.", nameof(parameterPrefix));
        var mapped = columns.GetMapped();
        var context = new Context(parameterPrefix);
        var sql = new StringBuilder(128);
        var reason = Write(filter, mapped, context, sql);
        if (reason is not null)
            return new PostgresAccessFilterUntranslatable(reason);
        return new PostgresAccessFilterSql(sql.ToString(), context.Parameters);
    }

    private sealed class Context(string prefix)
    {
        public List<NpgsqlParameter> Parameters { get; } = [];

        public void AppendText(StringBuilder sql, string value)
        {
            if (Parameters.Count >= MaximumParameters)
                throw new InvalidOperationException("The access filter needs too many parameters.");
            var index = Parameters.Count;
            var name = prefix + index.ToString(CultureInfo.InvariantCulture);
            Parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });
            sql.Append('@').Append(name);
        }

        public void AppendTextArray(StringBuilder sql, string[] values)
        {
            if (Parameters.Count >= MaximumParameters)
                throw new InvalidOperationException("The access filter needs too many parameters.");
            var index = Parameters.Count;
            var name = prefix + index.ToString(CultureInfo.InvariantCulture);
            Parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });
            sql.Append('@').Append(name);
        }
    }

    internal sealed class Mapped(PostgresAccessFilterColumns columns)
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
            var dot = column.IndexOf('.');
            if (dot < 0)
            {
                ValidatePart(column.AsSpan(), name);
                return "\"" + column + "\"";
            }
            var left = column.AsSpan(0, dot);
            var right = column.AsSpan(dot + 1);
            if (right.Contains('.'))
                throw new ArgumentException($"Column mapping {name} must be a lowercase identifier or alias.identifier.", name);
            ValidatePart(left, name);
            ValidatePart(right, name);
            return $"\"{left}\".\"{right}\"";
        }

        private static void ValidatePart(ReadOnlySpan<char> part, string name)
        {
            if (part.Length is 0 or > 63 || part[0] is not ((>= 'a' and <= 'z') or '_') || part.ContainsAnyExcept(IdentifierChars))
                throw new ArgumentException($"Column mapping {name} must be a lowercase identifier or alias.identifier.", name);
        }
    }

    /// <summary>Appends the predicate for <paramref name="filter"/>; returns a reason when a needed column is unmapped (the caller then denies all).</summary>
    private static string? Write(AccessFilter filter, Mapped columns, Context context, StringBuilder sql) => filter switch
    {
        AllowAllFilter => Append(sql, "TRUE"),
        DenyAllFilter => Append(sql, "FALSE"),
        UnknownFilter => Append(sql, "CAST(NULL AS boolean)"),
        ScopeFilter scope => WriteScope(scope.Scope, columns, context, sql),
        AndFilter and => Join(and.Operands, " AND ", columns, context, sql),
        OrFilter or => Join(or.Operands, " OR ", columns, context, sql),
        NotFilter not => Negate(not.Operand, columns, context, sql),
        TeamInFilter team => Membership(columns.Team, "team", team.Teams, static id => id.Value, context, sql),
        InboxInFilter inbox => Membership(columns.Inbox, "inbox", inbox.Inboxes, static id => id.Value, context, sql),
        AssigneeIsFilter assignee => WriteAssignee(assignee, columns, context, sql),
        HasLabelFilter label => WriteLabel(label, columns, context, sql),
        AttributeEqualsFilter equals => WriteAttrEquals(equals, columns, context, sql),
        AttributeInFilter @in => WriteAttrIn(@in, columns, context, sql),
    };

    private static string? Append(StringBuilder sql, string text)
    {
        sql.Append(text);
        return null;
    }

    private static string? WriteScope(TicketFlow.Core.TicketScope scope, Mapped columns, Context context, StringBuilder sql)
    {
        sql.Append('(');
        AppendEqual(sql, columns.Tenant, scope.TenantId, context);
        sql.Append(" AND ");
        AppendEqual(sql, columns.Project, scope.ProjectId, context);
        sql.Append(" AND ");
        AppendEqual(sql, columns.Environment, scope.EnvironmentId, context);
        sql.Append(" AND ");
        AppendEqual(sql, columns.App, scope.AppId, context);
        sql.Append(" AND ");
        AppendEqual(sql, columns.Workspace, scope.WorkspaceId.Value, context);
        sql.Append(')');
        return null;
    }

    private static string? WriteAssignee(AssigneeIsFilter assignee, Mapped columns, Context context, StringBuilder sql)
    {
        if (columns.Assignee is null)
            return Missing("assignee");
        sql.Append('(').Append(columns.Assignee).Append(" IS NOT NULL AND ").Append(columns.Assignee).Append(" = ");
        context.AppendText(sql, assignee.Assignee.Value);
        sql.Append(')');
        return null;
    }

    // NULL labels (unknown) yield NULL; an empty array yields false.
    private static string? WriteLabel(HasLabelFilter label, Mapped columns, Context context, StringBuilder sql)
    {
        if (columns.Labels is null)
            return Missing("labels");
        sql.Append('(');
        context.AppendText(sql, label.Label.Value);
        sql.Append(" = ANY(").Append(columns.Labels).Append("))");
        return null;
    }

    private static string? WriteAttrEquals(AttributeEqualsFilter equals, Mapped columns, Context context, StringBuilder sql)
    {
        if (columns.Attributes is null)
            return Missing("attributes");
        sql.Append("((").Append(columns.Attributes).Append(" ->> ");
        context.AppendText(sql, equals.Key);
        sql.Append(") = ");
        context.AppendText(sql, equals.Value);
        sql.Append(')');
        return null;
    }

    // The value list is never empty, so a missing key compares NULL = ANY(...) and stays unknown.
    private static string? WriteAttrIn(AttributeInFilter @in, Mapped columns, Context context, StringBuilder sql)
    {
        if (columns.Attributes is null)
            return Missing("attributes");
        sql.Append("((").Append(columns.Attributes).Append(" ->> ");
        context.AppendText(sql, @in.Key);
        sql.Append(") = ANY(");
        context.AppendTextArray(sql, @in.Values as string[] ?? [.. @in.Values]);
        sql.Append("))");
        return null;
    }

    private static void AppendEqual(StringBuilder sql, string column, string value, Context context)
    {
        sql.Append(column).Append(" = ");
        context.AppendText(sql, value);
    }

    private static string Missing(string column) => $"The column mapping does not provide {column}.";

    private static string? Negate(AccessFilter operand, Mapped columns, Context context, StringBuilder sql)
    {
        sql.Append("(NOT ");
        var reason = Write(operand, columns, context, sql);
        sql.Append(')');
        return reason;
    }

    private static string? Membership<T>(string? column, string name, IReadOnlySet<T> values, Func<T, string> selector, Context context, StringBuilder sql)
    {
        if (column is null)
            return Missing(name);
        if (values.Count == 0)
        {
            sql.Append("FALSE");
            return null;
        }
        var array = new string[values.Count];
        var index = 0;
        foreach (var value in values)
            array[index++] = selector(value);
        if (array.Length > 1)
            Array.Sort(array, StringComparer.Ordinal);
        // Two-valued: a NULL column (no team/inbox) is false, never unknown.
        sql.Append('(').Append(column).Append(" IS NOT NULL AND ").Append(column).Append(" = ANY(");
        context.AppendTextArray(sql, array);
        sql.Append("))");
        return null;
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
