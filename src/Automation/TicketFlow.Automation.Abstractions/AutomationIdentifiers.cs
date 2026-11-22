using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>Validated opaque identifier of an automation rule. <c>default</c> is uninitialized and rejected where an identifier is required.</summary>
public readonly record struct AutomationRuleId
{
    private readonly string? value;

    public AutomationRuleId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    public string Value => value ?? throw new InvalidOperationException("The AutomationRuleId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Validated opaque identifier of a macro. <c>default</c> is uninitialized.</summary>
public readonly record struct MacroId
{
    private readonly string? value;

    public MacroId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    public string Value => value ?? throw new InvalidOperationException("The MacroId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Validated opaque identifier of a canned response. <c>default</c> is uninitialized.</summary>
public readonly record struct CannedResponseId
{
    private readonly string? value;

    public CannedResponseId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    public string Value => value ?? throw new InvalidOperationException("The CannedResponseId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Opaque identifier of an application-registered webhook destination. The application resolves it to an endpoint and signing key
/// (<see cref="IAutomationWebhookDestinationResolver"/>); rules never store URLs or secrets.
/// </summary>
public readonly record struct WebhookDestinationId
{
    private readonly string? value;

    public WebhookDestinationId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    public string Value => value ?? throw new InvalidOperationException("The WebhookDestinationId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}
