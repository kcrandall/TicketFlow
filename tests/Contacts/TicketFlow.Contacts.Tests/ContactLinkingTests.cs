using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Contacts.StoreContracts;
using TicketFlow.Core;

namespace TicketFlow.Contacts.Tests;

[TestClass]
public sealed class ContactLinkingTests
{
    private static readonly ExternalAccountRef Account = new("acme-portal", "user-42");

    private static ContactIdentityAddress Visitor(string id) => ContactIdentityAddress.Parse(ContactIdentityKind.WidgetVisitor, id);

    public static string Describe(ContactLinkResult result) => result switch
    {
        ContactLinked linked => $"linked:{linked.Contact.Id}",
        ContactMergedInto merged => $"merged:{merged.Source}->{merged.Target.Id}",
        ContactAlreadyLinked already => $"already:{already.Contact.Id}",
        ContactLinkConflict conflict => $"conflict:{conflict.Reason}:" + string.Join(",", conflict.Candidates),
        ContactRefused refused => $"refused:{refused.Reason}",
    };

    private static CustomerResolved Resolved(CustomerResolution resolution) => resolution switch
    {
        CustomerResolved resolved => resolved,
        ContactLinkConflict conflict => throw new AssertFailedException($"conflict {conflict.Reason}"),
        ContactRefused refused => throw new AssertFailedException($"refused {refused.Reason} {string.Join(";", refused.Errors)}"),
    };

    private static ContactLinkRequest ByEvidence(ContactIdentityAddress evidence, ExternalAccountRef? account = null) => new(account ?? Account, "acme-login", evidence: evidence);

    private static ContactLinkRequest ByContact(string id, ExternalAccountRef? account = null) => new(account ?? Account, "acme-login", new ContactId(id));

    [TestMethod]
    public void RequestsValidateInputs()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ExternalAccountRef("Acme", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExternalAccountRef("acme", ""));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactLinkRequest(Account, "acme-login"));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactLinkRequest(Account, "acme-login", new ContactId("a"), Visitor("v")));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactLinkRequest(Account, "Bad Source", new ContactId("a")));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactLinkRequest(Account, "acme-login", evidence: Account.Address));
        Assert.ThrowsExactly<ArgumentException>(() => new CustomerClaim(Visitor("v"), true));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactAutoMergeRules([ContactIdentityKind.ExternalUser]));
        Assert.ThrowsExactly<ArgumentException>(() => new ContactAutoMergeRules([]));
        Assert.DoesNotContain("user-42", Account.ToString());
    }

    [TestMethod]
    public void PolicyCodecRoundTripsAndRejectsUnknownShapes()
    {
        ContactLinkingPolicy[] policies = [LinkingOff.Instance, LinkingSuggest.Instance,
            new LinkingAutoMerge(new ContactAutoMergeRules([ContactIdentityKind.Phone, ContactIdentityKind.Email, ContactIdentityKind.Email], 2, false))];
        foreach (var policy in policies)
            Assert.AreEqual(policy.Value, ContactLinkingCodec.DeserializePolicy(ContactLinkingCodec.SerializePolicy(policy)).Value);
        Assert.AreEqual("""{"v":1,"k":"auto-merge","kinds":[1,2],"min":2,"unverified":false}""", ContactLinkingCodec.SerializePolicy(policies[2]));
        foreach (var bad in new[] { """{"v":2,"k":"off"}""", """{"v":1,"k":"sometimes"}""", """{"v":1,"k":"off","x":1}""", """{"v":1,"k":"off","k":"off"}""",
            """{"v":1,"k":"auto-merge","kinds":[5],"min":1,"unverified":true}""", """{"v":1,"k":"auto-merge","kinds":[1],"min":0,"unverified":true}""", "[]", "nope" })
            Assert.ThrowsExactly<FormatException>(() => ContactLinkingCodec.DeserializePolicy(bad), bad);
    }

    [TestMethod]
    public async Task InMemoryLinkingStoreSatisfiesTheContract()
    {
        var contacts = new InMemoryContactStore();
        var store = new InMemoryContactLinkingStore(contacts);
        await ContactLinkingStoreContract.VerifyPolicyAsync(store, store);
        await ContactLinkingStoreContract.VerifySuggestionsAsync(contacts, store, store);
    }

    [TestMethod]
    public async Task LinkAttachesTheAccountWhenNoContactHoldsIt()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var anonymous = await fixture.CreateAsync("anon", identities: [fixture.Claimed(Visitor("v_1"))]);
        var result = await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_1")));
        Assert.AreEqual("linked:anon", Describe(result));
        var stored = await fixture.Store.GetAsync(fixture.Scope, anonymous.Id);
        var identity = stored!.Contact.Identities.Single(item => item.Address == Account.Address);
        Assert.IsTrue(identity.Verification.IsVerified);
        Assert.AreEqual("acme-login", identity.Verification.Source);
        var audit = fixture.Store.AuditEvents.Single(item => item.Kind == ContactAuditKind.Linked);
        Assert.AreEqual(anonymous.Id, audit.Contact);
        Assert.AreEqual(AccessActorKind.ApiClient, audit.Actor.Kind);
        Assert.AreEqual(ContactChangeKind.Updated, fixture.Observer.Changes[^1].Event.Kind);

        // Idempotent by evidence and by contact.
        Assert.AreEqual("already:anon", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_1")))));
        Assert.AreEqual("already:anon", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByContact("anon"))));
        Assert.AreEqual(1, fixture.Store.AuditEvents.Count(item => item.Kind == ContactAuditKind.Linked));
    }

    [TestMethod]
    public async Task LinkMergesTheAnonymousContactIntoTheAccountContact()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var account = await fixture.CreateAsync("account", "Ada", [fixture.Verified(Account.Address)]);
        var anonymous = await fixture.CreateAsync("anon", identities: [fixture.Claimed(Visitor("v_1")), fixture.Claimed(ContactFixture.Email("ada@example.com"))], labels: ["web"]);

        var result = await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_1")));
        Assert.AreEqual("merged:anon->account", Describe(result));
        var merged = (ContactMergedInto)result.Value!;
        var target = await fixture.Store.GetAsync(fixture.Scope, account.Id);
        Assert.HasCount(3, target!.Contact.Identities);
        CollectionAssert.Contains(target.Contact.Labels.ToList(), new LabelId("web"));
        Assert.AreEqual(account.Id, (await fixture.Store.GetAsync(fixture.Scope, anonymous.Id))!.Contact.MergedInto);
        Assert.IsNotNull(await fixture.Store.GetMergeAsync(fixture.Scope, merged.MergeId));
        Assert.IsTrue(fixture.Store.AuditEvents.Any(item => item.Kind == ContactAuditKind.Merged && item.Merge == merged.MergeId));
        CollectionAssert.AreEqual(new[] { ContactChangeKind.Merged, ContactChangeKind.Updated }, fixture.Observer.Changes.TakeLast(2).Select(change => change.Event.Kind).ToArray());

        // Retrying with the tombstone or the moved evidence is a no-op.
        Assert.AreEqual("already:account", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByContact("anon"))));
        Assert.AreEqual("already:account", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_1")))));

        // Unmerge (the documented undo) restores the anonymous contact.
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.UnmergeAsync(fixture.Owner, merged.MergeId)));
        var restored = await fixture.Store.GetAsync(fixture.Scope, anonymous.Id);
        Assert.IsNull(restored!.Contact.MergedInto);
        Assert.HasCount(2, restored.Contact.Identities);
        Assert.HasCount(1, (await fixture.Store.GetAsync(fixture.Scope, account.Id))!.Contact.Identities);
    }

    [TestMethod]
    public async Task LinkRefusesConflictsAndUnauthorizedActors()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.CreateAsync("other-account", identities: [fixture.Verified(Account.Address)]);
        await fixture.CreateAsync("holder", identities: [fixture.Verified(ContactIdentityAddress.Parse(ContactIdentityKind.ExternalUser, "user-7", "acme-portal")),
            fixture.Claimed(Visitor("v_2"))]);
        await fixture.CreateAsync("twin-1", identities: [fixture.Claimed(Visitor("v_shared"))]);
        await fixture.CreateAsync("twin-2", identities: [fixture.Claimed(Visitor("v_shared"))]);
        await fixture.CreateAsync("anon", identities: [fixture.Claimed(Visitor("v_3"))]);

        Assert.AreEqual("conflict:OtherAccount:holder", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_2")))));
        Assert.AreEqual("conflict:AmbiguousEvidence:twin-1,twin-2", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_shared")))));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_none"), new ExternalAccountRef("acme-portal", "nobody")))));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByContact("missing"))));

        // contact:link is required; crm can read and create but not link; supervisors can merge but not link.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Linking.LinkAccountAsync(fixture.Crm, ByEvidence(Visitor("v_3")))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Linking.LinkAccountAsync(fixture.Supervisor, ByEvidence(Visitor("v_3")))));

        // Linking needs contact:merge when a merge is required.
        var linkOnly = await fixture.CustomRoleAsync("link-only", ContactPermissions.Read, ContactPermissions.Link);
        await fixture.GrantAsync(new ApiClientId("link-only"), linkOnly);
        var linker = new ApiClientPrincipal(fixture.Scope, new ApiClientId("link-only"));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Linking.LinkAccountAsync(linker, ByEvidence(Visitor("v_3")))));
        Assert.IsNull((await fixture.Store.GetAsync(fixture.Scope, new ContactId("anon")))!.Contact.MergedInto);

        // A masked external-id field cannot be probed through linking.
        await fixture.PolicyAsync(new AccessFieldPolicy(ContactFieldPaths.ExternalId, [ContactPermissions.Read], [], ContactPermissions.Reveal));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Linking.LinkAccountAsync(fixture.Portal, ByEvidence(Visitor("v_3")))));
    }

    [TestMethod]
    public async Task ConcurrentLinksOfOneAccountConvergeOnOneAccountContact()
    {
        using var fixture = await ContactFixture.CreateAsync();
        for (var round = 0; round < 10; round++)
        {
            var account = new ExternalAccountRef("acme-portal", $"race-{round}");
            var ids = Enumerable.Range(0, 4).Select(index => $"r{round}-anon-{index}").ToList();
            foreach (var id in ids)
                await fixture.CreateAsync(id, identities: [fixture.Claimed(Visitor($"v_{id}"))]);
            var scopes = ids.Select(_ => fixture.Provider.CreateScope()).ToList();
            try
            {
                var results = await Task.WhenAll(ids.Select((id, index) => Task.Run(() =>
                    scopes[index].ServiceProvider.GetRequiredService<IContactLinkingService>().LinkAccountAsync(fixture.Portal, ByEvidence(Visitor($"v_{id}"), account)))));
                Assert.AreEqual(1, results.Count(result => result.Value is ContactLinked), $"round {round}: " + string.Join(" ", results.Select(Describe)));
                Assert.AreEqual(3, results.Count(result => result.Value is ContactMergedInto), $"round {round}");
                var owners = await fixture.Store.FindByIdentityAsync(fixture.Scope, account.Address);
                var winner = owners.Single().Contact;
                foreach (var id in ids.Where(id => id != winner.Value))
                    Assert.AreEqual(winner, (await fixture.Store.GetAsync(fixture.Scope, new ContactId(id)))!.Contact.MergedInto, $"round {round}");
                Assert.HasCount(5, (await fixture.Store.GetAsync(fixture.Scope, winner))!.Contact.Identities);
            }
            finally
            {
                foreach (var scope in scopes)
                    scope.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task ResolveCustomerEnsuresOneAccountContactWithClaims()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var email = ContactFixture.Email("ada@example.com");
        var phone = ContactFixture.Phone("+44 20 7946 0000");
        var request = new CustomerResolveRequest(Account, "acme-login", [new CustomerClaim(email, true), new CustomerClaim(phone, false)], "Ada");
        var first = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, request));
        Assert.IsTrue(first.Created);
        Assert.AreEqual("Ada", first.Contact.Profile.DisplayName);
        var stored = (await fixture.Store.GetAsync(fixture.Scope, first.Contact.Id))!.Contact;
        Assert.IsTrue(stored.Identities.Single(item => item.Address == email).Verification.IsVerified);
        Assert.IsFalse(stored.Identities.Single(item => item.Address == phone).Verification.IsVerified);

        var second = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, request));
        Assert.IsFalse(second.Created);
        Assert.AreEqual(first.Contact.Id, second.Contact.Id);
        Assert.AreEqual(first.Contact.Revision, second.Contact.Revision, "nothing to attach on repeat logins");

        // A later verified phone claim upgrades the claimed identity.
        var upgraded = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, new CustomerResolveRequest(Account, "acme-login", [new CustomerClaim(phone, true)])));
        Assert.IsTrue((await fixture.Store.GetAsync(fixture.Scope, upgraded.Contact.Id))!.Contact.Identities.Single(item => item.Address == phone).Verification.IsVerified);

        // Creation needs contact:create as well as contact:link.
        var linkOnly = await fixture.CustomRoleAsync("resolve-link-only", ContactPermissions.Read, ContactPermissions.Link);
        await fixture.GrantAsync(new ApiClientId("resolve-link-only"), linkOnly);
        Assert.IsInstanceOfType<ContactRefused>((await fixture.Linking.ResolveCustomerAsync(new ApiClientPrincipal(fixture.Scope, new ApiClientId("resolve-link-only")),
            new CustomerResolveRequest(new ExternalAccountRef("acme-portal", "new-user"), "acme-login"))).Value);
    }

    [TestMethod]
    public async Task ConcurrentResolvesCreateOneContact()
    {
        using var fixture = await ContactFixture.CreateAsync();
        for (var round = 0; round < 10; round++)
        {
            var account = new ExternalAccountRef("acme-portal", $"login-{round}");
            var scopes = Enumerable.Range(0, 4).Select(_ => fixture.Provider.CreateScope()).ToList();
            try
            {
                var results = await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                    scope.ServiceProvider.GetRequiredService<IContactLinkingService>().ResolveCustomerAsync(fixture.Portal, new CustomerResolveRequest(account, "acme-login")))));
                var resolved = results.Select(Resolved).ToList();
                Assert.HasCount(1, resolved.Select(item => item.Contact.Id).Distinct(), $"round {round}");
                Assert.AreEqual(1, resolved.Count(item => item.Created), $"round {round}");
            }
            finally
            {
                foreach (var scope in scopes)
                    scope.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task ResolveCustomerLinksTheSessionContact()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var account = await fixture.CreateAsync("account", identities: [fixture.Verified(Account.Address)]);
        await fixture.CreateAsync("anon", identities: [fixture.Claimed(Visitor("v_9"))]);
        var resolved = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, new CustomerResolveRequest(Account, "acme-login", linkEvidence: Visitor("v_9"))));
        Assert.AreEqual(account.Id, resolved.Contact.Id);
        Assert.AreEqual(new ContactId("anon"), resolved.Merged.Single().Source);

        // Without an existing account contact the session contact becomes the account contact.
        await fixture.CreateAsync("anon-2", identities: [fixture.Claimed(Visitor("v_10"))]);
        var fresh = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal,
            new CustomerResolveRequest(new ExternalAccountRef("acme-portal", "fresh"), "acme-login", linkContact: new ContactId("anon-2"))));
        Assert.AreEqual("anon-2", fresh.Contact.Id.Value);
        Assert.IsFalse(fresh.Created);
        Assert.IsEmpty(fresh.Merged);
    }

    [TestMethod]
    public async Task PolicyOffDoesNothingAndSuggestStoresSuggestions()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var email = ContactFixture.Email("ada@example.com");
        await fixture.CreateAsync("widget-ada", identities: [fixture.Claimed(email)]);
        var request = new CustomerResolveRequest(Account, "acme-login", [new CustomerClaim(email, true)]);

        var off = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, request));
        Assert.IsEmpty(off.Suggestions);
        Assert.IsEmpty(off.Merged);
        Assert.AreEqual(0L, ContactFixture.Applied(await fixture.Linking.GetPolicyAsync(fixture.Owner)).Revision);
        Assert.IsInstanceOfType<LinkingOff>(ContactFixture.Applied(await fixture.Linking.GetPolicyAsync(fixture.Owner)).Policy.Value);

        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Linking.SetPolicyAsync(fixture.Supervisor, LinkingSuggest.Instance, null)));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Linking.SetPolicyAsync(fixture.Owner, LinkingSuggest.Instance, null)));
        Assert.IsTrue(fixture.Store.AuditEvents.Any(item => item.Kind == ContactAuditKind.LinkingPolicyChanged));

        var suggested = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, request));
        var id = suggested.Suggestions.Single();
        Assert.IsEmpty(suggested.Merged);
        Assert.AreEqual(id, Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, request)).Suggestions.Single(), "pending suggestions are reused");
        Assert.AreEqual(1, fixture.Store.AuditEvents.Count(item => item.Kind == ContactAuditKind.LinkSuggested && item.Suggestion == id));

        var page = ContactFixture.Applied(await fixture.Linking.ListSuggestionsAsync(fixture.Owner, new ContactLinkSuggestionQuery()));
        var view = page.Suggestions.Single();
        Assert.AreEqual(new ContactId("widget-ada"), view.Candidate);
        Assert.AreEqual(suggested.Contact.Id, view.Account);
        Assert.AreEqual(ContactIdentityKind.Email, view.EvidenceKind);
        Assert.IsTrue(view.EvidenceVerified);
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Linking.ListSuggestionsAsync(fixture.Agent, new ContactLinkSuggestionQuery())));

        var accepted = (ContactLinkSuggestionAccepted)(await fixture.Linking.AcceptSuggestionAsync(fixture.Owner, id, view.Revision)).Value!;
        Assert.AreEqual(ContactLinkSuggestionStatus.Accepted, accepted.Suggestion.Status);
        Assert.AreEqual(accepted.Merge.MergeId, accepted.Suggestion.Merge);
        Assert.AreEqual(suggested.Contact.Id, (await fixture.Store.GetAsync(fixture.Scope, new ContactId("widget-ada")))!.Contact.MergedInto);
        Assert.IsTrue(fixture.Store.AuditEvents.Any(item => item.Kind == ContactAuditKind.LinkSuggestionAccepted && item.Suggestion == id && item.Merge == accepted.Merge.MergeId));
        Assert.IsInstanceOfType<ContactRefused>((await fixture.Linking.AcceptSuggestionAsync(fixture.Owner, id, accepted.Suggestion.Revision)).Value);
    }

    [TestMethod]
    public async Task SuggestionsCanBeRejectedAndAreAccessFiltered()
    {
        using var fixture = await ContactFixture.CreateAsync();
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Linking.SetPolicyAsync(fixture.Owner, LinkingSuggest.Instance, null)));
        var email = ContactFixture.Email("grace@example.com");
        var phone = ContactFixture.Phone("+1 202 555 0100");
        await fixture.CreateAsync("vip-claim", identities: [fixture.Claimed(email)], labels: ["vip"]);
        await fixture.CreateAsync("plain-claim", identities: [fixture.Claimed(phone)]);
        var resolved = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal,
            new CustomerResolveRequest(Account, "acme-login", [new CustomerClaim(email, true), new CustomerClaim(phone, false)])));
        Assert.HasCount(2, resolved.Suggestions);

        // A reviewer who may read only vip contacts sees no suggestion: the account contact is not vip.
        var reviewer = await fixture.CustomRoleAsync("reviewer", ContactPermissions.Read, ContactPermissions.Link, ContactPermissions.Merge);
        var linker = await fixture.CustomRoleAsync("linker", ContactPermissions.Link);
        var vipReader = await fixture.CustomRoleAsync("vip-reader", ContactPermissions.Read);
        await fixture.GrantAsync(new UserId("reviewer"), linker);
        await fixture.GrantAsync(new UserId("reviewer"), vipReader, condition: new HasLabelCondition(new LabelId("vip")));
        await fixture.GrantAsync(new UserId("reviewer-all"), reviewer);
        var limited = new UserPrincipal(fixture.Scope, new UserId("reviewer"));
        var broad = new UserPrincipal(fixture.Scope, new UserId("reviewer-all"));
        Assert.IsEmpty(ContactFixture.Applied(await fixture.Linking.ListSuggestionsAsync(limited, new ContactLinkSuggestionQuery())).Suggestions);
        var all = ContactFixture.Applied(await fixture.Linking.ListSuggestionsAsync(broad, new ContactLinkSuggestionQuery()));
        Assert.HasCount(2, all.Suggestions);

        var plain = all.Suggestions.Single(item => item.Candidate == new ContactId("plain-claim"));
        Assert.IsFalse(plain.EvidenceVerified, "claimed claims only ever suggest");
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Linking.RejectSuggestionAsync(limited, plain.Id, plain.Revision)));
        Assert.AreEqual("refused:Conflict:1", ContactFixture.Describe(await fixture.Linking.RejectSuggestionAsync(broad, plain.Id, 7)));
        var rejected = ContactFixture.Applied(await fixture.Linking.RejectSuggestionAsync(broad, plain.Id, plain.Revision, "different person"));
        Assert.AreEqual(ContactLinkSuggestionStatus.Rejected, rejected.Status);
        var audit = fixture.Store.AuditEvents.Single(item => item.Kind == ContactAuditKind.LinkSuggestionRejected);
        Assert.AreEqual("different person", audit.Justification);
        Assert.AreEqual(plain.Id, audit.Suggestion);
        Assert.IsNull((await fixture.Store.GetAsync(fixture.Scope, new ContactId("plain-claim")))!.Contact.MergedInto);
        Assert.HasCount(1, ContactFixture.Applied(await fixture.Linking.ListSuggestionsAsync(broad, new ContactLinkSuggestionQuery(ContactLinkSuggestionStatus.Rejected))).Suggestions);
    }

    [TestMethod]
    public async Task AutoMergeRequiresVerifiedEvidenceAndTheMinimumRules()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var rules = new ContactAutoMergeRules([ContactIdentityKind.Email]);
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Linking.SetPolicyAsync(fixture.Owner, new LinkingAutoMerge(rules), null)));
        var email = ContactFixture.Email("ada@example.com");
        var phone = ContactFixture.Phone("+44 20 7946 0000");
        var other = ContactFixture.Email("other@example.com");
        await fixture.CreateAsync("email-claim", identities: [fixture.Claimed(email)]);
        await fixture.CreateAsync("phone-claim", identities: [fixture.Claimed(phone)]);
        await fixture.CreateAsync("verified-elsewhere", identities: [fixture.Claimed(other), fixture.Verified(ContactFixture.Email("v@example.com"))]);
        await fixture.CreateAsync("unverified-claim", identities: [fixture.Claimed(ContactFixture.Email("claimed-only@example.com"))]);

        var resolved = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, new CustomerResolveRequest(Account, "acme-login",
        [
            new CustomerClaim(email, true),                                            // qualifies: verified email, unverified candidate
            new CustomerClaim(phone, true),                                            // verified, but phone is not an auto-merge kind
            new CustomerClaim(other, true),                                            // candidate holds a verified identity
            new CustomerClaim(ContactFixture.Email("claimed-only@example.com"), false), // claimed-only evidence never auto-merges
        ])));
        Assert.AreEqual("email-claim", resolved.Merged.Single().Source.Value);
        Assert.HasCount(3, resolved.Suggestions);
        Assert.AreEqual(resolved.Contact.Id, (await fixture.Store.GetAsync(fixture.Scope, new ContactId("email-claim")))!.Contact.MergedInto);
        foreach (var id in new[] { "phone-claim", "verified-elsewhere", "unverified-claim" })
            Assert.IsNull((await fixture.Store.GetAsync(fixture.Scope, new ContactId(id)))!.Contact.MergedInto, id);
        var merge = fixture.Store.AuditEvents.Single(item => item.Kind == ContactAuditKind.Merged);
        Assert.AreEqual(new ContactId("email-claim"), merge.Contact);
        Assert.AreEqual(AccessActorKind.ApiClient, merge.Actor.Kind);

        // An actor without contact:merge only suggests, even when the rules qualify.
        var noMerge = await fixture.CustomRoleAsync("login-no-merge", ContactPermissions.Read, ContactPermissions.Create, ContactPermissions.Link);
        await fixture.GrantAsync(new ApiClientId("login-no-merge"), noMerge);
        var second = ContactFixture.Email("second@example.com");
        await fixture.CreateAsync("second-claim", identities: [fixture.Claimed(second)]);
        var limited = Resolved(await fixture.Linking.ResolveCustomerAsync(new ApiClientPrincipal(fixture.Scope, new ApiClientId("login-no-merge")),
            new CustomerResolveRequest(new ExternalAccountRef("acme-portal", "second"), "acme-login", [new CustomerClaim(second, true)])));
        Assert.IsEmpty(limited.Merged);
        Assert.HasCount(1, limited.Suggestions);
    }

    [TestMethod]
    public async Task PoliciesNeverCrossScopes()
    {
        using var fixture = await ContactFixture.CreateAsync();
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Linking.SetPolicyAsync(fixture.Owner, new LinkingAutoMerge(new ContactAutoMergeRules([ContactIdentityKind.Email])), null)));
        var otherScope = TestingScopes.Other();
        var email = ContactFixture.Email("ada@example.com");
        Assert.IsTrue(ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(fixture.Store, otherScope,
            ContactStoreContract.NewContact("foreign", identities: [ContactStoreContract.Claimed(email)]))).StartsWith("applied", StringComparison.Ordinal));
        var resolved = Resolved(await fixture.Linking.ResolveCustomerAsync(fixture.Portal, new CustomerResolveRequest(Account, "acme-login", [new CustomerClaim(email, true)])));
        Assert.IsEmpty(resolved.Merged);
        Assert.IsEmpty(resolved.Suggestions);
        Assert.IsNull((await fixture.Store.GetAsync(otherScope, new ContactId("foreign")))!.Contact.MergedInto);
    }

    [TestMethod]
    public async Task UnlinkRemovesTheVerifiedAccount()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var contact = await fixture.CreateAsync("account", identities: [fixture.Verified(Account.Address)]);
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Linking.UnlinkAccountAsync(fixture.Supervisor, contact.Id, 1, Account)));
        Assert.AreEqual("refused:Conflict:1", ContactFixture.Describe(await fixture.Linking.UnlinkAccountAsync(fixture.Owner, contact.Id, 3, Account)));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(await fixture.Linking.UnlinkAccountAsync(fixture.Owner, contact.Id, 1, new ExternalAccountRef("acme-portal", "nope"))));
        var view = ContactFixture.Applied(await fixture.Linking.UnlinkAccountAsync(fixture.Owner, contact.Id, 1, Account, "wrong account"));
        Assert.IsEmpty(view.Identities);
        Assert.AreEqual("wrong account", fixture.Store.AuditEvents.Single(item => item.Kind == ContactAuditKind.Unlinked).Justification);
        Assert.IsEmpty(await fixture.Store.FindByIdentityAsync(fixture.Scope, Account.Address));
    }
}

internal static class TestingScopes
{
    public static TicketScope Other() => TicketFlow.Testing.TestScopes.Create(workspaceId: "other-workspace");
}
