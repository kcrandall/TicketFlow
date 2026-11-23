using System.Buffers;
using System.Text.Unicode;

namespace TicketFlow.Core;

/// <summary>The complete isolation boundary for support data: tenant, project, environment, app and workspace.</summary>
public sealed record TicketScope
{
    private static readonly SearchValues<char> ControlChars = CreateControlChars();

    /// <summary>Maximum identifier length in UTF-16 code units.</summary>
    public const int MaximumIdentifierLength = 256;

    public TicketScope(string tenantId, string projectId, string environmentId, string appId, WorkspaceId workspaceId)
    {
        TenantId = RequireIdentifier(tenantId, nameof(tenantId));
        ProjectId = RequireIdentifier(projectId, nameof(projectId));
        EnvironmentId = RequireIdentifier(environmentId, nameof(environmentId));
        AppId = RequireIdentifier(appId, nameof(appId));
        if (workspaceId == default)
            throw new ArgumentException("A workspace identifier is required.", nameof(workspaceId));
        WorkspaceId = workspaceId;
    }

    public string TenantId { get; }
    public string ProjectId { get; }
    public string EnvironmentId { get; }
    public string AppId { get; }
    public WorkspaceId WorkspaceId { get; }

    public override string ToString() => "TicketScope { <redacted> }";

    /// <summary>
    /// Validates an identifier: not null or blank, at most 256 UTF-16 code units, no C0/C1 control characters and well-formed UTF-16.
    /// Returns the exact input without case or Unicode normalization.
    /// </summary>
    public static string RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var span = value.AsSpan();
        if (span.Length > MaximumIdentifierLength || span.ContainsAny(ControlChars))
            throw new ArgumentException("Identifiers must contain at most 256 characters and no control characters.", parameterName);

        // Serializers replace isolated surrogates, which would collapse distinct identifiers into the same stored/hash identity.
        if (span.ContainsAnyInRange('\uD800', '\uDFFF') && !Utf16.IsValid(span))
            throw new ArgumentException("Identifiers must contain well-formed Unicode.", parameterName);

        return value;
    }

    private static SearchValues<char> CreateControlChars()
    {
        Span<char> chars = stackalloc char[65];
        var index = 0;
        for (var c = '\0'; c <= '\u001F'; c++)
            chars[index++] = c;
        for (var c = '\u007F'; c <= '\u009F'; c++)
            chars[index++] = c;
        return SearchValues.Create(chars);
    }
}
