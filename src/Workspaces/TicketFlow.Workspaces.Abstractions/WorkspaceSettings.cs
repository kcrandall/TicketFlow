using System.Buffers;
using System.Collections.Frozen;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// Revisioned settings of one workspace (the workspace itself is identified by <see cref="TicketScope.WorkspaceId"/>): display name,
/// default locale (a BCP 47 style tag), default time zone (IANA or Windows identifier) and bounded feature toggles.
/// </summary>
public sealed record WorkspaceSettings
{
    public const int MaximumNameLength = 128;
    public const int MaximumFeatureToggles = 64;
    public const int MaximumFeatureKeyLength = 64;

    public WorkspaceSettings(string name, string defaultLocale, string timeZoneId, IEnumerable<KeyValuePair<string, bool>>? featureToggles = null)
    {
        Name = WorkspaceText.RequireText(name, MaximumNameLength, nameof(name));
        DefaultLocale = WorkspaceLocales.Require(defaultLocale, nameof(defaultLocale));
        TimeZoneId = WorkspaceTimeZones.RequireId(timeZoneId, nameof(timeZoneId));
        FeatureToggles = Snapshot(featureToggles);
    }

    public string Name { get; }
    public string DefaultLocale { get; }

    /// <summary>The time-zone identifier. Format is validated here; services also require it to resolve on the host.</summary>
    public string TimeZoneId { get; }

    /// <summary>Feature toggles keyed by lowercase token. Absent keys are disabled.</summary>
    public IReadOnlyDictionary<string, bool> FeatureToggles { get; }

    public bool IsEnabled(string feature) => feature is not null && FeatureToggles.TryGetValue(feature, out var enabled) && enabled;

    public bool Equals(WorkspaceSettings? other)
    {
        if (other is null || !string.Equals(Name, other.Name, StringComparison.Ordinal) || !string.Equals(DefaultLocale, other.DefaultLocale, StringComparison.Ordinal) || !string.Equals(TimeZoneId, other.TimeZoneId, StringComparison.Ordinal) || FeatureToggles.Count != other.FeatureToggles.Count)
            return false;
        foreach (var pair in FeatureToggles)
            if (!other.FeatureToggles.TryGetValue(pair.Key, out var value) || value != pair.Value)
                return false;
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Name, DefaultLocale, TimeZoneId, FeatureToggles.Count);

    private static FrozenDictionary<string, bool> Snapshot(IEnumerable<KeyValuePair<string, bool>>? toggles)
    {
        if (toggles is null)
            return FrozenDictionary<string, bool>.Empty;
        var snapshot = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in toggles)
        {
            WorkspaceText.RequireToken(key, MaximumFeatureKeyLength, nameof(toggles), "Feature keys");
            if (!snapshot.TryAdd(key, value))
                throw new ArgumentException("Feature keys must be unique.", nameof(toggles));
            if (snapshot.Count > MaximumFeatureToggles)
                throw new ArgumentException($"A workspace can carry at most {MaximumFeatureToggles} feature toggles.", nameof(toggles));
        }
        return snapshot.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

/// <summary>Workspace settings with their scope and revision (first revision is 1).</summary>
public sealed record StoredWorkspaceSettings(TicketScope Scope, WorkspaceSettings Settings, long Revision);

/// <summary>Locale tag validation: <c>xx[-Yyyy][-ZZ]...</c>, a 2-8 letter primary subtag and 1-8 alphanumeric subtags, at most 35 characters.</summary>
public static class WorkspaceLocales
{
    public const int MaximumLength = 35;

    private static readonly SearchValues<char> AsciiLetters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
    private static readonly SearchValues<char> AsciiLettersOrDigits = SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length is 0 or > MaximumLength)
            return false;
        var span = value.AsSpan();
        var first = true;
        foreach (var range in span.Split('-'))
        {
            var segment = span[range];
            if (first)
            {
                if (segment.Length is < 2 or > 8 || segment.ContainsAnyExcept(AsciiLetters))
                    return false;
                first = false;
            }
            else if (segment.Length is < 1 or > 8 || segment.ContainsAnyExcept(AsciiLettersOrDigits))
                return false;
        }
        return !first;
    }

    internal static string Require(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsValid(value))
            throw new ArgumentException("Locales must be BCP 47 style tags such as 'en' or 'pt-BR' of at most 35 characters.", parameterName);
        return value;
    }
}

/// <summary>Time-zone identifier validation and resolution through <see cref="TimeZoneInfo"/> (IANA and Windows identifiers).</summary>
public static class WorkspaceTimeZones
{
    public const int MaximumIdLength = 64;

    private static readonly SearchValues<char> AllowedIdChars = SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz/_+- ");

    /// <summary>True when <paramref name="value"/> has identifier syntax: 1-64 characters of letters, digits, '/', '_', '+' or '-'.</summary>
    public static bool IsValidId(string? value) =>
        value is { Length: > 0 and <= MaximumIdLength } && !value.AsSpan().ContainsAnyExcept(AllowedIdChars);

    /// <summary>Resolves a syntactically valid identifier on this host; returns false when the zone is unknown.</summary>
    public static bool TryResolve(string? value, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (!IsValidId(value))
            return false;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(value!);
            return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    internal static string RequireId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsValidId(value) || value[0] == ' ' || value[^1] == ' ')
            throw new ArgumentException("Time-zone identifiers must be 1-64 characters of letters, digits, '/', '_', '+', '-' or inner spaces.", parameterName);
        return value;
    }
}
