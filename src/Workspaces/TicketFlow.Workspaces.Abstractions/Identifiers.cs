using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// Opaque channel kind such as <c>email</c> or <c>live-chat</c>: 1-64 characters of a-z, 0-9, '-', '_' or '.', starting with a
/// letter. Channel packages (E6) define the kinds they serve; workspaces only store the binding. <c>default</c> is uninitialized.
/// </summary>
public readonly record struct ChannelKind : IComparable<ChannelKind>
{
    public const int MaximumLength = 64;

    private readonly string? value;

    public ChannelKind(string value) => this.value = WorkspaceText.RequireToken(value, MaximumLength, nameof(value), "Channel kinds");

    /// <exception cref="InvalidOperationException">The kind is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The ChannelKind is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(ChannelKind other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Validated opaque identifier of a business calendar, unique within a workspace.</summary>
public readonly record struct BusinessCalendarId
{
    private readonly string? value;

    public BusinessCalendarId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The BusinessCalendarId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Opaque reference to an SLA policy. SLA packages (E9) own the policy; workspaces only store the reference.</summary>
public readonly record struct SlaPolicyId
{
    private readonly string? value;

    public SlaPolicyId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The SlaPolicyId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Opaque reference to routing settings. Routing packages (E8) own the settings; workspaces only store the reference.</summary>
public readonly record struct RoutingPolicyId
{
    private readonly string? value;

    public RoutingPolicyId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The RoutingPolicyId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}
