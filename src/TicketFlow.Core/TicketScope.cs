using System.Text.Unicode;

namespace TicketFlow.Core;

/// <summary>The complete isolation boundary for support data: tenant, project, environment, app and workspace.</summary>
public sealed record TicketScope
{
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
        if (value.Length > MaximumIdentifierLength || value.AsSpan().ContainsAnyInRange('\0', '\u001F') || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F'))
            throw new ArgumentException("Identifiers must contain at most 256 characters and no control characters.", parameterName);

        // Serializers replace isolated surrogates, which would collapse distinct identifiers into the same stored/hash identity.
        if (!Utf16.IsValid(value))
            throw new ArgumentException("Identifiers must contain well-formed Unicode.", parameterName);

        return value;
    }
}
