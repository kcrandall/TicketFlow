using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Channels.LiveChat;

public enum PrechatFieldKind
{
    /// <summary>The visitor's name; becomes the contact display name.</summary>
    Name = 1,

    /// <summary>An email address (validated and normalized; stored on the session, masked for agents like contact emails).</summary>
    Email = 2,

    /// <summary>Free text (single line).</summary>
    Text = 3,
}

/// <summary>A pre-chat form field. <see cref="Sensitive"/> answers are masked for agents unless an Access field policy unmasks <c>contact.attribute.&lt;key&gt;</c>.</summary>
public sealed record LiveChatPrechatField
{
    public const int MaximumKeyLength = 64;
    public const int MaximumLabelLength = 128;
    public const int MaximumAnswerLength = 512;

    public LiveChatPrechatField(string key, string label, PrechatFieldKind kind, bool required = false, int maximumLength = 256, bool sensitive = false)
    {
        Key = LiveChatText.RequireToken(key, MaximumKeyLength, nameof(key));
        Label = LiveChatText.RequireLine(label, MaximumLabelLength, nameof(label));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (maximumLength is < 1 or > MaximumAnswerLength)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        Kind = kind;
        Required = required;
        MaximumLength = maximumLength;
        Sensitive = sensitive;
    }

    public string Key { get; }
    public string Label { get; }
    public PrechatFieldKind Kind { get; }
    public bool Required { get; }
    public int MaximumLength { get; }
    public bool Sensitive { get; }
}

/// <summary>Help-center deflection: before a first message the widget searches <see cref="Portal"/> (public portals only).</summary>
public sealed record LiveChatDeflection
{
    public LiveChatDeflection(PortalId portal, HelpCenterLocale locale, int maximumResults = 5)
    {
        if (!portal.IsInitialized || !locale.IsInitialized)
            throw new ArgumentException("A portal and locale are required.", !portal.IsInitialized ? nameof(portal) : nameof(locale));
        if (maximumResults is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        Portal = portal;
        Locale = locale;
        MaximumResults = maximumResults;
    }

    public PortalId Portal { get; }
    public HelpCenterLocale Locale { get; }
    public int MaximumResults { get; }
}

/// <summary>Visitor attachment limits: at most <see cref="MaximumBytes"/> per file and only the listed media types (exact, lowercase).</summary>
public sealed record LiveChatAttachmentPolicy
{
    public const long MaximumAllowedBytes = 100L * 1024 * 1024;
    public const int MaximumMediaTypes = 32;

    public LiveChatAttachmentPolicy(long maximumBytes, IEnumerable<string> allowedMediaTypes)
    {
        if (maximumBytes is < 1 or > MaximumAllowedBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ArgumentNullException.ThrowIfNull(allowedMediaTypes);
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in allowedMediaTypes)
        {
            if (!IsMediaType(type))
                throw new ArgumentException("Media types must be lowercase 'type/subtype' tokens.", nameof(allowedMediaTypes));
            types.Add(type);
        }
        if (types.Count is 0 or > MaximumMediaTypes)
            throw new ArgumentException($"1-{MaximumMediaTypes} media types are required.", nameof(allowedMediaTypes));
        MaximumBytes = maximumBytes;
        AllowedMediaTypes = [.. types];
    }

    /// <summary>10 MiB of PNG, JPEG, GIF, WebP, PDF or plain text.</summary>
    public static LiveChatAttachmentPolicy Default { get; } = new(10 * 1024 * 1024, ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain"]);

    public long MaximumBytes { get; }
    public IReadOnlyList<string> AllowedMediaTypes { get; }

    public bool Allows(string mediaType) => AllowedMediaTypes.Contains(mediaType, StringComparer.Ordinal);

    public bool Equals(LiveChatAttachmentPolicy? other) => other is not null && MaximumBytes == other.MaximumBytes && AllowedMediaTypes.SequenceEqual(other.AllowedMediaTypes, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(MaximumBytes, AllowedMediaTypes.Count);

    internal static bool IsMediaType(string? value)
    {
        if (value is null || value.Length is < 3 or > 127)
            return false;
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0)
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '/' or '-' or '+' or '.'))
                return false;
        return true;
    }
}

/// <summary>
/// How visitors may prove who they are. <see cref="Namespace"/> is the contact identity namespace of verified <c>ExternalUser</c>
/// identities (HMAC verification; signed-token validators supply their own). <see cref="RequireVerified"/> refuses messages from
/// unverified visitors.
/// </summary>
public sealed record LiveChatIdentityPolicy
{
    public LiveChatIdentityPolicy(string @namespace = "app", bool allowHmac = true, bool allowSignedToken = true, bool requireVerified = false)
    {
        Namespace = LiveChatText.RequireToken(@namespace, 64, nameof(@namespace));
        AllowHmac = allowHmac;
        AllowSignedToken = allowSignedToken;
        RequireVerified = requireVerified;
    }

    public static LiveChatIdentityPolicy Default { get; } = new();

    public string Namespace { get; }
    public bool AllowHmac { get; }
    public bool AllowSignedToken { get; }
    public bool RequireVerified { get; }
}

/// <summary>
/// A live-chat widget configuration: the inbox's channel configuration (<see cref="ConfigurationId"/> equals the inbox
/// <c>ChannelBinding.ConfigurationId</c>), allowed embedding origins, an opaque theme reference resolved by the host/widget, pre-chat
/// fields, optional greeting and away-message overrides (else the inbox's), deflection, attachment policy and identity policy.
/// </summary>
public sealed record LiveChatWidget
{
    public const int MaximumOrigins = 32;
    public const int MaximumPrechatFields = 16;
    public const int MaximumThemeRefLength = 256;
    public const int MaximumMessageLength = 1024;

    public LiveChatWidget(
        string configurationId,
        InboxId inbox,
        IEnumerable<string> allowedOrigins,
        string? themeRef = null,
        IEnumerable<LiveChatPrechatField>? prechatFields = null,
        string? greetingMessage = null,
        string? awayMessage = null,
        LiveChatDeflection? deflection = null,
        LiveChatAttachmentPolicy? attachments = null,
        LiveChatIdentityPolicy? identity = null)
    {
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
        Inbox = inbox == default ? throw new ArgumentException("An inbox identifier is required.", nameof(inbox)) : inbox;
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var origin in allowedOrigins)
            origins.Add(LiveChatOrigin.Normalize(origin) ?? throw new ArgumentException("Origins must be absolute http(s) origins without path, query or user info.", nameof(allowedOrigins)));
        if (origins.Count is 0 or > MaximumOrigins)
            throw new ArgumentException($"1-{MaximumOrigins} allowed origins are required.", nameof(allowedOrigins));
        AllowedOrigins = [.. origins];
        ThemeRef = LiveChatText.OptionalLine(themeRef, MaximumThemeRefLength, nameof(themeRef));
        PrechatFields = [.. prechatFields ?? []];
        if (PrechatFields.Count > MaximumPrechatFields || PrechatFields.Any(field => field is null)
            || PrechatFields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != PrechatFields.Count
            || PrechatFields.Count(field => field.Kind == PrechatFieldKind.Name) > 1 || PrechatFields.Count(field => field.Kind == PrechatFieldKind.Email) > 1)
            throw new ArgumentException($"At most {MaximumPrechatFields} uniquely keyed fields, with at most one name and one email field.", nameof(prechatFields));
        GreetingMessage = greetingMessage is null ? null : LiveChatText.RequireMessage(greetingMessage, MaximumMessageLength, nameof(greetingMessage));
        AwayMessage = awayMessage is null ? null : LiveChatText.RequireMessage(awayMessage, MaximumMessageLength, nameof(awayMessage));
        Deflection = deflection;
        Attachments = attachments;
        Identity = identity ?? LiveChatIdentityPolicy.Default;
    }

    public string ConfigurationId { get; }
    public InboxId Inbox { get; }

    /// <summary>Normalized origins (<c>scheme://host[:port]</c>, default ports omitted), ordinal order.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; }

    public string? ThemeRef { get; }
    public IReadOnlyList<LiveChatPrechatField> PrechatFields { get; }
    public string? GreetingMessage { get; }
    public string? AwayMessage { get; }
    public LiveChatDeflection? Deflection { get; }

    /// <summary>Visitor uploads; <see langword="null"/> disables attachments.</summary>
    public LiveChatAttachmentPolicy? Attachments { get; }

    public LiveChatIdentityPolicy Identity { get; }

    public bool Equals(LiveChatWidget? other) =>
        other is not null && string.Equals(ConfigurationId, other.ConfigurationId, StringComparison.Ordinal) && Inbox == other.Inbox && AllowedOrigins.SequenceEqual(other.AllowedOrigins, StringComparer.Ordinal) && string.Equals(ThemeRef, other.ThemeRef, StringComparison.Ordinal) && PrechatFields.SequenceEqual(other.PrechatFields) && string.Equals(GreetingMessage, other.GreetingMessage, StringComparison.Ordinal) && string.Equals(AwayMessage, other.AwayMessage, StringComparison.Ordinal) && Deflection == other.Deflection
        && Equals(Attachments, other.Attachments) && Identity == other.Identity;

    public override int GetHashCode() => HashCode.Combine(ConfigurationId, Inbox, AllowedOrigins.Count, PrechatFields.Count);

    public override string ToString() => $"LiveChatWidget {{ ConfigurationId = {ConfigurationId} }}";
}

/// <summary>A widget configuration with its scope and revision (first revision is 1).</summary>
public sealed record StoredLiveChatWidget(TicketScope Scope, LiveChatWidget Widget, long Revision);

/// <summary>Origin normalization and exact matching (scheme, host and port).</summary>
public static class LiveChatOrigin
{
    /// <summary>
    /// <c>scheme://host[:port]</c> lowercase with default ports omitted, or <see langword="null"/> when <paramref name="origin"/> is not
    /// an absolute http/https origin without user info, path (other than "/"), query or fragment.
    /// </summary>
    public static string? Normalize(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin.Length > 512 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) || origin.Contains('@') || string.IsNullOrEmpty(uri.IdnHost))
            return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>True when <paramref name="origin"/> normalizes to one of the widget's allowed origins.</summary>
    public static bool IsAllowed(LiveChatWidget widget, string? origin)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return Normalize(origin) is { } normalized && widget.AllowedOrigins.Contains(normalized, StringComparer.Ordinal);
    }
}
