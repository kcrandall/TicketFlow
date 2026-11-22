using System.Text;
using System.Text.Json;
using EnvelopeFlow.Core;
using TicketFlow.Channels;
using TicketFlow.Core;

namespace TicketFlow.Integrations.EnvelopeFlow;

/// <summary>
/// Encodes TicketFlow conversations as EnvelopeFlow WhatsApp/chat destinations. The owner is a <see cref="RecipientBinding"/> for
/// the contact (identity namespace <see cref="IdentityNamespace"/>); the destination id is <c>tfc1.</c> plus base64url JSON of
/// (workspace, configuration id, conversation). Providers re-resolve live credentials through a host delegate, which calls
/// <see cref="EnvelopeFlowDestinationContextResolver"/> to obtain the recipient and last customer message time.
/// </summary>
public static class EnvelopeFlowDestinations
{
    public const string IdentityNamespace = "ticketflow-contact";
    private const string Prefix = "tfc1.";

    public static RecipientBinding Owner(TicketScope scope, ContactId contact) =>
        new(EnvelopeFlowScopes.ToEnvelopeScope(scope), contact.Value, new IdentityReference(IdentityNamespace, contact.Value));

    /// <summary>The destination id, or <see langword="null"/> when the identifiers are too long for an EnvelopeFlow identifier (256).</summary>
    public static string? Encode(TicketScope scope, string configurationId, ConversationId conversation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var json = JsonSerializer.SerializeToUtf8Bytes(new[] { scope.WorkspaceId.Value, configurationId, conversation.Value });
        var value = Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return value.Length <= TicketScope.MaximumIdentifierLength ? value : null;
    }

    /// <summary>Decodes a destination id produced by <see cref="Encode"/> within the EnvelopeFlow scope it was issued for.</summary>
    public static bool TryDecode(EnvelopeScope owner, string destinationId, out TicketScope scope, out string configurationId, out ConversationId conversation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        scope = null!;
        configurationId = null!;
        conversation = default;
        if (destinationId is null || !destinationId.StartsWith(Prefix, StringComparison.Ordinal) || destinationId.Length > TicketScope.MaximumIdentifierLength)
            return false;
        try
        {
            var text = destinationId[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            var parts = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(text)));
            if (parts is not { Length: 3 })
                return false;
            scope = new TicketScope(owner.TenantId, owner.ProjectId, owner.Environment, owner.AppName, new WorkspaceId(parts[0]));
            configurationId = TicketScope.RequireIdentifier(parts[1], nameof(destinationId));
            conversation = new ConversationId(parts[2]);
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>What a host's EnvelopeFlow binding delegate needs to know about a TicketFlow destination. Personal data: never log it.</summary>
public sealed record EnvelopeFlowDestinationContext(TicketScope Scope, ConversationId Conversation, string ConfigurationId, ChannelRecipient Recipient,
    string? ExternalThreadId, DateTimeOffset? LastIncomingAt)
{
    public override string ToString() => $"EnvelopeFlowDestinationContext {{ Conversation = {Conversation} }}";
}

/// <summary>
/// Trusted lookup for host-supplied EnvelopeFlow binding delegates (for example <c>MetaWhatsAppTransport</c>'s binding resolver):
/// decodes the destination and returns the conversation's recorded recipient, thread and last customer message time
/// (<c>LastCustomerMessageAtUtc</c>). Returns <see langword="null"/> for unknown or foreign destinations, or when the contact on the
/// destination no longer owns the conversation.
/// </summary>
public sealed class EnvelopeFlowDestinationContextResolver(IChannelConversationStateStore states, Conversations.IConversationStore conversations)
{
    private readonly IChannelConversationStateStore states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly Conversations.IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));

    public async Task<EnvelopeFlowDestinationContext?> ResolveAsync(RecipientBinding owner, string destinationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!EnvelopeFlowDestinations.TryDecode(owner.Scope, destinationId, out var scope, out var configurationId, out var conversation)
            || owner.Identity.IdentityNamespace != EnvelopeFlowDestinations.IdentityNamespace)
            return null;
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Conversation.Contact.Value != owner.RecipientId)
            return null;
        var state = await states.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (state is null)
            return null;
        return new EnvelopeFlowDestinationContext(scope, conversation, configurationId, state.State.Recipient, state.State.ExternalThreadId,
            state.State.LastIncomingAt ?? stored.Conversation.LastIncomingAt);
    }
}
