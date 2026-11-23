using System.Buffers;
using System.Buffers.Text;
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
        var buffer = new ArrayBufferWriter<byte>(192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(scope.WorkspaceId.Value);
            writer.WriteStringValue(configurationId);
            writer.WriteStringValue(conversation.Value);
            writer.WriteEndArray();
        }
        var encodedLen = Base64Url.GetEncodedLength(buffer.WrittenCount);
        if (Prefix.Length + encodedLen > TicketScope.MaximumIdentifierLength)
            return null;
        Span<char> chars = stackalloc char[Prefix.Length + encodedLen];
        Prefix.AsSpan().CopyTo(chars);
        Base64Url.EncodeToChars(buffer.WrittenSpan, chars[Prefix.Length..]);
        return new string(chars);
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
            var encoded = destinationId.AsSpan(Prefix.Length);
            Span<byte> utf8 = stackalloc byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
            if (Base64Url.DecodeFromChars(encoded, utf8, out _, out var written) != OperationStatus.Done)
                return false;
            var reader = new Utf8JsonReader(utf8[..written]);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return false;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.GetString() is not { } ws)
                return false;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.GetString() is not { } cfg)
                return false;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.GetString() is not { } conv)
                return false;
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                return false;
            scope = new TicketScope(owner.TenantId, owner.ProjectId, owner.Environment, owner.AppName, new WorkspaceId(ws));
            configurationId = TicketScope.RequireIdentifier(cfg, nameof(destinationId));
            conversation = new ConversationId(conv);
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
        var storedTask = conversations.GetAsync(scope, conversation, cancellationToken);
        var stateTask = states.GetAsync(scope, conversation, cancellationToken);
        await Task.WhenAll(storedTask, stateTask).ConfigureAwait(false);
        var stored = storedTask.Result;
        if (stored is null || stored.Conversation.Contact.Value != owner.RecipientId)
            return null;
        var state = stateTask.Result;
        if (state is null)
            return null;
        return new EnvelopeFlowDestinationContext(scope, conversation, configurationId, state.State.Recipient, state.State.ExternalThreadId,
            state.State.LastIncomingAt ?? stored.Conversation.LastIncomingAt);
    }
}
