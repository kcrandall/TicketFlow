using EnvelopeFlow.Chat;
using EnvelopeFlow.Core;
using EnvelopeFlow.Email;
using EnvelopeFlow.Inbound;
using EnvelopeFlow.Sms;
using EnvelopeFlow.WhatsApp;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Channels;
using TicketFlow.Channels.Testing;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;
using TicketFlow.Integrations.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.EnvelopeFlow.Tests;

/// <summary>A channel host with EnvelopeFlow channels over fake transports and endpoints addressed by provider and account.</summary>
internal sealed class EnvelopeFlowFixture : IAsyncDisposable
{
    private EnvelopeFlowFixture(ChannelHost host, FakeEmailTransport email, FakeSmsTransport sms, FakeChatTransport chat, WhatsAppHolder whatsApp)
    {
        Host = host;
        Email = email;
        Sms = sms;
        Chat = chat;
        whatsAppHolder = whatsApp;
    }

    private readonly WhatsAppHolder whatsAppHolder;

    public ChannelHost Host { get; }
    public FakeEmailTransport Email { get; }
    public FakeSmsTransport Sms { get; }
    public FakeChatTransport Chat { get; }
    public FakeWhatsAppTransport WhatsApp => whatsAppHolder.Transport!;
    public EnvelopeScope EnvelopeScope => EnvelopeFlowScopes.ToEnvelopeScope(Host.Scope);
    public IInboundEventHandler Handler => Host.Services.GetRequiredService<IInboundEventHandler>();

    private sealed class WhatsAppHolder
    {
        public FakeWhatsAppTransport? Transport { get; set; }
    }

    public static async Task<EnvelopeFlowFixture> CreateAsync(Action<EnvelopeFlowSurveyOptions>? surveys = null)
    {
        var email = new FakeEmailTransport();
        var sms = new FakeSmsTransport();
        var chat = new FakeChatTransport();
        var holder = new WhatsAppHolder();
        var host = await ChannelHost.CreateAsync(services =>
        {
            services.AddSingleton<IEmailTransport>(email);
            services.AddSingleton<ISmsTransport>(sms);
            services.AddSingleton<IChatTransport>(chat);
            services.AddSingleton<IWhatsAppTransport>(provider => holder.Transport = new FakeWhatsAppTransport(async (destination, cancellationToken) =>
            {
                using var scope = provider.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<EnvelopeFlowDestinationContextResolver>().ResolveAsync(destination.Owner, destination.DestinationId, cancellationToken);
            }, provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IEnvelopeFlowSenderDirectory, Senders>();
            var builder = services.AddTicketFlow()
                .AddEnvelopeFlowIntegration()
                .AddEnvelopeFlowEmailChannel()
                .AddEnvelopeFlowSmsChannel()
                .AddEnvelopeFlowWhatsAppChannel()
                .AddEnvelopeFlowSlackChannel();
            if (surveys is not null)
                builder.AddSurveys().AddInMemorySurveyStores().AddConversationSurveys().AddEnvelopeFlowSurveySender(surveys);
        });
        var fixture = new EnvelopeFlowFixture(host, email, sms, chat, holder);
        await fixture.AddressAsync("email", "cfg-email", "mailgun:acct");
        await fixture.AddressAsync("sms", "cfg-sms", "twilio:AC1:+19995550000");
        await fixture.AddressAsync("whatsapp", "cfg-wa", "whatsapp-meta:waba-1");
        var inboxes = host.Services.GetRequiredService<IInboxStore>();
        await inboxes.CreateAsync(host.Scope, new Inbox(new InboxId("slack-inbox"), "Slack", new ChannelBinding(new ChannelKind("slack"), "cfg-slack")));
        Assert.IsInstanceOfType<ChannelValue<StoredChannelEndpoint>>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(new ChannelKind("slack"), "cfg-slack", new InboxId("slack-inbox"), "slack:T1"))).Value);
        return fixture;
    }

    private async Task AddressAsync(string kind, string configuration, string address)
    {
        var current = ChannelHost.Is<ChannelValue<StoredChannelEndpoint>>((await Host.Endpoints.GetAsync(Host.Owner, new ChannelKind(kind), configuration)).Value).Value;
        Assert.IsInstanceOfType<ChannelValue<StoredChannelEndpoint>>((await Host.Endpoints.UpdateAsync(Host.Owner,
            new ChannelEndpoint(current.Endpoint.Kind, configuration, current.Endpoint.Inbox, address), current.Revision)).Value);
    }

    public InboundEvent Message(string provider, string account, string providerMessageId, string from, string? to = null, string? text = "Hello", string? inReplyTo = null,
        string? subject = null, string? eventId = null, IReadOnlyList<InboundAttachment>? attachments = null, EnvelopeScope? scope = null) => new()
    {
        Scope = scope ?? EnvelopeScope,
        Provider = provider,
        ProviderAccountId = account,
        EventId = eventId ?? providerMessageId,
        ProviderMessageId = providerMessageId,
        Kind = InboundEventKind.Message,
        ObservedAt = Host.Clock.GetUtcNow(),
        From = from,
        To = to,
        Text = text,
        Subject = subject,
        InReplyTo = inReplyTo,
        MediaCount = attachments?.Count ?? 0,
        Attachments = attachments,
    };

    public InboundEvent Receipt(string provider, string account, string providerMessageId, InboundDeliveryState state, string? errorCode = null, DateTimeOffset? observedAt = null, string? from = null) => new()
    {
        Scope = EnvelopeScope,
        Provider = provider,
        ProviderAccountId = account,
        EventId = $"{providerMessageId}:{state}",
        ProviderMessageId = providerMessageId,
        Kind = InboundEventKind.DeliveryReceipt,
        ObservedAt = observedAt ?? Host.Clock.GetUtcNow(),
        DeliveryState = state,
        ProviderErrorCode = errorCode,
        From = from,
    };

    public async Task<MessageId> ReplyAsync(ConversationId conversation, string body = "On it")
    {
        var reply = ChannelHost.Is<ConversationApplied>((await Host.Replies.ReplyAsync(Host.Agent, conversation, new OutgoingMessageDraft(body))).Value);
        return reply.Events.Select(item => item.Event.Value).OfType<MessageEvent>().Single().Id;
    }

    public async Task<IReadOnlyList<MessageEvent>> MessagesAsync(ConversationId conversation)
    {
        var page = ChannelHost.Is<TimelinePage>((await Host.Conversations.ReadTimelineAsync(Host.Owner, conversation, 0, 200)).Value);
        return [.. page.Events.Select(item => item.Event.Event.Value).OfType<MessageEvent>()];
    }

    public async Task<ConversationId> SingleConversationAsync(string inbox)
    {
        var page = ChannelHost.Is<ConversationListPage>((await Host.Conversations.ListAsync(Host.Owner, new ConversationListQuery(inbox: new InboxId(inbox)))).Value);
        return page.Conversations.Single().Id;
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
