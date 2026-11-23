using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.StoreContracts;

/// <summary>
/// Behavioral contract every <see cref="IContactLinkingStore"/> must satisfy. <c>primary</c> and <c>competing</c> must be independent
/// clients of the same storage; <c>contacts</c> must be the contact store whose contacts the linking store filters.
/// </summary>
public static class ContactLinkingStoreContract
{
    private static readonly AccessActor Actor = new(AccessActorKind.User, "owner");

    public static string Describe<T>(StoreWriteResult<T, ContactWriteRejection> result) => result switch
    {
        StoreApplied<T> applied => applied.Value switch
        {
            StoredLinkSuggestionWrite write => write.Created ? "created" : "applied",
            StoredContactLinkingPolicy policy => policy.Revision == 1 ? "created" : "applied",
            _ => "applied",
        },
        StoreNotFound => "not-found",
        StoreConflict conflict => $"conflict:{conflict.CurrentRevision}",
        StoreRejected<ContactWriteRejection> rejected => $"rejected:{rejected.Reason}",
    };

    public static ContactLinkSuggestion Suggestion(string candidate, string account, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), new ContactId(candidate), new ContactId(account), ContactStoreContract.Email($"{candidate}@example.com"), true,
            ContactLinkSuggestionStatus.Pending, ContactStoreContract.At, ContactStoreContract.At, Actor);

    public static async Task VerifyPolicyAsync(IContactLinkingStore primary, IContactLinkingStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "linking-policy");
        var other = TestScopes.Create(workspaceId: "linking-policy-other");
        Assert.IsNull(await primary.GetPolicyAsync(scope));
        Assert.AreEqual("not-found", Describe(await primary.PutPolicyAsync(scope, LinkingSuggest.Instance, 1)));
        Assert.AreEqual("created", Describe(await primary.PutPolicyAsync(scope, LinkingSuggest.Instance, null)));
        Assert.AreEqual("conflict:1", Describe(await competing.PutPolicyAsync(scope, LinkingOff.Instance, null)));
        var auto = new LinkingAutoMerge(new ContactAutoMergeRules([ContactIdentityKind.Phone, ContactIdentityKind.Email], 2, false));
        Assert.AreEqual("applied", Describe(await competing.PutPolicyAsync(scope, auto, 1)));
        Assert.AreEqual("conflict:2", Describe(await primary.PutPolicyAsync(scope, LinkingOff.Instance, 1)));
        var stored = await primary.GetPolicyAsync(scope);
        Assert.AreEqual(2, stored!.Revision);
        Assert.AreEqual(scope, stored.Scope);
        Assert.AreEqual(auto, stored.Policy.Value);
        Assert.IsNull(await primary.GetPolicyAsync(other), "policies never cross scopes");
    }

    public static async Task VerifySuggestionsAsync(IContactStore contacts, IContactLinkingStore primary, IContactLinkingStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "linking-suggestions");
        var other = TestScopes.Create(workspaceId: "linking-suggestions-other");
        foreach (var (id, labels) in new[] { ("anon-1", new[] { "vip" }), ("anon-2", Array.Empty<string>()), ("anon-3", ["vip"]), ("account", ["vip"]) })
            Assert.IsTrue(ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(contacts, scope, ContactStoreContract.NewContact(id, labels: labels))).StartsWith("applied", StringComparison.Ordinal));

        var first = Suggestion("anon-1", "account");
        Assert.AreEqual("created", Describe(await primary.CreateSuggestionAsync(scope, first)));
        var duplicate = await competing.CreateSuggestionAsync(scope, Suggestion("anon-1", "account"));
        Assert.AreEqual("applied", Describe(duplicate), "one pending suggestion per pair");
        Assert.AreEqual(first.Id, ((StoreApplied<StoredLinkSuggestionWrite>)duplicate.Value!).Value.Suggestion.Suggestion.Id);
        Assert.AreEqual("conflict:1", Describe(await competing.CreateSuggestionAsync(scope, Suggestion("anon-2", "account", first.Id))), "identifiers are unique");
        Assert.AreEqual("created", Describe(await primary.CreateSuggestionAsync(scope, Suggestion("anon-2", "account"))));
        Assert.AreEqual("created", Describe(await primary.CreateSuggestionAsync(scope, Suggestion("anon-3", "account"))));
        Assert.AreEqual("created", Describe(await primary.CreateSuggestionAsync(other, Suggestion("anon-1", "account"))), "pairs are per scope");

        var loaded = await competing.GetSuggestionAsync(scope, first.Id);
        Assert.AreEqual(1, loaded!.Revision);
        Assert.AreEqual(first.Evidence, loaded.Suggestion.Evidence);
        Assert.AreEqual(Actor, loaded.Suggestion.CreatedBy);
        Assert.IsNull(await competing.GetSuggestionAsync(other, first.Id));

        // Compare-and-set decisions; a decided pair can be suggested again.
        var rejected = first.Decide(ContactLinkSuggestionStatus.Rejected, Actor, ContactStoreContract.At.AddMinutes(1));
        Assert.AreEqual("not-found", Describe(await primary.UpdateSuggestionAsync(scope, Suggestion("anon-1", "account"), 1)));
        Assert.AreEqual("conflict:1", Describe(await primary.UpdateSuggestionAsync(scope, rejected, 2)));
        Assert.AreEqual("applied", Describe(await primary.UpdateSuggestionAsync(scope, rejected, 1)));
        Assert.AreEqual("conflict:2", Describe(await competing.UpdateSuggestionAsync(scope, first.Decide(ContactLinkSuggestionStatus.Accepted, Actor, ContactStoreContract.At), 1)));
        var decided = await competing.GetSuggestionAsync(scope, first.Id);
        Assert.AreEqual(ContactLinkSuggestionStatus.Rejected, decided!.Suggestion.Status);
        Assert.AreEqual(2, decided.Revision);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => primary.UpdateSuggestionAsync(scope, first, 2), "decided suggestions never become pending");
        Assert.AreEqual("created", Describe(await competing.CreateSuggestionAsync(scope, Suggestion("anon-1", "account"))));

        // Listing: by status, ordered by identifier, filtered on both contacts, paged.
        var all = await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(), AllowAllFilter.Instance);
        Assert.HasCount(3, all.Suggestions);
        CollectionAssert.AreEqual(all.Suggestions.Select(item => item.Suggestion.Id.ToString("D")).Order(StringComparer.Ordinal).ToArray(),
            all.Suggestions.Select(item => item.Suggestion.Id.ToString("D")).ToArray());
        Assert.IsNull(all.Next);
        var vip = new HasLabelFilter(new LabelId("vip"));
        var visible = await competing.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(), vip);
        CollectionAssert.AreEquivalent(new[] { "anon-1", "anon-3" }, visible.Suggestions.Select(item => item.Suggestion.Candidate.Value).ToArray());
        Assert.IsEmpty((await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(), DenyAllFilter.Instance)).Suggestions);
        var forContact = await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(contact: new ContactId("anon-2")), AllowAllFilter.Instance);
        Assert.AreEqual("anon-2", forContact.Suggestions.Single().Suggestion.Candidate.Value);
        var rejectedPage = await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(ContactLinkSuggestionStatus.Rejected), AllowAllFilter.Instance);
        Assert.AreEqual(first.Id, rejectedPage.Suggestions.Single().Suggestion.Id);
        var pageOne = await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(limit: 2), AllowAllFilter.Instance);
        Assert.HasCount(2, pageOne.Suggestions);
        Assert.IsNotNull(pageOne.Next);
        var pageTwo = await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(limit: 2, after: pageOne.Next), AllowAllFilter.Instance);
        Assert.HasCount(1, pageTwo.Suggestions);
        Assert.IsNull(pageTwo.Next);
        CollectionAssert.AreEquivalent(all.Suggestions.Select(item => item.Suggestion.Id).ToArray(),
            pageOne.Suggestions.Concat(pageTwo.Suggestions).Select(item => item.Suggestion.Id).ToArray());

        // Suggestions whose contacts no longer exist are never listed.
        var anon2 = await contacts.GetAsync(scope, new ContactId("anon-2"));
        Assert.IsTrue(ContactStoreContract.Describe(await contacts.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(anon2!.Id, anon2.Revision)]))).StartsWith("applied", StringComparison.Ordinal));
        Assert.HasCount(2, (await primary.ListSuggestionsAsync(scope, new ContactLinkSuggestionQuery(), AllowAllFilter.Instance)).Suggestions);
    }

    /// <summary>Retention over a store with no other suggestions: only decided suggestions older than the cutoff are pruned, oldest first.</summary>
    public static async Task VerifyPruneAsync(IContactStore contacts, IContactLinkingStore store)
    {
        var scope = TestScopes.Create(workspaceId: "linking-prune");
        foreach (var id in new[] { "p-1", "p-2", "p-3", "account" })
            Assert.IsTrue(ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(contacts, scope, ContactStoreContract.NewContact(id))).StartsWith("applied", StringComparison.Ordinal));
        var old = Suggestion("p-1", "account");
        var newer = Suggestion("p-2", "account");
        var pending = Suggestion("p-3", "account");
        foreach (var suggestion in new[] { old, newer, pending })
            Assert.AreEqual("created", Describe(await store.CreateSuggestionAsync(scope, suggestion)));
        Assert.AreEqual("applied", Describe(await store.UpdateSuggestionAsync(scope, old.Decide(ContactLinkSuggestionStatus.Rejected, Actor, ContactStoreContract.At.AddMinutes(1)), 1)));
        Assert.AreEqual("applied", Describe(await store.UpdateSuggestionAsync(scope, newer.Decide(ContactLinkSuggestionStatus.Superseded, null, ContactStoreContract.At.AddMinutes(5)), 1)));

        Assert.AreEqual(0, await store.PruneDecidedSuggestionsAsync(ContactStoreContract.At.AddMinutes(1), 100), "the cutoff is exclusive");
        Assert.AreEqual(1, await store.PruneDecidedSuggestionsAsync(ContactStoreContract.At.AddDays(1), 1), "oldest first, bounded");
        Assert.IsNull(await store.GetSuggestionAsync(scope, old.Id));
        Assert.IsNotNull(await store.GetSuggestionAsync(scope, newer.Id));
        Assert.AreEqual(1, await store.PruneDecidedSuggestionsAsync(ContactStoreContract.At.AddDays(1), 100));
        Assert.AreEqual(0, await store.PruneDecidedSuggestionsAsync(DateTimeOffset.MaxValue, 100));
        Assert.IsNotNull(await store.GetSuggestionAsync(scope, pending.Id), "pending suggestions are never pruned");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.PruneDecidedSuggestionsAsync(ContactStoreContract.At, 0));
    }
}
