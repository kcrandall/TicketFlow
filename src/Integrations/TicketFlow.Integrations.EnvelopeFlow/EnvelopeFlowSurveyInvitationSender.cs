using Microsoft.Extensions.Options;
using TicketFlow.Channels;
using TicketFlow.Conversations;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.EnvelopeFlow;

public sealed class EnvelopeFlowSurveyOptions
{
    /// <summary>Builds the respondent link that embeds the raw token (required; for example <c>https://support.example.com/s/{token}</c>).</summary>
    public Func<SurveyInvitationMessage, Uri>? LinkFactory { get; set; }

    /// <summary>Renders the free-text invitation (default: the localized question, a blank line and the link).</summary>
    public Func<SurveyInvitationMessage, Uri, string> TextFormatter { get; set; } = (message, link) => $"{message.Question.Text}\n\n{link}";

    /// <summary>Approved WhatsApp template (name, locale) used outside the 24-hour window; its single body parameter is the link.</summary>
    public (string Name, string Locale)? WhatsAppTemplate { get; set; }
}

/// <summary>
/// Sends survey invitations (<see cref="ISurveyInvitationSender"/>) through the EnvelopeFlow channel of the surveyed conversation, to
/// the recipient the channel layer recorded. WhatsApp invitations outside the 24-hour window use
/// <see cref="EnvelopeFlowSurveyOptions.WhatsAppTemplate"/> (failing when none is configured). Throws on rejections so the survey
/// dispatcher retries; an unknown outcome is not thrown (the invitation may have been delivered; survey dispatch is at-most-once).
/// Never logs the token, link or address.
/// </summary>
public sealed class EnvelopeFlowSurveyInvitationSender(
    IChannelConversationStateStore states,
    IConversationStore conversations,
    IInboxStore inboxes,
    IChannelRegistry registry,
    IOptions<EnvelopeFlowSurveyOptions> options,
    TimeProvider time) : ISurveyInvitationSender
{
    private readonly IChannelConversationStateStore states = states ?? throw new ArgumentNullException(nameof(states));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IInboxStore inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
    private readonly IChannelRegistry registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly EnvelopeFlowSurveyOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    public async Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var linkFactory = options.LinkFactory ?? throw new InvalidOperationException("EnvelopeFlowSurveyOptions.LinkFactory must be configured.");
        var invitation = message.Invitation;
        var stored = await conversations.GetAsync(message.Scope, invitation.Conversation, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The surveyed conversation no longer exists.");
        var state = await states.GetAsync(message.Scope, invitation.Conversation, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The surveyed conversation has no channel recipient.");
        var inbox = await inboxes.GetAsync(message.Scope, stored.Conversation.Inbox, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The surveyed conversation's inbox no longer exists.");
        if (registry.Find(inbox.Inbox.Channel.Kind) is not IEnvelopeFlowChannel channel)
            throw new InvalidOperationException("The surveyed conversation is not on an EnvelopeFlow channel.");

        var link = linkFactory(message);
        var lastIncoming = state.State.LastIncomingAt ?? stored.Conversation.LastIncomingAt;
        ChannelTemplate? template = null;
        if (ChannelReplyWindow.Evaluate(channel.Capabilities, lastIncoming, time.GetUtcNow()) is { Value: ReplyWindowClosed })
        {
            if (!channel.Capabilities.Has(ChannelFeatures.Templates) || options.WhatsAppTemplate is not { } approved)
                throw new InvalidOperationException("The reply window is closed and no approved survey template is configured.");
            template = new ChannelTemplate(approved.Name, approved.Locale, [link.ToString()]);
        }
        var request = new OutboundChannelMessage(message.Scope, stored.Conversation.Inbox, inbox.Inbox.Channel, stored.Id, null, stored.Conversation.Contact,
            state.State.Recipient, options.TextFormatter(message, link), MessageContentType.PlainText,
            $"tfs-{invitation.Id.Value:N}-{invitation.Attempts}",
            thread: new ChannelThreadContext(state.State.ExternalThreadId, subject: state.State.Subject), lastIncomingAt: lastIncoming, template: template);
        switch (await channel.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            case ChannelSendAccepted:
            case ChannelSendUnknown:
                return;
            case ChannelSendRejected rejected:
                throw new InvalidOperationException($"The survey invitation was rejected ({rejected.Code}).");
        }
    }
}
