using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Channels;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Integrations.Surveys.Tests;

[TestClass]
public sealed class ConversationResolvedSurveyHandlerTests
{
    private sealed class RecordingSender : ISurveyInvitationSender
    {
        public List<SurveyInvitationMessage> Sent { get; } = [];

        public Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static async Task<(ChannelHost Host, RecordingSender Sender)> HostAsync()
    {
        var sender = new RecordingSender();
        var host = await ChannelHost.CreateAsync(services =>
        {
            services.AddSingleton<ISurveyInvitationSender>(sender);
            services.AddTicketFlow().AddSurveys().AddInMemorySurveyStores().AddConversationSurveys();
        });
        var store = host.Services.GetRequiredService<ISurveyStore>();
        var question = new SurveyQuestion(new SurveyLocale("en"), "How did we do?");
        Assert.IsInstanceOfType<SurveyWriteApplied<StoredSurvey>>((await store.CreateSurveyAsync(host.Scope, new SurveyDefinition(new SurveyId("csat"), "CSAT", SurveyKind.Csat,
            new SurveyLocale("en"), [question, new SurveyQuestion(new SurveyLocale("pt-BR"), "Como foi?")], new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(7))))).Value);
        return (host, sender);
    }

    private static async Task<ConversationId> ConversationAsync(ChannelHost host, string messageId = "<m1@x>")
    {
        var recorded = await host.Pipeline.ReceiveAsync(host.Worker, host.Email(messageId));
        return ChannelHost.Is<InboundRecorded>(recorded.Value).Conversation;
    }

    [TestMethod]
    public async Task ResolvingAConversationSchedulesOneInvitation()
    {
        var (host, sender) = await HostAsync();
        await using var _ = host;
        var conversation = await ConversationAsync(host);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.AssignAsync(host.Owner, conversation, new UserId("agent"))).Value);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, conversation, ConversationStatus.Pending)).Value);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, conversation, ConversationStatus.Resolved)).Value);
        await host.DispatchAsync();

        var dispatcher = host.Services.GetRequiredService<ISurveyDispatcher>();
        Assert.AreEqual(1, (await dispatcher.DispatchDueAsync(host.Scope)).Sent);
        var message = sender.Sent.Single();
        Assert.AreEqual(conversation, message.Invitation.Conversation);
        Assert.AreEqual(new InboxId("email-inbox"), message.Invitation.Inbox);
        Assert.AreEqual(new UserId("agent"), message.Invitation.Agent);

        // Reopen and resolve again: the per-conversation duplicate check keeps one invitation.
        await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m2@x>", inReplyTo: "<m1@x>"));
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, conversation, ConversationStatus.Resolved)).Value);
        await host.DispatchAsync();
        Assert.AreEqual(0, (await dispatcher.DispatchDueAsync(host.Scope)).Sent);
        Assert.AreEqual(1, sender.Sent.Count);
    }

    [TestMethod]
    public async Task UsesTheContactLocaleAndIgnoresOtherChanges()
    {
        var (host, sender) = await HostAsync();
        await using var _ = host;
        var conversation = await ConversationAsync(host);
        var view = ChannelHost.Is<ConversationView>((await host.Conversations.GetAsync(host.Owner, conversation)).Value);
        var contact = ChannelHost.Is<ContactApplied<ContactView>>((await host.Contacts.GetAsync(host.Owner, view.Contact)).Value).Value;
        Assert.IsInstanceOfType<ContactApplied<ContactView>>((await host.Contacts.UpdateAsync(host.Owner, view.Contact, contact.Revision,
            [new SetContactProfile(new ContactProfile("Ada", locale: "pt-BR"))])).Value);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, conversation, ConversationStatus.Pending)).Value);
        await host.DispatchAsync();
        var dispatcher = host.Services.GetRequiredService<ISurveyDispatcher>();
        Assert.AreEqual(0, (await dispatcher.DispatchDueAsync(host.Scope)).Sent);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, conversation, ConversationStatus.Closed)).Value);
        await host.DispatchAsync();
        Assert.AreEqual(0, (await dispatcher.DispatchDueAsync(host.Scope)).Sent, "Closing without resolving is not surveyed.");

        var second = await ConversationAsync(host, "<m9@x>");
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, second, ConversationStatus.Resolved)).Value);
        await host.DispatchAsync();
        Assert.AreEqual(1, (await dispatcher.DispatchDueAsync(host.Scope)).Sent);
        Assert.AreEqual("pt-BR", sender.Sent.Single().Invitation.Locale.Value);
    }

    [TestMethod]
    public async Task ReplayedEntriesAreIdempotent()
    {
        var (host, _) = await HostAsync();
        await using var __ = host;
        var conversation = await ConversationAsync(host);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, conversation, ConversationStatus.Resolved)).Value);
        var store = host.Services.GetRequiredService<IConversationStore>();
        var timeline = await store.ReadTimelineAsync(host.Scope, conversation, 0, 50, includeNotes: false);
        var status = timeline.Single(item => item.Event.Value is StatusChanged);
        var change = new ConversationChange(Guid.CreateVersion7(), host.Scope, conversation, new InboxId("email-inbox"), 2, ConversationChangeKind.EventsAppended,
            [ConversationEventSummary.From(status)], null, null, host.Clock.GetUtcNow());
        var handler = new ConversationResolvedSurveyHandler(store, host.Services.GetRequiredService<IContactStore>(), host.Services.GetRequiredService<ISurveyScheduler>());
        Assert.AreEqual("surveys.resolved", handler.Consumer);
        await handler.HandleAsync(new ConversationOutboxEntry(1, change));
        await handler.HandleAsync(new ConversationOutboxEntry(1, change));
        var invitations = await host.Services.GetRequiredService<ISurveyStore>().ListDueInvitationsAsync(host.Scope, host.Clock.GetUtcNow(), 10);
        Assert.AreEqual(1, invitations.Count);
    }
}
