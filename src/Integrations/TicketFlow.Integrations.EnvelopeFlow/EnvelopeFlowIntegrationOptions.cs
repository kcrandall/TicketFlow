using EnvelopeFlow.Core;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.EnvelopeFlow;

/// <summary>Channel kinds served by the EnvelopeFlow adapter.</summary>
public static class EnvelopeFlowChannelKinds
{
    public static ChannelKind Email { get; } = new("email");
    public static ChannelKind Sms { get; } = new("sms");
    public static ChannelKind WhatsApp { get; } = new("whatsapp");
    public static ChannelKind Slack { get; } = new("slack");
    public static ChannelKind Teams { get; } = new("teams");
}

public sealed class EnvelopeFlowIntegrationOptions
{
    /// <summary>EnvelopeFlow provider names mapped to TicketFlow channel kinds (defaults cover the published providers).</summary>
    public IDictionary<string, ChannelKind> ProviderKinds { get; } = new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
    {
        ["mailgun"] = EnvelopeFlowChannelKinds.Email,
        ["ses"] = EnvelopeFlowChannelKinds.Email,
        ["sendgrid"] = EnvelopeFlowChannelKinds.Email,
        ["postmark"] = EnvelopeFlowChannelKinds.Email,
        ["resend"] = EnvelopeFlowChannelKinds.Email,
        ["azure-email"] = EnvelopeFlowChannelKinds.Email,
        ["twilio"] = EnvelopeFlowChannelKinds.Sms,
        ["telnyx"] = EnvelopeFlowChannelKinds.Sms,
        ["azure-sms"] = EnvelopeFlowChannelKinds.Sms,
        ["whatsapp-meta"] = EnvelopeFlowChannelKinds.WhatsApp,
        ["slack"] = EnvelopeFlowChannelKinds.Slack,
    };

    /// <summary>
    /// Kinds whose sender address the provider authenticates strongly enough to record as a verified identity (default WhatsApp and
    /// Slack). Email senders and SMS numbers can be spoofed and are recorded as claimed.
    /// </summary>
    public ISet<ChannelKind> VerifiedSenderKinds { get; } = new HashSet<ChannelKind> { EnvelopeFlowChannelKinds.WhatsApp, EnvelopeFlowChannelKinds.Slack };

    /// <summary>How long a delivery receipt for a not-yet-correlated message is retried (default 10 minutes after it was observed).</summary>
    public TimeSpan ReceiptCorrelationGrace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Email subject used when the conversation has none (a <c>Re: </c> prefix is added).</summary>
    public string DefaultEmailSubject { get; set; } = "Your conversation";

    /// <summary>Appends <c>[Ticket #&lt;conversationId&gt;]</c> to outbound email subjects and emits <c>X-TicketFlow-Conversation</c> (default true).</summary>
    public bool IncludeTicketTagInEmailSubject { get; set; } = true;

    /// <summary>Quote the customer's last WhatsApp message in replies (default false).</summary>
    public bool QuoteWhatsAppReplies { get; set; }

    internal void Validate()
    {
        if (ReceiptCorrelationGrace < TimeSpan.Zero || ReceiptCorrelationGrace > TimeSpan.FromDays(1) || string.IsNullOrWhiteSpace(DefaultEmailSubject)
            || DefaultEmailSubject.Length > 900 || DefaultEmailSubject.Any(char.IsControl) || ProviderKinds.Values.Any(kind => !kind.IsInitialized))
            throw new InvalidOperationException("EnvelopeFlow integration options are invalid.");
    }
}

/// <summary>Scope mapping between TicketFlow and EnvelopeFlow (EnvelopeFlow scopes have no workspace).</summary>
public static class EnvelopeFlowScopes
{
    public static EnvelopeScope ToEnvelopeScope(TicketScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new EnvelopeScope(scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId);
    }

    public static Access.AccessPartition ToPartition(EnvelopeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new Access.AccessPartition(scope.TenantId, scope.ProjectId, scope.Environment, scope.AppName);
    }
}

/// <summary>
/// Endpoint address conventions (<see cref="Channels.ChannelEndpoint.Address"/>) used to find an endpoint from EnvelopeFlow routing
/// data: <c>{provider}:{providerAccountId}</c>, optionally narrowed to one business number or channel as
/// <c>{provider}:{providerAccountId}:{local}</c> (the event's <c>To</c> for messages). The narrow form is tried first.
/// </summary>
public static class EnvelopeFlowEndpointAddresses
{
    public static string Account(string provider, string providerAccountId) => $"{Require(provider)}:{Require(providerAccountId)}";

    public static string Local(string provider, string providerAccountId, string local) => $"{Account(provider, providerAccountId)}:{Require(local)}";

    private static string Require(string value) => EnvelopeScope.RequireIdentifier(value, nameof(value));
}
