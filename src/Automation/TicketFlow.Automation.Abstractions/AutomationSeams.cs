using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>A resolved webhook destination: an https endpoint and a 32-64 byte HMAC key. Keys never leave the sender.</summary>
public sealed class AutomationWebhookDestination : IDisposable
{
    private readonly byte[] key;

    public AutomationWebhookDestination(Uri endpoint, ReadOnlySpan<byte> signingKey, string keyId)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (signingKey.Length is < 32 or > 64)
            throw new ArgumentException("Signing keys are 32-64 bytes.", nameof(signingKey));
        key = signingKey.ToArray();
        KeyId = AutomationText.RequireText(keyId, 128, nameof(keyId));
    }

    public Uri Endpoint { get; }
    public string KeyId { get; }
    public ReadOnlySpan<byte> Key => key;

    public void Dispose() => Array.Clear(key);

    public override string ToString() => "AutomationWebhookDestination { <redacted> }";
}

/// <summary>Application-supplied resolution of <see cref="WebhookDestinationId"/>s (endpoints and keys live in the application's secret store).</summary>
public interface IAutomationWebhookDestinationResolver
{
    /// <summary>Returns the destination, or <see langword="null"/> when unknown in <paramref name="scope"/>.</summary>
    ValueTask<AutomationWebhookDestination?> ResolveAsync(TicketScope scope, WebhookDestinationId destination, CancellationToken cancellationToken = default);
}

/// <summary>One webhook delivery: <see cref="DeliveryId"/> is stable across retries of the same action (receivers deduplicate on it).</summary>
public sealed record AutomationWebhookRequest(TicketScope Scope, WebhookDestinationId Destination, string DeliveryId, string Payload)
{
    public override string ToString() => $"AutomationWebhookRequest {{ Destination = {Destination}, DeliveryId = {DeliveryId} }}";
}

public sealed record AutomationWebhookDelivered(int StatusCode);

/// <summary>Delivery failed; <see cref="Code"/> is a token such as <c>endpoint-blocked</c>, <c>destination-missing</c> or <c>http-503</c>.</summary>
public sealed record AutomationWebhookFailed(string Code);

/// <summary>Outcome of a webhook delivery. Switch over every case without a discard arm.</summary>
public union AutomationWebhookResult(AutomationWebhookDelivered, AutomationWebhookFailed);

/// <summary>Sends signed automation webhooks. The default implementation reuses the E6 API channel signature and SSRF guard.</summary>
public interface IAutomationWebhookSender
{
    Task<AutomationWebhookResult> SendAsync(AutomationWebhookRequest request, CancellationToken cancellationToken = default);
}

public sealed record AutomationSurveyTriggered;

public sealed record AutomationSurveyNotTriggered(string Reason);

/// <summary>Outcome of a survey trigger. Switch over every case without a discard arm.</summary>
public union AutomationSurveyTriggerResult(AutomationSurveyTriggered, AutomationSurveyNotTriggered);

/// <summary>
/// Optional survey integration for <see cref="TriggerSurveyAction"/>. Implementations must be idempotent per
/// the idempotency key. Without a registration the action is recorded as skipped.
/// </summary>
public interface IAutomationSurveyTrigger
{
    Task<AutomationSurveyTriggerResult> TriggerAsync(TicketScope scope, ConversationId conversation, ContactId contact, string? survey, string idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional application directory of agent display names for the <c>agent.name</c> template variable.</summary>
public interface IAutomationAgentDirectory
{
    ValueTask<string?> GetDisplayNameAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default);
}
