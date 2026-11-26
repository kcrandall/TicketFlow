using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.StoreContracts;

/// <summary>
/// Behavioral contract every contact store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the same
/// storage (for example two data sources), so fencing is verified across clients.
/// </summary>
public static class ContactStoreContract
{
    public static readonly DateTimeOffset At = TestClock.DefaultStart;

    public static string Describe(ContactWriteResult result) => result switch
    {
        ContactWriteApplied applied => "applied:" + string.Join(",", applied.Contacts.Select(stored => stored.Revision)),
        ContactWriteNotFound notFound => notFound.Merge is { } merge ? $"not-found:merge" : $"not-found:{notFound.Contact}",
        ContactWriteConflict conflict => $"conflict:{conflict.Contact}:{conflict.CurrentRevision}",
        ContactIdentityTaken taken => $"taken:{taken.Owner}",
        ContactWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static string Describe(AttributeDefinitionWriteResult result) => result switch
    {
        AttributeDefinitionApplied applied => $"applied:{applied.Value.Revision}",
        AttributeDefinitionNotFound => "not-found",
        AttributeDefinitionConflict conflict => $"conflict:{conflict.CurrentRevision}",
        ContactWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static ContactIdentityAddress Email(string value) => ContactIdentityAddress.Parse(ContactIdentityKind.Email, value);

    public static ContactIdentity Verified(ContactIdentityAddress address) => new(address, new IdentityVerified("test", At));

    public static ContactIdentity Claimed(ContactIdentityAddress address) => new(address, new IdentityClaimed("test", At));

    public static Contact NewContact(string id, string? name = null, IEnumerable<ContactIdentity>? identities = null, IEnumerable<string>? labels = null,
        IEnumerable<KeyValuePair<string, ContactAttributeValue>>? attributes = null, ContactId? mergedInto = null) =>
        new(new ContactId(id), new ContactProfile(name, locale: "en-US", timeZone: "Europe/London", company: "Analytical Engines"), identities ?? [],
            attributes ?? [], (labels ?? []).Select(label => new LabelId(label)), false, At, At, mergedInto);

    public static ContactChangeDraft Change(ContactChangeKind kind, string contact, string? related = null, Guid? merge = null) =>
        new(Guid.NewGuid(), kind, new ContactId(contact), At, related is null ? null : new ContactId(related), merge);

    public static Task<ContactWriteResult> CreateAsync(IContactStore store, TicketScope scope, Contact contact) =>
        store.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(contact, null)], changes: [Change(ContactChangeKind.Created, contact.Id.Value)]));

    public static Task<ContactWriteResult> ReplaceAsync(IContactStore store, TicketScope scope, Contact contact, long expected) =>
        store.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(contact, expected)], changes: [Change(ContactChangeKind.Updated, contact.Id.Value)]));

    public static async Task VerifyContactsAsync(IContactStore primary, IContactStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "contacts-contract");
        var ada = Email("Ada@Example.COM");
        var phone = ContactIdentityAddress.Parse(ContactIdentityKind.Phone, "+44 20 7946 0000");
        var attributes = new Dictionary<string, ContactAttributeValue>
        {
            ["tier"] = new TextAttributeValue("gold"),
            ["score"] = new NumberAttributeValue(12.5m),
            ["vip"] = new BooleanAttributeValue(true),
            ["birthday"] = new DateAttributeValue(new DateOnly(1815, 12, 10)),
            ["tags"] = new ListAttributeValue(["math", "poetry"]),
        };
        var first = NewContact("contact-a", "Ada Lovelace", [Verified(ada), Claimed(phone)], ["vip"], attributes);
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, scope, first)));
        Assert.AreEqual("conflict:contact-a:1", Describe(await CreateAsync(competing, scope, first)));
        var stored = (await competing.GetAsync(scope, first.Id))!;
        Assert.AreEqual(first, stored.Contact);
        Assert.AreEqual(1L, stored.Revision);
        Assert.AreEqual(scope, stored.Scope);

        var renamed = first.With(new ContactProfile("Augusta Ada King"), updatedAt: At.AddMinutes(1));
        Assert.AreEqual("applied:2", Describe(await ReplaceAsync(competing, scope, renamed, 1)));
        Assert.AreEqual("conflict:contact-a:2", Describe(await ReplaceAsync(primary, scope, first, 1)));
        Assert.AreEqual(renamed, (await primary.GetAsync(scope, first.Id))!.Contact);
        Assert.AreEqual("not-found:missing", Describe(await ReplaceAsync(primary, scope, NewContact("missing"), 1)));

        // Verified identities are unique; claimed identities may repeat.
        var taken = NewContact("contact-b", "Impostor", [Verified(ada)]);
        Assert.AreEqual("taken:contact-a", Describe(await CreateAsync(competing, scope, taken)));
        Assert.IsNull(await primary.GetAsync(scope, taken.Id));
        var claimant = NewContact("contact-b", "Claimant", [Claimed(ada), Claimed(phone)]);
        Assert.AreEqual("applied:1", Describe(await CreateAsync(competing, scope, claimant)));
        var owners = await primary.FindByIdentityAsync(scope, ada);
        CollectionAssert.AreEqual(new[] { "contact-a:True", "contact-b:False" }, owners.Select(owner => $"{owner.Contact}:{owner.Verification.IsVerified}").ToArray());
        Assert.HasCount(2, await primary.FindByIdentityAsync(scope, phone));
        Assert.IsEmpty(await primary.FindByIdentityAsync(scope, Email("nobody@example.com")));

        // Identities move between contacts atomically inside one batch.
        var moved = await primary.WriteAsync(scope, new ContactWriteBatch(puts:
        [
            new ContactPut(renamed.With(identities: [Claimed(phone)]), 2),
            new ContactPut(claimant.With(identities: [Verified(ada), Claimed(phone)]), 1),
        ]));
        Assert.AreEqual("applied:3,2", Describe(moved));
        Assert.AreEqual("contact-b", (await competing.FindByIdentityAsync(scope, ada)).Single().Contact.Value);

        // All or nothing: a failing fence leaves the other put unapplied.
        var partial = await competing.WriteAsync(scope, new ContactWriteBatch(puts:
        [
            new ContactPut(NewContact("contact-c", "Never"), null),
            new ContactPut(claimant, 1),
        ]));
        Assert.AreEqual("conflict:contact-b:2", Describe(partial));
        Assert.IsNull(await primary.GetAsync(scope, new ContactId("contact-c")));
        var sameBatch = await competing.WriteAsync(scope, new ContactWriteBatch(puts:
        [
            new ContactPut(NewContact("contact-d", identities: [Verified(Email("twin@example.com"))]), null),
            new ContactPut(NewContact("contact-e", identities: [Verified(Email("twin@example.com"))]), null),
        ]));
        StringAssert.StartsWith(Describe(sameBatch), "taken:", StringComparison.Ordinal);
        Assert.IsNull(await primary.GetAsync(scope, new ContactId("contact-d")));

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, first.Id));
            Assert.IsEmpty(await primary.FindByIdentityAsync(other, ada));
            Assert.IsEmpty((await primary.SearchAsync(other, ContactSearch.ByName(), AllowAllFilter.Instance)).Contacts);
            Assert.AreEqual("not-found:contact-a", Describe(await ReplaceAsync(primary, other, first, 3)));
            Assert.AreEqual("not-found:contact-a", Describe(await primary.WriteAsync(other, new ContactWriteBatch(deletes: [new ContactDelete(first.Id, 3)]))));
            Assert.IsEmpty(await primary.ReadChangesAsync(other, 0, 10));
        }
        var elsewhere = TestScopes.LevelVariants(scope)[1];
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, elsewhere, NewContact("contact-a", identities: [Verified(ada)]))));

        Assert.AreEqual("conflict:contact-a:3", Describe(await primary.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(first.Id, 1)]))));
        Assert.AreEqual("applied:", Describe(await competing.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(first.Id, 3)],
            changes: [Change(ContactChangeKind.Deleted, "contact-a")]))));
        Assert.IsNull(await primary.GetAsync(scope, first.Id));
        Assert.IsNotNull(await primary.GetAsync(elsewhere, first.Id));
        Assert.AreEqual("contact-b", (await primary.FindByIdentityAsync(scope, phone)).Single().Contact.Value);
        Assert.AreEqual("not-found:contact-a", Describe(await primary.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(first.Id, 3)]))));
    }

    public static async Task VerifyMergeRecordsAsync(IContactStore primary, IContactStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "merge-contract");
        var email = Email("grace@example.com");
        var target = NewContact("target", "Grace Hopper");
        var source = NewContact("source", "G. Hopper", [Verified(email)], ["navy"], [new("rank", new TextAttributeValue("rear admiral"))]);
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, scope, target)));
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, scope, source)));

        var actor = new AccessActor(AccessActorKind.User, "supervisor");
        var merge = ContactMergerShape(target, source, actor);
        var mergedTarget = target.With(identities: [Verified(email)], labels: [new LabelId("navy")]);
        var batch = new ContactWriteBatch(
            puts: [new ContactPut(mergedTarget, 1), new ContactPut(source.Tombstone(target.Id, At), 1)],
            changes: [Change(ContactChangeKind.Merged, "source", "target", merge.Id), Change(ContactChangeKind.Updated, "target", merge: merge.Id)],
            createMerge: merge);
        Assert.AreEqual("applied:2,2", Describe(await primary.WriteAsync(scope, batch)));
        Assert.AreEqual(merge, await competing.GetMergeAsync(scope, merge.Id));
        Assert.IsNull(await competing.GetMergeAsync(TestScopes.LevelVariants(scope)[5], merge.Id));
        var tombstone = (await competing.GetAsync(scope, source.Id))!;
        Assert.AreEqual(target.Id, tombstone.Contact.MergedInto);
        Assert.IsEmpty(tombstone.Contact.Identities);
        Assert.IsFalse((await competing.SearchAsync(scope, ContactSearch.ByName(), AllowAllFilter.Instance)).Contacts.Any(stored => stored.Id == source.Id));

        var duplicate = await competing.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(mergedTarget, 2)], createMerge: merge));
        Assert.AreEqual("rejected:DuplicateMerge", Describe(duplicate));
        Assert.AreEqual(2L, (await primary.GetAsync(scope, target.Id))!.Revision);
        var missing = await competing.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(mergedTarget, 2)], deleteMerge: Guid.NewGuid()));
        Assert.AreEqual("not-found:merge", Describe(missing));
        Assert.AreEqual(2L, (await primary.GetAsync(scope, target.Id))!.Revision);

        // Unmerge-shaped batch: restore the source and delete the record.
        var unmerge = await competing.WriteAsync(scope, new ContactWriteBatch(
            puts: [new ContactPut(target, 2), new ContactPut(source, 2)], deleteMerge: merge.Id));
        Assert.AreEqual("applied:3,3", Describe(unmerge));
        Assert.IsNull(await primary.GetMergeAsync(scope, merge.Id));
        Assert.AreEqual("source", (await primary.FindByIdentityAsync(scope, email)).Single().Contact.Value);

        // Deleting a target removes tombstones merged into it (recursively) and every merge record naming them.
        var middle = NewContact("middle");
        var leaf = NewContact("leaf");
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, scope, middle)));
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, scope, leaf)));
        var leafMerge = ContactMergerShape(middle, leaf, actor);
        Assert.AreEqual("applied:2,2", Describe(await primary.WriteAsync(scope, new ContactWriteBatch(
            puts: [new ContactPut(middle, 1), new ContactPut(leaf.Tombstone(middle.Id, At), 1)], createMerge: leafMerge))));
        var middleMerge = ContactMergerShape(target, middle, actor);
        Assert.AreEqual("applied:4,3", Describe(await primary.WriteAsync(scope, new ContactWriteBatch(
            puts: [new ContactPut(target, 3), new ContactPut(middle.Tombstone(target.Id, At), 2)], createMerge: middleMerge))));
        Assert.AreEqual("applied:", Describe(await competing.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(target.Id, 4)]))));
        Assert.IsNull(await primary.GetAsync(scope, middle.Id));
        Assert.IsNull(await primary.GetAsync(scope, leaf.Id));
        Assert.IsNull(await primary.GetMergeAsync(scope, leafMerge.Id));
        Assert.IsNull(await primary.GetMergeAsync(scope, middleMerge.Id));
        Assert.IsNotNull(await primary.GetAsync(scope, source.Id));
    }

    private static ContactMergeRecord ContactMergerShape(Contact target, Contact source, AccessActor actor) =>
        new(Guid.NewGuid(), target.Id, source.Id, At, actor, source, [.. source.Identities.Select(identity => identity.Address)], [],
            [.. source.AttributeKeys.Select(key => new ContactMergeAttributeChange(key, null, source.Attributes[key]))], source.Labels,
            [ContactProfileField.Company], false);

    public static async Task VerifySearchAsync(IContactStore store)
    {
        var scope = TestScopes.Create(workspaceId: "search-contract");
        var social = new Func<string, string, ContactIdentityAddress>((network, handle) => ContactIdentityAddress.Parse(ContactIdentityKind.SocialHandle, handle, network));
        Contact[] contacts =
        [
            NewContact("c1", "Ada Lovelace", [Verified(Email("ada@example.com")), Claimed(social("instagram", "@Ada"))], ["vip"]),
            NewContact("c2", "ada byron", [Claimed(Email("ada.byron@example.com")), Claimed(social("x", "ada"))]),
            NewContact("c3", "Alan Turing", [Verified(Email("alan@example.com"))], ["vip"]),
            NewContact("c4", "Bob", [Claimed(Email("bob@example.com"))]),
            NewContact("c5", null, [Claimed(Email("adam@example.com")), Claimed(Email("adam.two@example.com"))]),
        ];
        foreach (var contact in contacts)
            Assert.AreEqual("applied:1", Describe(await CreateAsync(store, scope, contact)));
        Assert.AreEqual("applied:1", Describe(await CreateAsync(store, scope, NewContact("c6", "Adalyn"))));
        Assert.AreEqual("applied:2", Describe(await ReplaceAsync(store, scope, NewContact("c6", "Adalyn").Tombstone(new ContactId("c1"), At), 1)));

        static string[] Ids(ContactPage page) => [.. page.Contacts.Select(stored => stored.Id.Value)];

        CollectionAssert.AreEqual(new[] { "c5", "c2", "c1", "c3", "c4" }, Ids(await store.SearchAsync(scope, ContactSearch.ByName(), AllowAllFilter.Instance)));
        CollectionAssert.AreEqual(new[] { "c2", "c1" }, Ids(await store.SearchAsync(scope, ContactSearch.ByName("  AD"), AllowAllFilter.Instance)));
        CollectionAssert.AreEqual(new[] { "c1", "c3" }, Ids(await store.SearchAsync(scope, ContactSearch.ByName(), new HasLabelFilter(new LabelId("vip")))));
        Assert.IsEmpty(Ids(await store.SearchAsync(scope, ContactSearch.ByName(), DenyAllFilter.Instance)));
        Assert.IsEmpty(Ids(await store.SearchAsync(scope, ContactSearch.ByName(), new ScopeFilter(TestScopes.LevelVariants(scope)[5]))));
        CollectionAssert.AreEqual(new[] { "c5", "c2", "c4" }, Ids(await store.SearchAsync(scope, ContactSearch.ByName(), new NotFilter(new HasLabelFilter(new LabelId("vip"))))));
        Assert.IsEmpty(Ids(await store.SearchAsync(scope, ContactSearch.ByName(), new TeamInFilter([new TeamId("team")]))));
        Assert.IsEmpty(Ids(await store.SearchAsync(scope, ContactSearch.ByName(), new AttributeEqualsFilter("tier", "gold"))));

        // Keyset paging visits every row once.
        var seen = new List<string>();
        var search = ContactSearch.ByName(limit: 2);
        for (var page = await store.SearchAsync(scope, search, AllowAllFilter.Instance); ; page = await store.SearchAsync(scope, search, AllowAllFilter.Instance))
        {
            seen.AddRange(Ids(page));
            if (page.Next is null)
                break;
            search = search.WithAfter(page.Next);
        }
        CollectionAssert.AreEqual(new[] { "c5", "c2", "c1", "c3", "c4" }, seen);

        CollectionAssert.AreEqual(new[] { "c2", "c1", "c5" }, Ids(await store.SearchAsync(scope, ContactSearch.ByIdentity(ContactIdentityKind.Email, "ADA"), AllowAllFilter.Instance)));
        CollectionAssert.AreEqual(new[] { "c1" }, Ids(await store.SearchAsync(scope, ContactSearch.ByIdentity(ContactIdentityKind.Email, "ada"), new HasLabelFilter(new LabelId("vip")))));
        CollectionAssert.AreEqual(new[] { "c1", "c2" }, Ids(await store.SearchAsync(scope, ContactSearch.ByIdentity(ContactIdentityKind.SocialHandle, "@ad"), AllowAllFilter.Instance)));
        CollectionAssert.AreEqual(new[] { "c2" }, Ids(await store.SearchAsync(scope, ContactSearch.ByIdentity(ContactIdentityKind.SocialHandle, "ad", "x"), AllowAllFilter.Instance)));
        Assert.IsEmpty(Ids(await store.SearchAsync(scope, ContactSearch.ByIdentity(ContactIdentityKind.Phone, "+1"), AllowAllFilter.Instance)));

        // Identity rows page on (value, id); a contact matching twice appears once per page.
        var identitySeen = new List<string>();
        var identitySearch = ContactSearch.ByIdentity(ContactIdentityKind.Email, "ada", limit: 1);
        for (var page = await store.SearchAsync(scope, identitySearch, AllowAllFilter.Instance); ; page = await store.SearchAsync(scope, identitySearch, AllowAllFilter.Instance))
        {
            identitySeen.AddRange(Ids(page));
            if (page.Next is null)
                break;
            identitySearch = identitySearch.WithAfter(page.Next);
        }
        CollectionAssert.AreEqual(new[] { "c2", "c1", "c5", "c5" }, identitySeen);
    }

    public static async Task VerifyChangesAsync(IContactStore primary, IContactStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "changes-contract");
        var tasks = Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? primary : competing)
            .WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(NewContact($"c{index:D2}"), null)],
                changes: [Change(ContactChangeKind.Created, $"c{index:D2}"), Change(ContactChangeKind.Updated, $"c{index:D2}")])));
        var results = await Task.WhenAll(tasks);
        Assert.IsTrue(results.All(result => result.Value is ContactWriteApplied));
        var assigned = results.SelectMany(result => ((ContactWriteApplied)result.Value!).Changes.Select(change => change.Sequence)).Order().ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, 24).Select(value => (long)value).ToArray(), assigned);

        var all = await competing.ReadChangesAsync(scope, 0, IContactStore.MaximumChangesPerRead);
        CollectionAssert.AreEqual(assigned, all.Select(change => change.Sequence).ToArray());
        Assert.IsTrue(all.All(change => change.Scope == scope));
        foreach (var result in results)
            foreach (var change in ((ContactWriteApplied)result.Value!).Changes)
                Assert.AreEqual(change.Event.Id, all.Single(item => item.Sequence == change.Sequence).Event.Id);
        CollectionAssert.AreEqual(new long[] { 11, 12, 13 }, (await primary.ReadChangesAsync(scope, 10, 3)).Select(change => change.Sequence).ToArray());
        Assert.IsEmpty(await primary.ReadChangesAsync(scope, 24, 10));

        // A failed batch appends nothing; the next successful batch continues the sequence without a gap.
        var failed = await primary.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(NewContact("c00"), null)],
            changes: [Change(ContactChangeKind.Created, "c00")]));
        Assert.AreEqual("conflict:c00:1", Describe(failed));
        var taken = await competing.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(NewContact("v1", identities: [Verified(Email("v@example.com"))]), null),
            new ContactPut(NewContact("v2", identities: [Verified(Email("v@example.com"))]), null)], changes: [Change(ContactChangeKind.Created, "v1")]));
        StringAssert.StartsWith(Describe(taken), "taken:", StringComparison.Ordinal);
        var next = (ContactWriteApplied)(await competing.WriteAsync(scope, new ContactWriteBatch(puts: [new ContactPut(NewContact("after-failure"), null)],
            changes: [Change(ContactChangeKind.Created, "after-failure")]))).Value!;
        Assert.AreEqual(25L, next.Changes.Single().Sequence);
        CollectionAssert.AreEqual(Enumerable.Range(1, 25).Select(value => (long)value).ToArray(),
            (await primary.ReadChangesAsync(scope, 0, IContactStore.MaximumChangesPerRead)).Select(change => change.Sequence).ToArray());
        var otherScope = TestScopes.Create(workspaceId: "changes-other");
        Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, otherScope, NewContact("x"))));
        Assert.AreEqual(1L, (await primary.ReadChangesAsync(otherScope, 0, 10)).Single().Sequence);
    }

    public static async Task VerifyDeleteCascadeAsync(IContactStore primary, IContactStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "cascade-contract");
        var actor = new AccessActor(AccessActorKind.User, "supervisor");
        foreach (var id in new[] { "root", "child-a", "child-b", "grandchild" })
            Assert.AreEqual("applied:1", Describe(await CreateAsync(primary, scope, NewContact(id))));
        async Task MergeAsync(string target, string source)
        {
            var targetStored = (await primary.GetAsync(scope, new ContactId(target)))!;
            var sourceStored = (await primary.GetAsync(scope, new ContactId(source)))!;
            StringAssert.StartsWith(Describe(await primary.WriteAsync(scope, new ContactWriteBatch(
                puts: [new ContactPut(targetStored.Contact, targetStored.Revision), new ContactPut(sourceStored.Contact.Tombstone(targetStored.Id, At), sourceStored.Revision)],
                createMerge: ContactMergerShape(targetStored.Contact, sourceStored.Contact, actor)))), "applied:", StringComparison.Ordinal);
        }
        await MergeAsync("child-a", "grandchild");
        await MergeAsync("root", "child-a");
        await MergeAsync("root", "child-b");

        CollectionAssert.AreEqual(new[] { "child-a", "child-b" }, (await competing.ListMergedIntoAsync(scope, new ContactId("root"), 10)).Select(id => id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "child-a" }, (await competing.ListMergedIntoAsync(scope, new ContactId("root"), 1)).Select(id => id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "grandchild" }, (await competing.ListMergedIntoAsync(scope, new ContactId("child-a"), 10)).Select(id => id.Value).ToArray());
        Assert.IsEmpty(await competing.ListMergedIntoAsync(TestScopes.LevelVariants(scope)[5], new ContactId("root"), 10));

        // A cascade that differs from the authorized set is a conflict and removes nothing.
        var partial = await competing.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(new ContactId("root"), 3, [new ContactId("child-a"), new ContactId("child-b")])],
            changes: [Change(ContactChangeKind.Deleted, "root")]));
        Assert.AreEqual("conflict:root:3", Describe(partial));
        Assert.IsNotNull(await primary.GetAsync(scope, new ContactId("grandchild")));

        var applied = (ContactWriteApplied)(await competing.WriteAsync(scope, new ContactWriteBatch(
            deletes: [new ContactDelete(new ContactId("root"), 3, [new ContactId("child-a"), new ContactId("child-b"), new ContactId("grandchild")])],
            changes: [Change(ContactChangeKind.Deleted, "root")]))).Value!;
        CollectionAssert.AreEqual(new[] { "root", "child-a", "child-b", "grandchild" }, applied.Deleted.Select(id => id.Value).ToArray());
        foreach (var id in applied.Deleted)
            Assert.IsNull(await primary.GetAsync(scope, id));
    }

    /// <summary><paramref name="store"/> must examine at most 3 rows per search page.</summary>
    public static async Task VerifyBoundedScanAsync(IContactStore store)
    {
        var scope = TestScopes.Create(workspaceId: "bounded-scan");
        for (var index = 0; index < 7; index++)
            Assert.AreEqual("applied:1", Describe(await CreateAsync(store, scope, NewContact($"c{index}", $"name {index}", labels: index == 5 ? ["vip"] : null))));
        var filter = new HasLabelFilter(new LabelId("vip"));
        var search = ContactSearch.ByName(limit: 10);
        var first = await store.SearchAsync(scope, search, filter);
        Assert.IsEmpty(first.Contacts);
        Assert.AreEqual("c2", first.Next!.Contact.Value);
        var second = await store.SearchAsync(scope, search.WithAfter(first.Next), filter);
        CollectionAssert.AreEqual(new[] { "c5" }, second.Contacts.Select(stored => stored.Id.Value).ToArray());
        Assert.AreEqual("c5", second.Next!.Contact.Value);
        var third = await store.SearchAsync(scope, search.WithAfter(second.Next), filter);
        Assert.IsEmpty(third.Contacts);
        Assert.IsNull(third.Next);
        var unfiltered = await store.SearchAsync(scope, ContactSearch.ByName(limit: 2), AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { "c0", "c1" }, unfiltered.Contacts.Select(stored => stored.Id.Value).ToArray());
    }

    public static async Task VerifyConcurrentVerifiedIdentityAsync(IContactStore primary, IContactStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "identity-race");
        for (var round = 0; round < 8; round++)
        {
            var address = Email($"race-{round}@example.com");
            var results = await Task.WhenAll(
                CreateAsync(primary, scope, NewContact($"left-{round}", identities: [Verified(address)])),
                CreateAsync(competing, scope, NewContact($"right-{round}", identities: [Verified(address)])));
            Assert.AreEqual(1, results.Count(result => result.Value is ContactWriteApplied), $"round {round}");
            Assert.AreEqual(1, results.Count(result => result.Value is ContactIdentityTaken), $"round {round}");
            Assert.HasCount(1, await primary.FindByIdentityAsync(scope, address));
        }
    }

    public static async Task VerifyDefinitionsAsync(IContactAttributeDefinitionStore primary, IContactAttributeDefinitionStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "definitions-contract");
        var tier = new ContactAttributeDefinition("tier", ContactAttributeType.Text, displayName: "Tier");
        Assert.AreEqual("applied:1", Describe(await primary.CreateAsync(scope, tier)));
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, tier)));
        Assert.AreEqual(tier, (await competing.GetAsync(scope, "tier"))!.Definition);
        var sensitive = new ContactAttributeDefinition("tier", ContactAttributeType.Text, sensitive: true);
        Assert.AreEqual("applied:2", Describe(await competing.UpdateAsync(scope, sensitive, 1)));
        Assert.AreEqual("conflict:2", Describe(await primary.UpdateAsync(scope, tier, 1)));
        Assert.AreEqual("rejected:TypeChanged", Describe(await primary.UpdateAsync(scope, new ContactAttributeDefinition("tier", ContactAttributeType.Number), 2)));
        Assert.AreEqual("not-found", Describe(await primary.UpdateAsync(scope, new ContactAttributeDefinition("missing", ContactAttributeType.Text), 1)));
        Assert.AreEqual("applied:1", Describe(await primary.CreateAsync(scope, new ContactAttributeDefinition("age", ContactAttributeType.Number))));
        CollectionAssert.AreEqual(new[] { "age", "tier" }, (await competing.ListAsync(scope)).Select(stored => stored.Definition.Key).ToArray());
        Assert.IsTrue((await competing.GetAsync(scope, "tier"))!.Definition.Sensitive);
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, "tier"));
            Assert.IsEmpty(await primary.ListAsync(other));
            Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(other, "tier", 2)));
        }
        Assert.AreEqual("conflict:2", Describe(await primary.DeleteAsync(scope, "tier", 1)));
        Assert.AreEqual("applied:2", Describe(await competing.DeleteAsync(scope, "tier", 2)));
        Assert.IsNull(await primary.GetAsync(scope, "tier"));
        Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(scope, "tier", 2)));

        var capacity = TestScopes.Create(workspaceId: "definitions-capacity");
        for (var index = 0; index < IContactAttributeDefinitionStore.MaximumDefinitionsPerWorkspace; index++)
            Assert.AreEqual("applied:1", Describe(await primary.CreateAsync(capacity, new ContactAttributeDefinition($"k{index}", ContactAttributeType.Boolean))));
        Assert.AreEqual("rejected:CapacityExceeded", Describe(await competing.CreateAsync(capacity, new ContactAttributeDefinition("overflow", ContactAttributeType.Boolean))));
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(capacity, new ContactAttributeDefinition("k0", ContactAttributeType.Boolean))));
    }
}
