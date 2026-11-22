using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts.Tests;

[TestClass]
public sealed class ResolutionTests
{
    private static ContactResolveRequest Request(params ContactIdentityEvidence[] evidence) => new(evidence, new ContactProfile("Inbound"));

    private static ContactIdentityEvidence Evidence(ContactIdentity identity) => new(identity.Address, identity.Verification);

    [TestMethod]
    public async Task CreatesThenMatchesVerifiedEvidence()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var email = fixture.Verified(ContactFixture.Email("Ada@Example.com"));
        var created = await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(email)));
        var contact = ((ContactCreated)created.Value!).Contact;
        Assert.AreEqual("Inbound", contact.Profile.DisplayName);
        Assert.AreEqual($"matched:Verified:{contact.Id}", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(email)))));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Agent,
            Request(Evidence(fixture.Verified(ContactFixture.Email("new@example.com")))))));
        Assert.AreEqual(ContactChangeKind.Created, fixture.Observer.Changes.Single().Event.Kind);
    }

    [TestMethod]
    public async Task ClaimedEvidenceNeverCountsAsVerifiedMatch()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var address = ContactFixture.Email("ceo@example.com");
        await fixture.CreateAsync("ceo", identities: [fixture.Verified(address)]);
        Assert.AreEqual("matched:Claimed:ceo", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(fixture.Claimed(address))))));
        Assert.AreEqual("created", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm,
            new ContactResolveRequest([Evidence(fixture.Claimed(address))], claimedMatching: ClaimedIdentityMatching.Never))));
    }

    [TestMethod]
    public async Task ClaimedMatchingPolicyAndAmbiguity()
    {
        using var fixture = await ContactFixture.CreateAsync(options => options.ClaimedIdentityMatching = ClaimedIdentityMatching.Never);
        var shared = ContactFixture.Phone("+14155550100");
        var unique = ContactFixture.Phone("+14155550199");
        await fixture.CreateAsync("one", identities: [fixture.Claimed(shared)]);
        await fixture.CreateAsync("two", identities: [fixture.Claimed(unique)]);
        ContactResolveRequest WhenUnique(ContactIdentityAddress address) =>
            new([new ContactIdentityEvidence(address, new IdentityClaimed("form", fixture.Clock.GetUtcNow()))], claimedMatching: ClaimedIdentityMatching.WhenUnique);

        // The configured policy (Never) creates a second claimed holder of the shared number.
        Assert.AreEqual("created", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(fixture.Claimed(shared))))));
        Assert.AreEqual("matched:Claimed:two", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm, WhenUnique(unique))));
        var ambiguous = (ContactAmbiguous)(await fixture.Contacts.ResolveAsync(fixture.Crm, WhenUnique(shared))).Value!;
        Assert.HasCount(2, ambiguous.Candidates);
        Assert.Contains(new ContactId("one"), ambiguous.Candidates);
    }

    [TestMethod]
    public async Task VerifiedEvidenceForDifferentContactsIsAmbiguous()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var email = fixture.Verified(ContactFixture.Email("ada@example.com"));
        var phone = fixture.Verified(ContactFixture.Phone("+14155550100"));
        await fixture.CreateAsync("by-email", identities: [email]);
        await fixture.CreateAsync("by-phone", identities: [phone]);
        Assert.AreEqual("ambiguous:by-email,by-phone", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(email), Evidence(phone)))));
        var claimedPhone = fixture.Claimed(ContactFixture.Phone("+14155550100"));
        Assert.AreEqual("matched:Verified:by-email", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(email), Evidence(claimedPhone)))));
    }

    [TestMethod]
    public async Task AmbiguityListsOnlyReadableCandidates()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var reader = await fixture.CustomRoleAsync("vip-resolver", ContactPermissions.Read);
        await fixture.GrantAsync(new ApiClientId("vip-crm"), reader, condition: new HasLabelCondition(new LabelId("vip")));
        var vipCrm = Testing.TestPrincipals.ApiClient(fixture.Scope, "vip-crm");
        var email = fixture.Verified(ContactFixture.Email("ada@example.com"));
        var phone = fixture.Verified(ContactFixture.Phone("+14155550100"));
        await fixture.CreateAsync("hidden", identities: [email]);
        await fixture.CreateAsync("visible", identities: [phone], labels: ["vip"]);
        Assert.AreEqual("ambiguous:visible", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(vipCrm, Request(Evidence(email), Evidence(phone)))));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(vipCrm, Request(Evidence(email)))));
    }

    [TestMethod]
    public async Task MatchedContactsAreMaskedForTheResolver()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.MaskIdentitiesAsync();
        var email = fixture.Verified(ContactFixture.Email("ada@example.com"));
        var created = ((ContactCreated)(await fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(email)))).Value!).Contact;
        Assert.IsTrue(created.Identities.Single().Masked);
        Assert.IsNull(created.Identities.Single().Value);
    }

    [TestMethod]
    public async Task ConcurrentResolutionsConvergeOnOneContact()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var email = fixture.Verified(ContactFixture.Email("race@example.com"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Contacts.ResolveAsync(fixture.Crm, Request(Evidence(email)))));
        var ids = results.Select(result => result.Value switch
        {
            ContactCreated created => created.Contact.Id,
            ContactMatched matched => matched.Contact.Id,
            _ => throw new AssertFailedException(ContactFixture.Describe(result)),
        }).Distinct().ToList();
        Assert.HasCount(1, ids);
    }
}

[TestClass]
public sealed class SearchAndExportTests
{
    [TestMethod]
    public async Task SearchUsesAccessFiltersAndKeysetCursors()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var viewer = await fixture.CustomRoleAsync("vip-reader", ContactPermissions.Read);
        await fixture.GrantAsync(new UserId("vip-reader"), viewer, condition: new HasLabelCondition(new LabelId("vip")));
        var vipReader = Testing.TestPrincipals.User(fixture.Scope, "vip-reader");
        await fixture.CreateAsync("c1", "Ada Lovelace", labels: ["vip"]);
        await fixture.CreateAsync("c2", "Ada Byron");
        await fixture.CreateAsync("c3", "Alan Turing", labels: ["vip"]);
        await fixture.CreateAsync("c4", "Adele");

        static string[] Ids(ContactSearchResult result) => [.. ((ContactSearchPage)result.Value!).Contacts.Select(contact => contact.Id.Value)];

        CollectionAssert.AreEqual(new[] { "c2", "c1", "c4" }, Ids(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByName("ad"))));
        CollectionAssert.AreEqual(new[] { "c1", "c3" }, Ids(await fixture.Contacts.SearchAsync(vipReader, ContactSearch.ByName())));
        Assert.IsEmpty(Ids(await fixture.Contacts.SearchAsync(Testing.TestPrincipals.User(fixture.Scope, "nobody"), ContactSearch.ByName())));

        var search = ContactSearch.ByName(limit: 1);
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = (ContactSearchPage)(await fixture.Contacts.SearchAsync(fixture.Agent, search, cursor)).Value!;
            seen.AddRange(page.Contacts.Select(contact => contact.Id.Value));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        CollectionAssert.AreEqual(new[] { "c2", "c1", "c4", "c3" }, seen);

        var first = (ContactSearchPage)(await fixture.Contacts.SearchAsync(fixture.Agent, search)).Value!;
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByName("ad", limit: 1), first.NextCursor)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Contacts.SearchAsync(fixture.Agent, search, "not-a-cursor")));
    }

    private static string Describe(ContactSearchResult result) => result switch
    {
        ContactSearchPage page => $"page:{page.Contacts.Count}",
        ContactRefused refused => $"refused:{refused.Reason}",
    };

    [TestMethod]
    public async Task IdentitySearchIsRefusedForMaskedKinds()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.MaskIdentitiesAsync();
        await fixture.CreateAsync("c1", "Ada", [fixture.Verified(ContactFixture.Email("ada@example.com")),
            new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.SocialHandle, "ada", "x"), new IdentityClaimed("agent", fixture.Clock.GetUtcNow()))]);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByIdentity(ContactIdentityKind.Email, "a"))));
        Assert.AreEqual("page:1", Describe(await fixture.Contacts.SearchAsync(fixture.Supervisor, ContactSearch.ByIdentity(ContactIdentityKind.Email, "a"))));
        Assert.AreEqual("page:1", Describe(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByIdentity(ContactIdentityKind.SocialHandle, "@a"))));
        var masked = ((ContactSearchPage)(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByName())).Value!).Contacts.Single();
        Assert.IsNull(masked.Identities.Single(identity => identity.Kind == ContactIdentityKind.Email).Value);
    }

    [TestMethod]
    public async Task ExportIsAuthorizedAuditedBoundedAndMasked()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.MaskIdentitiesAsync();
        for (var index = 0; index < 7; index++)
            await fixture.CreateAsync($"c{index}", $"Contact {index}", [fixture.Verified(ContactFixture.Email($"c{index}@example.com"))]);

        Assert.AreEqual(ContactRefusal.Forbidden, ((ContactRefused)(await fixture.Contacts.ExportAsync(fixture.Agent, new ContactExportRequest(5, "quarterly report"))).Value!).Reason);
        Assert.AreEqual(ContactRefusal.Invalid, ((ContactRefused)(await fixture.Contacts.ExportAsync(fixture.Supervisor, new ContactExportRequest(10_001, "too many"))).Value!).Reason);
        Assert.IsEmpty(fixture.Store.AuditEvents);

        var started = (ContactExportStarted)(await fixture.Contacts.ExportAsync(fixture.Supervisor, new ContactExportRequest(5, "quarterly report"))).Value!;
        var audit = fixture.Store.AuditEvents.Single();
        Assert.AreEqual(ContactAuditKind.Exported, audit.Kind);
        Assert.AreEqual(started.AuditEventId, audit.Id);
        Assert.AreEqual(5, audit.Count);
        var rows = new List<ContactView>();
        await foreach (var row in started.Contacts)
            rows.Add(row);
        Assert.HasCount(5, rows);
        Assert.IsTrue(rows.All(row => row.Identities.All(identity => !identity.Masked)));

        var owner = (ContactExportStarted)(await fixture.Contacts.ExportAsync(fixture.Owner, new ContactExportRequest(100, "full export"))).Value!;
        var ownerRows = new List<ContactView>();
        await foreach (var row in owner.Contacts)
            ownerRows.Add(row);
        Assert.HasCount(7, ownerRows);
        Assert.IsTrue(ownerRows.All(row => row.Identities.All(identity => identity.Masked && identity.Revealable)));
        Assert.IsTrue(fixture.AccessAudit.Snapshot().Any(item => item.Kind == AccessAuditKind.Decision && item.Permission == ContactPermissions.Export));
    }

    [TestMethod]
    public async Task ExportPagesThroughTheStore()
    {
        using var fixture = await ContactFixture.CreateAsync(options => options.ExportPageSize = 2);
        for (var index = 0; index < 5; index++)
            await fixture.CreateAsync($"c{index}", $"Contact {index}");
        var started = (ContactExportStarted)(await fixture.Contacts.ExportAsync(fixture.Owner, new ContactExportRequest(100, "paging check"))).Value!;
        var ids = new List<string>();
        await foreach (var row in started.Contacts)
            ids.Add(row.Id.Value);
        CollectionAssert.AreEqual(new[] { "c0", "c1", "c2", "c3", "c4" }, ids);
    }
}

[TestClass]
public sealed class MergeTests
{
    [TestMethod]
    public async Task MergeMovesGraphAuditsAndNotifies()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.DefineAsync(new ContactAttributeDefinition("tier", ContactAttributeType.Text));
        await fixture.DefineAsync(new ContactAttributeDefinition("plan", ContactAttributeType.Text));
        var email = ContactFixture.Email("ada@example.com");
        var phone = ContactFixture.Phone("+14155550100");
        await fixture.CreateAsync("target", "Ada", [fixture.Claimed(email)], ["vip"], new() { ["tier"] = new TextAttributeValue("gold") });
        var source = await fixture.Contacts.CreateAsync(fixture.Owner, new ContactDraft(new ContactProfile("A. Lovelace", company: "Engines Ltd"),
            [fixture.Verified(email), fixture.Verified(phone)], new Dictionary<string, ContactAttributeValue> { ["tier"] = new TextAttributeValue("silver"), ["plan"] = new TextAttributeValue("pro") },
            [new LabelId("beta")], Blocked: true, Id: new ContactId("source")));
        ContactFixture.Applied(source);

        var request = new ContactMergeRequest(new ContactId("target"), 1, new ContactId("source"), 1, justification: "duplicate");
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.MergeAsync(fixture.Agent, request)));
        Assert.AreEqual("refused:Conflict:1", ContactFixture.Describe(await fixture.Contacts.MergeAsync(fixture.Supervisor,
            new ContactMergeRequest(new ContactId("target"), 2, new ContactId("source"), 1))));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(await fixture.Contacts.MergeAsync(fixture.Supervisor,
            new ContactMergeRequest(new ContactId("target"), 1, new ContactId("target"), 1))));

        var observed = fixture.Observer.Changes.Count;
        var outcome = ContactFixture.Applied(await fixture.Contacts.MergeAsync(fixture.Supervisor, request));
        var merged = outcome.Target;
        Assert.AreEqual(2L, merged.Revision);
        Assert.AreEqual("Ada", merged.Profile.DisplayName);
        Assert.AreEqual("Engines Ltd", merged.Profile.Company);
        Assert.IsTrue(merged.Blocked);
        Assert.IsTrue(merged.Identities.All(identity => identity.Verification.IsVerified));
        Assert.HasCount(2, merged.Identities);
        CollectionAssert.AreEqual(new[] { "beta", "vip" }, merged.Labels.Select(label => label.Value).ToArray());
        Assert.AreEqual(new TextAttributeValue("gold"), merged.Attributes.Single(attribute => attribute.Key == "tier").Value!.Value.Value);
        Assert.AreEqual(new TextAttributeValue("pro"), merged.Attributes.Single(attribute => attribute.Key == "plan").Value!.Value.Value);

        var redirected = (ContactRefused)(await fixture.Contacts.GetAsync(fixture.Agent, new ContactId("source"))).Value!;
        Assert.AreEqual(ContactRefusal.Merged, redirected.Reason);
        Assert.AreEqual(new ContactId("target"), redirected.MergedInto);
        Assert.AreEqual("matched:Verified:target", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm,
            new ContactResolveRequest([new ContactIdentityEvidence(phone, new IdentityVerified("sms", fixture.Clock.GetUtcNow()))]))));
        Assert.IsFalse(((ContactSearchPage)(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByName())).Value!).Contacts.Any(contact => contact.Id.Value == "source"));

        var audit = fixture.Store.AuditEvents.Single(item => item.Kind == ContactAuditKind.Merged);
        Assert.AreEqual(outcome.AuditEventId, audit.Id);
        Assert.AreEqual(outcome.MergeId, audit.Merge);
        Assert.AreEqual(new ContactId("source"), audit.Contact);
        Assert.AreEqual(new ContactId("target"), audit.Related);
        Assert.AreEqual("duplicate", audit.Justification);
        Assert.AreEqual(new AccessActor(AccessActorKind.User, "supervisor"), audit.Actor);
        Assert.IsTrue(fixture.AccessAudit.Snapshot().Count(item => item.Kind == AccessAuditKind.Decision && item.Permission == ContactPermissions.Merge) >= 2);

        var mergeChanges = fixture.Observer.Changes.Skip(observed).ToList();
        CollectionAssert.AreEqual(new[] { ContactChangeKind.Merged, ContactChangeKind.Updated }, mergeChanges.Select(change => change.Event.Kind).ToArray());
        Assert.AreEqual(new ContactId("target"), mergeChanges[0].Event.Related);
        Assert.AreEqual(outcome.MergeId, mergeChanges[0].Event.Merge);
        var outbox = await fixture.Store.ReadChangesAsync(fixture.Scope, 0, 100);
        CollectionAssert.AreEqual(fixture.Observer.Changes.Select(change => change.Sequence).ToArray(), outbox.Select(change => change.Sequence).ToArray());
    }

    [TestMethod]
    public async Task UnmergeRestoresSourceAndKeepsLaterTargetEdits()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.DefineAsync(new ContactAttributeDefinition("tier", ContactAttributeType.Text));
        await fixture.DefineAsync(new ContactAttributeDefinition("plan", ContactAttributeType.Text));
        var email = ContactFixture.Email("ada@example.com");
        var phone = ContactFixture.Phone("+14155550100");
        var targetEmail = fixture.Claimed(email);
        await fixture.CreateAsync("target", "Ada", [targetEmail], ["vip"], new() { ["tier"] = new TextAttributeValue("gold") });
        var sourceView = await fixture.CreateAsync("source", "Lovelace", [fixture.Verified(email), fixture.Verified(phone)], ["beta"],
            new() { ["tier"] = new TextAttributeValue("silver"), ["plan"] = new TextAttributeValue("pro") });
        var sourceBefore = (await fixture.Store.GetAsync(fixture.Scope, new ContactId("source")))!.Contact;

        var outcome = ContactFixture.Applied(await fixture.Contacts.MergeAsync(fixture.Supervisor,
            new ContactMergeRequest(new ContactId("target"), 1, new ContactId("source"), 1, ContactAttributeConflict.PreferSource)));
        Assert.AreEqual(new TextAttributeValue("silver"), outcome.Target.Attributes.Single(attribute => attribute.Key == "tier").Value!.Value.Value);
        // A later edit to a merged contribution is kept on the target by unmerge.
        ContactFixture.Applied(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("target"), 2,
            [new SetContactAttribute("plan", new TextAttributeValue("enterprise")), new AddContactLabel(new LabelId("later"))]));

        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.UnmergeAsync(fixture.Agent, outcome.MergeId)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var unmerged = ContactFixture.Applied(await fixture.Contacts.UnmergeAsync(fixture.Supervisor, outcome.MergeId));
        var target = unmerged.Target;
        Assert.AreEqual(new ContactIdentityView(ContactIdentityKind.Email, "", "ada@example.com", targetEmail.Verification, false, false), target.Identities.Single());
        Assert.AreEqual(new TextAttributeValue("gold"), target.Attributes.Single(attribute => attribute.Key == "tier").Value!.Value.Value);
        Assert.AreEqual(new TextAttributeValue("enterprise"), target.Attributes.Single(attribute => attribute.Key == "plan").Value!.Value.Value);
        CollectionAssert.AreEqual(new[] { "later", "vip" }, target.Labels.Select(label => label.Value).ToArray());

        var restored = (await fixture.Store.GetAsync(fixture.Scope, new ContactId("source")))!;
        Assert.IsNull(restored.Contact.MergedInto);
        Assert.AreEqual(3L, restored.Revision);
        CollectionAssert.AreEqual(sourceBefore.Identities.ToArray(), restored.Contact.Identities.ToArray());
        Assert.AreEqual(sourceBefore.Profile, restored.Contact.Profile);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), restored.Contact.UpdatedAt);
        Assert.AreEqual(sourceView.Id, unmerged.Source.Id);
        Assert.AreEqual("matched:Verified:source", ContactFixture.Describe(await fixture.Contacts.ResolveAsync(fixture.Crm,
            new ContactResolveRequest([new ContactIdentityEvidence(phone, new IdentityVerified("sms", fixture.Clock.GetUtcNow()))]))));

        Assert.AreEqual(ContactAuditKind.Unmerged, fixture.Store.AuditEvents[^1].Kind);
        Assert.AreEqual(ContactChangeKind.Unmerged, fixture.Observer.Changes[^2].Event.Kind);
        Assert.AreEqual("refused:NotFound", ContactFixture.Describe(await fixture.Contacts.UnmergeAsync(fixture.Supervisor, outcome.MergeId)));
    }

    [TestMethod]
    public async Task ChainedMergesUnmergeInOrder()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.CreateAsync("a");
        await fixture.CreateAsync("b");
        await fixture.CreateAsync("c");
        var first = ContactFixture.Applied(await fixture.Contacts.MergeAsync(fixture.Supervisor, new ContactMergeRequest(new ContactId("b"), 1, new ContactId("a"), 1)));
        var second = ContactFixture.Applied(await fixture.Contacts.MergeAsync(fixture.Supervisor, new ContactMergeRequest(new ContactId("c"), 1, new ContactId("b"), 2)));
        Assert.AreEqual("refused:Merged:3", ContactFixture.Describe(await fixture.Contacts.UnmergeAsync(fixture.Supervisor, first.MergeId)));
        Assert.AreEqual("refused:Merged:2", ContactFixture.Describe(await fixture.Contacts.MergeAsync(fixture.Supervisor, new ContactMergeRequest(new ContactId("c"), 2, new ContactId("a"), 2))));
        ContactFixture.Applied(await fixture.Contacts.UnmergeAsync(fixture.Supervisor, second.MergeId));
        ContactFixture.Applied(await fixture.Contacts.UnmergeAsync(fixture.Supervisor, first.MergeId));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.GetAsync(fixture.Agent, new ContactId("a"))));
    }

    [TestMethod]
    public async Task ObserverFailurePropagatesAfterCommit()
    {
        using var fixture = await ContactFixture.CreateAsync();
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(fixture.Provider);
        var services = scope.ServiceProvider;
        var service = new ContactService(fixture.Store, new InMemoryContactAttributeDefinitionStore(), fixture.Store,
            (IAccessEvaluator)services.GetService(typeof(IAccessEvaluator))!, (IAccessQueryPlanner)services.GetService(typeof(IAccessQueryPlanner))!, fixture.Policies, fixture.Catalog,
            [new ThrowingObserver()], Microsoft.Extensions.Options.Options.Create(new ContactsOptions()), fixture.Clock);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.CreateAsync(fixture.Owner, new ContactDraft(ContactProfile.Empty, Id: new ContactId("kept"))));
        Assert.IsNotNull(await fixture.Store.GetAsync(fixture.Scope, new ContactId("kept")));
    }

    private sealed class ThrowingObserver : IContactChangeObserver
    {
        public Task OnChangedAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken = default) => throw new InvalidOperationException("observer down");
    }
}
