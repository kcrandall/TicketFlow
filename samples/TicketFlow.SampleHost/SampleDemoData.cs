using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Channels;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.SampleHost;

/// <summary>
/// Realistic demo content for the support UI: inboxes across channels, teams, staff, contacts with masked fields, two weeks of
/// conversation history (so reports and charts have data), current conversations with timelines, SLA at-risk and breached items,
/// automation rules, macros, canned responses, help-center articles and CSAT responses. History is written through the authorized
/// services with the <see cref="SampleClock"/> moved into the past; the outbox is then drained so SLA, routing, automation, reporting
/// and survey consumers process everything. Runs once per workspace (skipped when the demo contacts exist).
/// </summary>
internal static class SampleDemoData
{
    private static readonly InboxId Chat = new("web-chat");
    private static readonly InboxId Email = new("email");
    private static readonly InboxId WhatsApp = new("whatsapp");
    private static readonly InboxId Billing = new("billing");
    private static readonly TeamId Tier1 = new("tier1");
    private static readonly TeamId BillingTeam = new("billing");
    private static readonly TeamId Escalations = new("escalations");

    public static async Task SeedAsync(IServiceProvider services, SampleSettings settings, CancellationToken cancellationToken)
    {
        var scope = settings.Scope;
        var admin = new UserPrincipal(scope, new UserId("admin"));
        var contacts = services.GetRequiredService<IContactService>();
        if (await contacts.GetAsync(admin, new ContactId("c-olivia"), cancellationToken) is ContactApplied<ContactView>)
            return;
        var clock = services.GetRequiredService<SampleClock>();
        var now = clock.GetUtcNow();
        try
        {
            await ProvisionAsync(services, settings, admin, cancellationToken);
            var contactIds = await ContactsAsync(contacts, admin, now, cancellationToken);
            var conversations = services.GetRequiredService<IConversationService>();
            await HistoryAsync(conversations, clock, scope, admin, contactIds, now, cancellationToken);
            await CurrentAsync(conversations, clock, scope, admin, now, cancellationToken);
        }
        finally
        {
            clock.Offset = TimeSpan.Zero;
        }
        await DrainAsync(services, settings, cancellationToken);
        await AnswerSurveysAsync(services, settings, cancellationToken);
        var availability = services.GetRequiredService<IAgentAvailabilityService>();
        var profiles = new (string Agent, AgentStatus Status, int Capacity, (string Skill, int Level)[] Skills)[]
        {
            ("agent1", AgentStatus.Online, 6, [("billing", 2), ("english", 5), ("api", 3)]),
            ("agent2", AgentStatus.Online, 5, [("english", 5), ("italian", 4), ("french", 3)]),
            ("agent3", AgentStatus.Away, 4, [("billing", 5), ("refunds", 4)]),
            ("supervisor", AgentStatus.Busy, 3, [("escalations", 5), ("api", 4)]),
        };
        foreach (var (agent, status, capacity, skills) in profiles)
        {
            await availability.ConfigureAsync(admin, new AgentRoutingSettings(new UserId(agent), capacity, null, skills.Select(skill => new AgentSkill(skill.Skill, skill.Level))), null, cancellationToken);
            await availability.SetStatusAsync(admin, new UserId(agent), status, cancellationToken);
        }
    }

    private static async Task ProvisionAsync(IServiceProvider services, SampleSettings settings, UserPrincipal admin, CancellationToken cancellationToken)
    {
        var scope = settings.Scope;
        var partition = settings.Partition;
        var grants = services.GetRequiredService<IAccessGrantStore>();
        var roles = services.GetRequiredService<IAccessRoleStore>();
        await roles.CreateAsync(scope, new AccessRole(new RoleId("billing-specialist"), "Billing specialist",
            [AccessPermissions.ConversationRead, AccessPermissions.ConversationReply, AccessPermissions.ConversationResolve, AccessPermissions.NoteRead,
                AccessPermissions.NoteWrite, AccessPermissions.ContactRead, AccessPermissions.ContactUpdate, AccessPermissions.ReportView, AccessPermissions.InboxRead,
                AccessPermissions.TeamRead, AccessPermissions.WorkspaceRead]), cancellationToken);
        async Task GrantAsync(string id, UserId user, RoleId role, GrantScope? target = null) =>
            await grants.CreateAsync(new AccessGrant(new GrantId("seed-" + id), partition, user, role, target ?? new WorkspaceGrantScope(scope.WorkspaceId)), cancellationToken);
        await GrantAsync("agent3", new UserId("agent3"), BuiltInRoles.Agent.Id);
        await GrantAsync("agent3-billing", new UserId("agent3"), new RoleId("billing-specialist"), new InboxGrantScope(scope.WorkspaceId, Billing));
        await GrantAsync("supervisor", new UserId("supervisor"), BuiltInRoles.Supervisor.Id);
        await GrantAsync("viewer", new UserId("viewer"), BuiltInRoles.Viewer.Id);
        // Phone numbers are masked for everyone except supervisors; holders of contact:reveal can reveal them (audited).
        await services.GetRequiredService<IAccessFieldPolicyStore>().CreateAsync(scope,
            new AccessFieldPolicy(ContactFieldPaths.Phone, [AccessPermissions.ContactRead], [BuiltInRoles.Supervisor.Id], AccessPermissions.ContactReveal), cancellationToken);

        var calendars = services.GetRequiredService<IBusinessCalendarService>();
        await calendars.CreateAsync(admin, BusinessCalendarCodec.DecodeSchedule(new BusinessCalendarId("support-hours"), "Support hours (Mon–Fri)", "America/New_York",
            """{"v":1,"week":[{"d":1,"s":480,"e":1140},{"d":2,"s":480,"e":1140},{"d":3,"s":480,"e":1140},{"d":4,"s":480,"e":1140},{"d":5,"s":480,"e":1080}],"overrides":[{"date":"2026-12-25","name":"Christmas Day","hours":[]},{"date":"2027-01-01","name":"New Year","hours":[]}]}"""),
            cancellationToken);
        await calendars.CreateAsync(admin, BusinessCalendarCodec.DecodeSchedule(new BusinessCalendarId("always-on"), "24/7 coverage", "UTC",
            """{"v":1,"week":[{"d":0,"s":0,"e":1440},{"d":1,"s":0,"e":1440},{"d":2,"s":0,"e":1440},{"d":3,"s":0,"e":1440},{"d":4,"s":0,"e":1440},{"d":5,"s":0,"e":1440},{"d":6,"s":0,"e":1440}],"overrides":[]}"""),
            cancellationToken);

        var slaPolicies = services.GetRequiredService<ISlaPolicyService>();
        await slaPolicies.CreateAsync(admin, new SlaPolicy(new SlaPolicyId("standard"), "Standard support",
        [
            new SlaRule("Urgent & high", new SlaCondition([ConversationPriority.Urgent, ConversationPriority.High]),
                new SlaTargets(TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(8)), SlaClockBasis.CalendarTime),
            new SlaRule("Everything else", new SlaCondition(), new SlaTargets(TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(48)), SlaClockBasis.CalendarTime),
        ]), cancellationToken);
        await slaPolicies.CreateAsync(admin, new SlaPolicy(new SlaPolicyId("billing"), "Billing (business hours)",
        [
            new SlaRule("All billing", new SlaCondition(), new SlaTargets(TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromDays(3)), SlaClockBasis.CalendarTime),
        ], atRiskPercent: 70), cancellationToken);
        await services.GetRequiredService<IRoutingPolicyService>().CreateAsync(admin, new RoutingPolicy(new RoutingPolicyId("chat-round-robin"), "Chat round robin",
            RoutingStrategy.RoundRobin, Tier1, maximumWait: TimeSpan.FromMinutes(5)), cancellationToken);
        await services.GetRequiredService<IRoutingPolicyService>().CreateAsync(admin, new RoutingPolicy(new RoutingPolicyId("email-least-busy"), "Email least busy",
            RoutingStrategy.LeastBusy, Tier1), cancellationToken);

        var inboxes = services.GetRequiredService<IInboxStore>();
        if (await inboxes.GetAsync(scope, Chat, cancellationToken) is { } chat)
            await inboxes.UpdateAsync(scope, new Inbox(Chat, "Website chat", new ChannelBinding(new ChannelKind("livechat"), "cfg-web"), new BusinessCalendarId("always-on"),
                new SlaPolicyId("standard"), new RoutingPolicyId("chat-round-robin"), greetingMessage: "Hi! How can we help today?"), chat.Revision, cancellationToken);
        await inboxes.CreateAsync(scope, new Inbox(Email, "Email support", new ChannelBinding(new ChannelKind("email"), "support-mailbox"), new BusinessCalendarId("support-hours"),
            new SlaPolicyId("standard"), awayMessage: "Thanks for writing in! Our team is offline and will reply during business hours."), cancellationToken);
        await inboxes.CreateAsync(scope, new Inbox(WhatsApp, "WhatsApp", new ChannelBinding(new ChannelKind("whatsapp"), "wa-main"), new BusinessCalendarId("always-on"),
            new SlaPolicyId("standard")), cancellationToken);
        await inboxes.CreateAsync(scope, new Inbox(Billing, "Billing", new ChannelBinding(new ChannelKind("email"), "billing-mailbox"), new BusinessCalendarId("support-hours"),
            new SlaPolicyId("billing")), cancellationToken);
        var endpoints = services.GetRequiredService<IChannelEndpointStore>();
        await endpoints.CreateAsync(scope, new ChannelEndpoint(new ChannelKind("email"), "support-mailbox", Email, "support@acme.test"), cancellationToken);
        await endpoints.CreateAsync(scope, new ChannelEndpoint(new ChannelKind("whatsapp"), "wa-main", WhatsApp, "+1 555 0100"), cancellationToken);
        await endpoints.CreateAsync(scope, new ChannelEndpoint(new ChannelKind("email"), "billing-mailbox", Billing, "billing@acme.test"), cancellationToken);

        var teams = services.GetRequiredService<ITeamStore>();
        await teams.CreateAsync(scope, new Team(BillingTeam, "Billing", "Invoices, refunds and subscriptions", [new UserId("agent3")]), cancellationToken);
        await teams.CreateAsync(scope, new Team(Escalations, "Escalations", "Tier 2 for complex and VIP cases", [new UserId("supervisor"), new UserId("agent1")]), cancellationToken);

        var contactAttributes = services.GetRequiredService<IContactAttributeDefinitionService>();
        await contactAttributes.CreateAsync(admin, new ContactAttributeDefinition("plan", ContactAttributeType.Text, displayName: "Plan"), cancellationToken);
        await contactAttributes.CreateAsync(admin, new ContactAttributeDefinition("seats", ContactAttributeType.Number, displayName: "Seats"), cancellationToken);
        await contactAttributes.CreateAsync(admin, new ContactAttributeDefinition("customer_since", ContactAttributeType.Date, displayName: "Customer since"), cancellationToken);
        await services.GetRequiredService<IConversationAttributeDefinitionService>().CreateAsync(admin,
            new ConversationAttributeDefinition("order_id", ConversationAttributeType.Text, displayName: "Order ID"), cancellationToken);

        await AutomationAsync(services, admin, cancellationToken);
        await HelpCenterAsync(services, admin, cancellationToken);
        await services.GetRequiredService<ISurveyService>().CreateSurveyAsync(admin, new SurveyDefinition(new SurveyId("nps"), "Quarterly NPS", SurveyKind.Nps,
            new SurveyLocale("en"), [new SurveyQuestion(new SurveyLocale("en"), "How likely are you to recommend Acme to a friend?", "What is the main reason for your score?")],
            new SurveyTriggerPolicy(TimeSpan.FromHours(1), TimeSpan.FromDays(90), TimeSpan.FromDays(14)), Email, enabled: false), cancellationToken);
    }

    private static async Task AutomationAsync(IServiceProvider services, UserPrincipal admin, CancellationToken cancellationToken)
    {
        var canned = services.GetRequiredService<ICannedResponseService>();
        CannedResponse Canned(string id, string shortcut, string title, string body) =>
            new(new CannedResponseId(id), shortcut, title, "en", [new CannedResponseVariant("en", new AutomationTemplate(body))]);
        await canned.CreateAsync(admin, Canned("greeting", "hi", "Friendly greeting", "Hi {{contact.name}}, thanks for reaching out to Acme! I'm looking into this for you now."), cancellationToken);
        await canned.CreateAsync(admin, Canned("refund-policy", "refund", "Refund policy", "Refunds are issued to the original payment method within 5–7 business days. You'll get an email as soon as it's processed."), cancellationToken);
        await canned.CreateAsync(admin, Canned("tracking", "track", "Order tracking", "You can follow your delivery any time at acme.test/track — just enter your order number and postcode."), cancellationToken);
        await canned.CreateAsync(admin, Canned("after-hours", "afterhours", "After-hours auto reply", "Thanks for your message! Our team is offline right now and will get back to you first thing in the morning."), cancellationToken);
        await canned.CreateAsync(admin, Canned("closing", "bye", "Closing message", "Glad I could help! I'll close this conversation now — just reply if anything else comes up."), cancellationToken);

        var rules = services.GetRequiredService<IAutomationRuleService>();
        await rules.CreateAsync(admin, new AutomationRule(new AutomationRuleId("tag-refunds"), "Tag refund requests", MessageReceivedTrigger.Instance,
            new MessageContentCondition(MessageTextOperator.Contains, "refund"), [new AddLabelsAction(new LabelId("refund")), new SetPriorityAction(ConversationPriority.High)], priority: 10), cancellationToken);
        await rules.CreateAsync(admin, new AutomationRule(new AutomationRuleId("route-billing"), "Route invoice questions to Billing", ConversationCreatedTrigger.Instance,
            new AnyOfCondition(new MessageContentCondition(MessageTextOperator.Contains, "invoice"), new InboxIsCondition(Billing)),
            [new AssignTeamAction(BillingTeam), new AddLabelsAction(new LabelId("billing"))], priority: 20), cancellationToken);
        await rules.CreateAsync(admin, new AutomationRule(new AutomationRuleId("vip-priority"), "VIP customers jump the queue", ConversationCreatedTrigger.Instance,
            new HasLabelsCondition(ConditionTarget.Contact, LabelMatch.Any, new LabelId("vip")), [new SetPriorityAction(ConversationPriority.Urgent), new AssignTeamAction(Escalations)], priority: 5,
            stopProcessing: true), cancellationToken);
        await rules.CreateAsync(admin, new AutomationRule(new AutomationRuleId("sla-escalation"), "Escalate SLA breaches", new SlaEventTrigger([SlaEventKind.Breached]), null,
            [ExecuteSlaEscalationAction.Instance, new AddLabelsAction(new LabelId("sla-breached"))], priority: 30), cancellationToken);
        await rules.CreateAsync(admin, new AutomationRule(new AutomationRuleId("stale-pending"), "Resolve stale pending conversations",
            new ScheduledTrigger(new AutomationSchedule("0 * * * *", "UTC"), [ConversationStatusKind.Pending]),
            new ElapsedCondition(ConversationTimestamp.LastActivity, ElapsedComparison.AtLeast, TimeSpan.FromDays(3)),
            [new AddNoteAction(new AutomationTemplate("Auto-resolved after 3 days without a customer reply.")), new SetStatusAction(ConversationStatusKind.Resolved)], priority: 50), cancellationToken);
        await rules.CreateAsync(admin, new AutomationRule(new AutomationRuleId("after-hours"), "After-hours auto reply", MessageReceivedTrigger.Instance,
            new InboxIsCondition(Email), [new SendReplyAction(new CannedResponseId("after-hours"))], priority: 90, enabled: false), cancellationToken);

        var macros = services.GetRequiredService<IMacroService>();
        await macros.CreateAsync(admin, new Macro(new MacroId("escalate-tier2"), "Escalate to Tier 2", MacroVisibility.Shared, null,
            [new AssignTeamAction(Escalations), new AddLabelsAction(new LabelId("escalated")), new SetPriorityAction(ConversationPriority.High),
                new AddNoteAction(new AutomationTemplate("Escalated to Tier 2 for investigation."))], "Hand the conversation to the escalations team with high priority."), cancellationToken);
        await macros.CreateAsync(admin, new Macro(new MacroId("close-duplicate"), "Close as duplicate", MacroVisibility.Shared, null,
            [new AddLabelsAction(new LabelId("duplicate")), new SetStatusAction(ConversationStatusKind.Resolved)], "Label as duplicate and resolve."), cancellationToken);
        await macros.CreateAsync(admin, new Macro(new MacroId("refund-approved"), "Refund approved", MacroVisibility.Shared, null,
            [new SendReplyAction(new CannedResponseId("refund-policy")), new AddLabelsAction(new LabelId("refund")), new SetStatusAction(ConversationStatusKind.Pending)],
            "Send the refund policy and wait for the customer."), cancellationToken);
    }

    private static async Task HelpCenterAsync(IServiceProvider services, UserPrincipal admin, CancellationToken cancellationToken)
    {
        var helpCenter = services.GetRequiredService<IHelpCenterService>();
        var portal = new PortalId("help");
        var en = new HelpCenterLocale("en");
        async Task CollectionAsync(string id, string name, string description, int position) =>
            await helpCenter.CreateCollectionAsync(admin, new HelpCollection(new CollectionId(id), portal, [new CollectionTranslation(en, name, new HelpCenterSlug(id), description)], position: position), cancellationToken);
        await CollectionAsync("getting-started", "Getting started", "Set up your account and learn the basics.", 0);
        await CollectionAsync("billing", "Billing & payments", "Invoices, refunds, plans and payment methods.", 1);
        await CollectionAsync("account", "Account & security", "Passwords, two-factor authentication and SSO.", 2);
        async Task ArticleAsync(string id, string collection, string title, string excerpt, string body, string[] tags, bool publish)
        {
            if (await helpCenter.CreateArticleAsync(admin, new Article(new ArticleId(id), portal, [new ArticleTranslation(en, title, new HelpCenterSlug(id), body, excerpt, tags)],
                new CollectionId(collection)), cancellationToken) is HelpCenterSucceeded<StoredArticle> created && publish)
                await helpCenter.PublishArticleAsync(admin, created.Value.Id, created.Value.Revision, cancellationToken);
        }
        await ArticleAsync("track-your-order", "getting-started", "Track your order", "Follow your delivery from warehouse to doorstep.",
            "## Where is my order?\n\nOpen **Orders** in your account and choose the order you want to follow. The timeline shows every step from *packed* to *delivered*.\n\n- Orders ship within **1 business day**.\n- Tracking links are emailed as soon as the carrier scans the parcel.\n\nStill no update after 5 days? Contact us and we'll chase the carrier for you.",
            ["orders", "shipping"], true);
        await ArticleAsync("refund-policy", "billing", "Refunds and returns", "How refunds work and how long they take.",
            "## Refund timeline\n\nRefunds go back to the original payment method within **5–7 business days** after we receive the return.\n\n1. Start a return from **Orders → Return item**.\n2. Print the prepaid label.\n3. Drop the parcel at any carrier location.\n\nDuplicate charges are refunded automatically within 24 hours.",
            ["billing", "refunds"], true);
        await ArticleAsync("update-payment-method", "billing", "Update your payment method", "Change the card or bank account we charge.",
            "Go to **Settings → Billing → Payment methods** and choose *Add method*. The new method is used from your next invoice.", ["billing"], true);
        await ArticleAsync("vat-invoices", "billing", "Add a VAT number to invoices", "Show your company VAT ID on every invoice.",
            "Add your VAT ID under **Settings → Billing → Company details**. Past invoices can be reissued from the invoice list.", ["billing", "invoices"], true);
        await ArticleAsync("enable-2fa", "account", "Turn on two-factor authentication", "Protect your account with an authenticator app.",
            "## Set up 2FA\n\nOpen **Settings → Security**, choose *Two-factor authentication* and scan the QR code with your authenticator app. Store the recovery codes somewhere safe.",
            ["security", "account"], true);
        await ArticleAsync("sso-setup", "account", "Set up single sign-on (SAML)", "Connect Okta, Entra ID or Google Workspace.",
            "## Before you start\n\nSSO is available on the **Business** plan. You need admin access to your identity provider.\n\n### Steps\n\n1. In Acme, open **Settings → Security → SSO**.\n2. Copy the *ACS URL* and *Entity ID* into your identity provider.\n3. Upload the IdP metadata XML and press **Test connection**.\n\n> Draft — screenshots for Entra ID are still missing.",
            ["security", "sso"], false);
    }

    private sealed record DemoContact(string Id, string Name, string Email, string? Phone, string Company, string Plan, int Seats, string[] Labels);

    private static readonly DemoContact[] People =
    [
        new("c-olivia", "Olivia Bennett", "olivia.bennett@northwind.test", "+1 415 555 0142", "Northwind Traders", "Business", 48, ["vip"]),
        new("c-marcus", "Marcus Lee", "marcus.lee@contoso.test", "+1 206 555 0199", "Contoso", "Pro", 12, []),
        new("c-sofia", "Sofia Rossi", "sofia.rossi@fabrikam.test", "+39 02 5550 1234", "Fabrikam", "Starter", 3, []),
        new("c-daniel", "Daniel Kim", "daniel.kim@tailspin.test", "+1 312 555 0107", "Tailspin Toys", "Business", 120, ["enterprise"]),
        new("c-emma", "Emma Schultz", "emma.schultz@adatum.test", null, "Adatum", "Pro", 9, []),
        new("c-noah", "Noah Williams", "noah@wingtip.test", "+44 20 7946 0321", "Wingtip Labs", "Business", 64, ["developer"]),
        new("c-ava", "Ava Martinez", "ava.martinez@litware.test", "+1 512 555 0164", "Litware", "Pro", 15, []),
        new("c-liam", "Liam O'Connor", "liam.oconnor@proseware.test", null, "Proseware", "Starter", 2, []),
        new("c-chloe", "Chloe Dubois", "chloe.dubois@lamna.test", "+33 1 55 50 12 34", "Lamna Healthcare", "Pro", 22, []),
        new("c-ethan", "Ethan Brown", "ethan.brown@relecloud.test", null, "Relecloud", "Starter", 1, []),
        new("c-mia", "Mia Johnson", "mia.johnson@woodgrove.test", "+1 646 555 0187", "Woodgrove Bank", "Business", 50, ["enterprise"]),
        new("c-lucas", "Lucas Garcia", "lucas.garcia@cohowinery.test", "+34 91 555 0110", "Coho Winery", "Pro", 6, []),
        new("c-hannah", "Hannah Novak", "hannah.novak@trey.test", null, "Trey Research", "Pro", 18, []),
        new("c-james", "James Wilson", "james.wilson@blueyonder.test", "+1 617 555 0175", "Blue Yonder Airlines", "Business", 80, ["vip"]),
        new("c-isabella", "Isabella Costa", "isabella.costa@margies.test", null, "Margie's Travel", "Starter", 4, []),
        new("c-oliver", "Oliver Smith", "oliver.smith@alpineski.test", "+41 44 555 01 23", "Alpine Ski House", "Pro", 11, []),
        new("c-amelia", "Amelia Clarke", "amelia.clarke@fourthcoffee.test", null, "Fourth Coffee", "Starter", 2, []),
        new("c-leo", "Leo Fischer", "leo.fischer@graphicdesign.test", "+49 30 555 0199", "Graphic Design Institute", "Pro", 7, []),
    ];

    private static async Task<IReadOnlyList<ContactId>> ContactsAsync(IContactService contacts, UserPrincipal admin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = new List<ContactId>();
        foreach (var (person, index) in People.Select((person, index) => (person, index)))
        {
            var identities = new List<ContactIdentity> { new(ContactIdentityAddress.Parse(ContactIdentityKind.Email, person.Email), new IdentityVerified("email", now.AddDays(-90))) };
            if (person.Phone is { } phone)
                identities.Add(new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Phone, phone), new IdentityClaimed("whatsapp", now.AddDays(-30))));
            var attributes = new Dictionary<string, ContactAttributeValue>(StringComparer.Ordinal)
            {
                ["plan"] = new TextAttributeValue(person.Plan),
                ["seats"] = new NumberAttributeValue(person.Seats),
                ["customer_since"] = new DateAttributeValue(DateOnly.FromDateTime(now.UtcDateTime.AddDays(-120 - (index * 37)))),
            };
            await contacts.CreateAsync(admin, new ContactDraft(new ContactProfile(person.Name, locale: "en", company: person.Company), identities, attributes,
                [.. person.Labels.Select(label => new LabelId(label))], Id: new ContactId(person.Id)), cancellationToken);
            ids.Add(new ContactId(person.Id));
        }
        return ids;
    }

    private static readonly (string Incoming, string Reply)[] Topics =
    [
        ("Hi, my order hasn't arrived yet. Can you check the status?", "Thanks for waiting! Your parcel is with the carrier and should arrive tomorrow."),
        ("How do I add a teammate to our workspace?", "You can invite teammates from Settings → Members. They'll get an email invite right away."),
        ("I need a copy of last month's invoice.", "I've re-sent the invoice to your billing email — let me know if it doesn't arrive."),
        ("The mobile app keeps logging me out.", "We just shipped a fix in version 4.2.1 — updating the app should solve it."),
        ("Can I change my plan mid-cycle?", "Yes! Upgrades apply immediately and we prorate the difference on your next invoice."),
        ("Is there an API for exporting orders?", "Absolutely — the Orders API supports CSV and JSON exports. Here's the documentation link."),
        ("I was charged twice this month.", "Sorry about that! I've refunded the duplicate charge; it'll show up within 5–7 days."),
        ("How do I reset my password?", "Use 'Forgot password' on the sign-in page and we'll email you a reset link."),
        ("Can you help me set up SSO?", "Sure! SSO is on the Business plan — I've shared our setup guide for Okta."),
        ("The checkout page shows an error.", "Thanks for flagging — our team fixed the checkout issue a few minutes ago. Could you try again?"),
    ];

    private static async Task HistoryAsync(IConversationService conversations, SampleClock clock, TicketScope scope, UserPrincipal admin, IReadOnlyList<ContactId> contacts,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var random = new Random(7);
        var inboxes = new[] { Chat, Chat, Chat, Email, Email, WhatsApp, Billing };
        var agents = new[] { "agent1", "agent2", "agent1", "agent2", "agent3", "supervisor" };
        var sequence = 0;
        for (var day = 13; day >= 1; day--)
        {
            var weekday = now.AddDays(-day).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            var count = (weekday ? 6 : 2) + random.Next(0, weekday ? 5 : 3);
            for (var item = 0; item < count; item++)
            {
                var start = now.Date.AddDays(-day).AddHours(8 + random.Next(0, 10)).AddMinutes(random.Next(0, 60));
                var inbox = inboxes[random.Next(inboxes.Length)];
                var agent = new UserPrincipal(scope, new UserId(inbox == Billing ? "agent3" : agents[random.Next(agents.Length)]));
                var topic = Topics[random.Next(Topics.Length)];
                var id = new ConversationId($"h-{++sequence:D3}");
                Move(clock, now, start);
                var priority = random.Next(10) switch { 0 => ConversationPriority.Urgent, < 3 => ConversationPriority.High, < 6 => ConversationPriority.Medium, _ => ConversationPriority.None };
                if (await conversations.StartAsync(admin, new StartConversationRequest(inbox, contacts[random.Next(contacts.Count)], Priority: priority,
                    Team: inbox == Billing ? BillingTeam : Tier1, InitialMessage: new IncomingMessageDraft(topic.Incoming), Id: id), cancellationToken) is not ConversationApplied)
                    continue;
                Move(clock, now, start.AddMinutes(random.Next(1, 6)));
                await conversations.AssignAsync(admin, id, agent.UserId, null, cancellationToken);
                var responseMinutes = random.Next(4, 100) * (random.Next(8) == 0 ? 4 : 1);
                Move(clock, now, start.AddMinutes(responseMinutes));
                await conversations.ReplyAsync(agent, id, new OutgoingMessageDraft(topic.Reply), null, cancellationToken);
                if (random.Next(3) == 0)
                {
                    Move(clock, now, start.AddMinutes(responseMinutes + random.Next(10, 120)));
                    await conversations.AppendIncomingAsync(admin, id, new IncomingMessageDraft("Thanks, that worked!"), cancellationToken);
                }
                Move(clock, now, start.AddMinutes(responseMinutes + random.Next(30, 600)));
                await conversations.ChangeStatusAsync(agent, id, ConversationStatus.Resolved, null, cancellationToken);
            }
        }
    }

    private static void Move(SampleClock clock, DateTimeOffset now, DateTimeOffset at) => clock.Offset = at > now ? TimeSpan.Zero : at - now;

    private sealed class Script(IConversationService conversations, SampleClock clock, TicketScope scope, UserPrincipal admin, CancellationToken cancellationToken)
    {
        public ConversationId Id { get; private set; }

        public async Task<bool> StartAsync(string id, InboxId inbox, string contact, TimeSpan ago, string message, ConversationPriority priority = ConversationPriority.None,
            TeamId? team = null, string[]? labels = null, string? orderId = null)
        {
            Id = new ConversationId(id);
            At(ago);
            Dictionary<string, ConversationAttributeValue>? attributes = orderId is null ? null : new(StringComparer.Ordinal) { ["order_id"] = new ConversationTextValue(orderId) };
            return await conversations.StartAsync(admin, new StartConversationRequest(inbox, new ContactId(contact), Priority: priority, Labels: labels?.Select(label => new LabelId(label)).ToList(),
                Attributes: attributes, Team: team, InitialMessage: new IncomingMessageDraft(message), Id: Id), cancellationToken) is ConversationApplied;
        }

        public Task IncomingAsync(TimeSpan ago, string message)
        {
            At(ago);
            return conversations.AppendIncomingAsync(admin, Id, new IncomingMessageDraft(message), cancellationToken);
        }

        public Task ReplyAsync(TimeSpan ago, string agent, string message)
        {
            At(ago);
            return conversations.ReplyAsync(User(agent), Id, new OutgoingMessageDraft(message), null, cancellationToken);
        }

        public Task NoteAsync(TimeSpan ago, string agent, string note)
        {
            At(ago);
            return conversations.AddNoteAsync(User(agent), Id, new NoteDraft(note), null, cancellationToken);
        }

        public Task AssignAsync(TimeSpan ago, string agent)
        {
            At(ago);
            return conversations.AssignAsync(admin, Id, new UserId(agent), null, cancellationToken);
        }

        public Task StatusAsync(TimeSpan ago, string agent, ConversationStatus status)
        {
            At(ago);
            return conversations.ChangeStatusAsync(User(agent), Id, status, null, cancellationToken);
        }

        private UserPrincipal User(string agent) => new(scope, new UserId(agent));

        private void At(TimeSpan ago) => clock.Offset = -ago;
    }

    private static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);

    private static async Task CurrentAsync(IConversationService conversations, SampleClock clock, TicketScope scope, UserPrincipal admin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var s = new Script(conversations, clock, scope, admin, cancellationToken);

        if (await s.StartAsync("c-1001", Chat, "c-olivia", M(95), "Hi! I was charged twice for order #48213 — once on Monday and again this morning. Can you refund the duplicate?",
            team: Tier1, orderId: "48213"))
        {
            await s.AssignAsync(M(93), "agent1");
            await s.ReplyAsync(M(88), "agent1", "Hi Olivia, sorry about that! Let me check the payment records for order #48213.");
            await s.NoteAsync(M(80), "agent1", "Stripe shows two captures of $249.00 (ch_3Pk… and ch_3Pq…). Second one looks like a retry after a gateway timeout.");
            await s.ReplyAsync(M(76), "agent1", "I can confirm the duplicate charge. I've started the refund for the second payment of $249.00.");
            await s.IncomingAsync(M(18), "Thank you! How long until I see it on my card? I need it before the end of the month.");
        }
        if (await s.StartAsync("c-1002", Email, "c-marcus", M(26), "Hello,\n\nAfter resetting my password yesterday I can no longer sign in — the login page just reloads. Our whole team is blocked. Please help ASAP.\n\nMarcus",
            ConversationPriority.Urgent, Tier1))
            await s.NoteAsync(M(12), "agent2", "Looks related to the session cookie bug from this morning's deploy. Checking with engineering.");
        if (await s.StartAsync("c-1003", WhatsApp, "c-sofia", M(300), "Ciao! Where is my package? Order 51877 was supposed to arrive on Friday.", team: Tier1, orderId: "51877"))
        {
            await s.AssignAsync(M(290), "agent2");
            await s.ReplyAsync(M(282), "agent2", "Hi Sofia! The courier had a delay in Milan — your package is out for delivery today. Here's the tracking link: acme.test/track/51877");
            await s.StatusAsync(M(281), "agent2", ConversationStatus.Pending);
        }
        if (await s.StartAsync("c-1004", Billing, "c-daniel", M(190), "Hi team, the October invoice (INV-2026-1043) is missing our VAT number DE 811 907 980. Our accounting team can't book it without it. Could you reissue it?",
            team: BillingTeam))
            await s.AssignAsync(M(185), "agent3");
        if (await s.StartAsync("c-1005", Chat, "c-emma", TimeSpan.FromHours(26), "Any plans for a dark mode in the mobile app? My eyes would thank you 🙂", team: Tier1, labels: ["feature-request"]))
        {
            await s.AssignAsync(TimeSpan.FromHours(25.9), "agent1");
            await s.ReplyAsync(TimeSpan.FromHours(25.8), "agent1", "Great idea, Emma! Dark mode is on our roadmap for Q1. I've added your vote to the feature request.");
            await s.IncomingAsync(TimeSpan.FromHours(25.6), "Awesome, thanks!");
            await s.StatusAsync(TimeSpan.FromHours(25.5), "agent1", ConversationStatus.Resolved);
        }
        if (await s.StartAsync("c-1006", Email, "c-noah", M(240), "We're getting HTTP 429 responses from /v2/orders even though we're well under 100 requests per minute. Request IDs: req_8f2a, req_91bc. Is something wrong with the rate limiter?",
            ConversationPriority.High, Tier1, ["api", "bug"]))
        {
            await s.AssignAsync(M(235), "admin");
            await s.ReplyAsync(M(220), "admin", "Hi Noah, thanks for the request IDs. I'm checking the rate limiter logs with our platform team now.");
            await s.NoteAsync(M(200), "admin", "Platform team confirmed: the per-key limit was misconfigured to 60/min for keys created after Nov 10. Fix rolling out at 15:00 UTC.");
            await s.IncomingAsync(M(70), "Any update? Our nightly sync failed again.");
        }
        if (await s.StartAsync("c-1007", Chat, "c-ava", M(400), "We'd like to migrate 3 workspaces into one. Is that possible without losing history?", team: Tier1))
        {
            await s.AssignAsync(M(395), "agent2");
            await s.ReplyAsync(M(380), "agent2", "Yes, we can merge workspaces and keep all history. Which workspace should be the primary one?");
            await s.IncomingAsync(M(300), "Let me check with my team and get back to you tomorrow.");
            await s.StatusAsync(M(298), "agent2", ConversationStatus.Snoozed(now.AddHours(18)));
        }
        if (await s.StartAsync("c-1008", Email, "c-liam", TimeSpan.FromHours(20), "Please cancel my subscription at the end of the current period. Thanks.", team: Tier1, labels: ["churn-risk"]))
        {
            await s.AssignAsync(TimeSpan.FromHours(19.5), "agent1");
            await s.ReplyAsync(TimeSpan.FromHours(19), "agent1", "Hi Liam, I can do that. Before I do — would a 3-month discount on the Starter plan help? If not, I'll schedule the cancellation.");
            await s.StatusAsync(TimeSpan.FromHours(18.9), "agent1", ConversationStatus.Pending);
        }
        if (await s.StartAsync("c-1009", WhatsApp, "c-chloe", M(140), "Bonjour, the blender arrived with a cracked jar. I've attached a photo. Can I get a replacement?",
            ConversationPriority.Medium, Tier1, ["damaged"], "50210"))
        {
            await s.AssignAsync(M(130), "agent2");
            await s.ReplyAsync(M(122), "agent2", "So sorry, Chloé! A replacement jar ships today with express delivery — no need to return the broken one.");
            await s.IncomingAsync(M(55), "Merci beaucoup! Will I get a tracking number?");
        }
        if (await s.StartAsync("c-1010", Chat, "c-ethan", M(8), "How do I export all my data before our audit next week?"))
        {
        }
        if (await s.StartAsync("c-1011", Email, "c-mia", M(150), "We're considering expanding to 50 more seats. Do you offer volume discounts for annual billing?", ConversationPriority.Medium, Tier1, ["sales"]))
        {
            await s.AssignAsync(M(145), "supervisor");
            await s.ReplyAsync(M(110), "supervisor", "Hi Mia, yes — annual plans over 50 seats get 15% off. I'll send over a quote this afternoon.");
            await s.IncomingAsync(M(35), "Great, could you also include SSO in the quote?");
        }
        if (await s.StartAsync("c-1012", Billing, "c-lucas", TimeSpan.FromHours(50), "Hi, any update on my refund? It's been a week.", team: BillingTeam, labels: ["refund"]))
        {
            await s.AssignAsync(TimeSpan.FromHours(49.5), "agent3");
            await s.ReplyAsync(TimeSpan.FromHours(49), "agent3", "Hi Lucas, the refund was issued on Tuesday — it should reach your account within 2 business days.");
            await s.IncomingAsync(TimeSpan.FromHours(30), "Got it today, thank you!");
            await s.StatusAsync(TimeSpan.FromHours(29.8), "agent3", ConversationStatus.Resolved);
        }
        if (await s.StartAsync("c-1013", Chat, "c-james", M(42), "Our flight booking portal integration is returning 500 errors since 10am. This is impacting customers right now.",
            ConversationPriority.Urgent, Escalations, ["vip", "incident"]))
        {
            await s.AssignAsync(M(40), "supervisor");
            await s.ReplyAsync(M(37), "supervisor", "James, we're on it — I've opened an incident with engineering and will update you every 30 minutes.");
            await s.NoteAsync(M(30), "supervisor", "Incident INC-2291 opened. Root cause suspected in the payments webhook. @agent1 please monitor the status page.");
            await s.IncomingAsync(M(6), "Any news? We're still seeing errors for about 20% of requests.");
        }
        if (await s.StartAsync("c-1014", Email, "c-hannah", M(65), "Is it possible to restrict which teammates can see customer phone numbers?", team: Tier1))
        {
        }
        if (await s.StartAsync("c-1015", WhatsApp, "c-oliver", M(160), "Hi, can I change the delivery address for order 51990? I'm moving next week.", team: Tier1, orderId: "51990"))
            await s.AssignAsync(M(150), "agent1");
    }

    /// <summary>Runs every outbox consumer (SLA, routing, automation, reporting, realtime, surveys) until the backlog is processed.</summary>
    private static async Task DrainAsync(IServiceProvider services, SampleSettings settings, CancellationToken cancellationToken)
    {
        var dispatcher = services.GetRequiredService<ConversationOutboxDispatcher>();
        await dispatcher.RegisterAsync(cancellationToken);
        async Task DispatchAsync()
        {
            for (var pass = 0; pass < 400; pass++)
                if ((await dispatcher.RunOnceAsync(cancellationToken)).Handled == 0)
                    break;
        }
        await DispatchAsync();
        if (services.GetService<SlaTimerScheduler>() is { } timers)
            for (var pass = 0; pass < 50; pass++)
                if ((await timers.RunOnceAsync(cancellationToken)).Evaluated == 0)
                    break;
        await DispatchAsync();
    }

    private static async Task AnswerSurveysAsync(IServiceProvider services, SampleSettings settings, CancellationToken cancellationToken)
    {
        if (services.GetService<ISurveyDispatcher>() is not { } dispatcher || services.GetService<ISurveyResponder>() is not { } responder)
            return;
        var tokens = services.GetRequiredService<SampleSurveyTokens>();
        tokens.Capture = true;
        try
        {
            for (var pass = 0; pass < 10; pass++)
                if ((await dispatcher.DispatchDueAsync(settings.Scope, 100, cancellationToken)).Sent == 0)
                    break;
        }
        finally
        {
            tokens.Capture = false;
        }
        var scores = new[] { 5, 5, 4, 5, 3, 5, 4, 5, 2, 5, 4, 5 };
        var comments = new[] { "Super quick and friendly!", null, "Solved my problem, thanks.", null, "Took a while but got there.", "Jamie was great.", null, null, "Still waiting on a proper fix.", null };
        var index = 0;
        foreach (var token in tokens.Drain())
        {
            await responder.RespondAsync(settings.Scope, token, scores[index % scores.Length], comments[index % comments.Length], cancellationToken);
            index++;
        }
    }
}

/// <summary>Collects survey tokens while the seeder answers demo surveys (development only; tokens are never logged).</summary>
public sealed class SampleSurveyTokens
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> tokens = new();

    public bool Capture { get; set; }

    public void Add(string token)
    {
        if (Capture)
            tokens.Enqueue(token);
    }

    public IEnumerable<string> Drain()
    {
        while (tokens.TryDequeue(out var token))
            yield return token;
    }
}
