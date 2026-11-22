using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Identity.StoreContracts;

/// <summary>
/// Behavioral contract every <see cref="IStaffIdentityStore"/> must satisfy. <c>primary</c> and <c>competing</c> must be independent
/// clients of the same storage.
/// </summary>
public static class StaffIdentityStoreContract
{
    public static readonly DateTimeOffset At = TestClock.DefaultStart;
    private static readonly AccessActor Actor = new(AccessActorKind.User, "owner");

    public static string Describe(StaffIdentityWriteResult result) => result switch
    {
        StaffIdentityWriteApplied applied => applied.Changed ? "applied" : "unchanged",
        StaffIdentityTaken taken => $"taken:{taken.User}",
        StaffWriteNotFound => "not-found",
        StaffWriteRejected => "rejected",
    };

    public static string Describe(StaffProfileWriteResult result) => result switch
    {
        StaffProfileWriteApplied applied => $"applied:{applied.Profile.Revision}",
        StaffWriteNotFound => "not-found",
        StaffProfileWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
    };

    public static StaffIdentityAuditEvent Audit(TicketScope scope, StaffIdentityAuditKind kind, string user, string? provider = "google") =>
        new(Guid.NewGuid(), At, scope, kind, Actor, new UserId(user), provider);

    private static Task<StaffIdentityWriteResult> LinkAsync(IStaffIdentityStore store, TicketScope scope, string provider, string subject, string user) =>
        store.LinkAsync(scope, new StaffIdentityLink(new StaffIdentityRef(provider, subject), new UserId(user), At, Actor), Audit(scope, StaffIdentityAuditKind.Linked, user, provider));

    public static async Task VerifyLinksAsync(IStaffIdentityStore primary, IStaffIdentityStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "staff-links");
        var other = TestScopes.Create(workspaceId: "staff-links-other");
        var google = new StaffIdentityRef("google", "Sub-1");
        Assert.IsNull(await primary.FindAsync(scope, google));
        Assert.AreEqual("applied", Describe(await LinkAsync(primary, scope, "google", "Sub-1", "ada")));
        Assert.AreEqual("unchanged", Describe(await LinkAsync(competing, scope, "google", "Sub-1", "ada")), "relinking to the same user is idempotent");
        Assert.AreEqual("taken:ada", Describe(await LinkAsync(competing, scope, "google", "Sub-1", "grace")));
        Assert.AreEqual("applied", Describe(await LinkAsync(competing, scope, "google", "sub-1", "grace")), "subjects compare exactly");
        Assert.AreEqual("applied", Describe(await LinkAsync(primary, scope, "entra-acme", "Sub-1", "ada")), "many identities map to one user");
        Assert.AreEqual("applied", Describe(await LinkAsync(primary, other, "google", "Sub-1", "grace")), "mappings are per scope");

        var found = await competing.FindAsync(scope, google);
        Assert.AreEqual(new UserId("ada"), found!.Link.User);
        Assert.AreEqual(scope, found.Scope);
        Assert.AreEqual(At, found.Link.LinkedAt);
        Assert.AreEqual(Actor, found.Link.LinkedBy);
        Assert.AreEqual(new UserId("grace"), (await primary.FindAsync(other, google))!.Link.User);
        CollectionAssert.AreEqual(new[] { "entra-acme", "google" }, (await primary.ListForUserAsync(scope, new UserId("ada"))).Select(link => link.Link.Identity.Provider).ToArray());
        Assert.IsEmpty(await primary.ListForUserAsync(other, new UserId("ada")));

        Assert.AreEqual("taken:ada", Describe(await competing.UnlinkAsync(scope, google, new UserId("grace"), Audit(scope, StaffIdentityAuditKind.Unlinked, "grace"))));
        Assert.AreEqual("applied", Describe(await competing.UnlinkAsync(scope, google, new UserId("ada"), Audit(scope, StaffIdentityAuditKind.Unlinked, "ada"))));
        Assert.AreEqual("not-found", Describe(await primary.UnlinkAsync(scope, google, new UserId("ada"), Audit(scope, StaffIdentityAuditKind.Unlinked, "ada"))));
        Assert.IsNull(await primary.FindAsync(scope, google));
        Assert.IsNotNull(await primary.FindAsync(other, google));

        // Per-user capacity is enforced.
        for (var index = 0; index < IStaffIdentityStore.MaximumIdentitiesPerUser - 1; index++)
            Assert.AreEqual("applied", Describe(await LinkAsync(index % 2 == 0 ? primary : competing, scope, "bulk", $"s{index:D2}", "ada")));
        Assert.HasCount(IStaffIdentityStore.MaximumIdentitiesPerUser, await primary.ListForUserAsync(scope, new UserId("ada")));
        Assert.AreEqual("rejected", Describe(await LinkAsync(primary, scope, "bulk", "overflow", "ada")));
    }

    public static async Task VerifyConcurrentLinksAsync(IStaffIdentityStore primary, IStaffIdentityStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "staff-races");
        for (var round = 0; round < 10; round++)
        {
            var results = await Task.WhenAll(
                LinkAsync(primary, scope, "google", $"race-{round}", "ada"),
                LinkAsync(competing, scope, "google", $"race-{round}", "grace"),
                LinkAsync(primary, scope, "google", $"race-{round}", "linus"));
            Assert.AreEqual(1, results.Count(result => result.Value is StaffIdentityWriteApplied), $"round {round}");
            var owner = (await primary.FindAsync(scope, new StaffIdentityRef("google", $"race-{round}")))!.Link.User;
            Assert.IsTrue(results.Where(result => result.Value is StaffIdentityTaken).All(result => ((StaffIdentityTaken)result.Value!).User == owner), $"round {round}");
        }
    }

    public static async Task VerifyProfilesAsync(IStaffIdentityStore primary, IStaffIdentityStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "staff-profiles");
        var other = TestScopes.Create(workspaceId: "staff-profiles-other");
        var user = new UserId("ada");
        var audit = Audit(scope, StaffIdentityAuditKind.ProfileChanged, "ada", null);
        Assert.IsNull(await primary.GetProfileAsync(scope, user));
        Assert.AreEqual("not-found", Describe(await primary.PutProfileAsync(scope, new StaffProfile(user, "Ada"), At, 1, audit)));
        Assert.AreEqual("applied:1", Describe(await primary.PutProfileAsync(scope, new StaffProfile(user, "Ada", "avatars/ada.png", "ada@example.com"), At, null, audit)));
        Assert.AreEqual("conflict:1", Describe(await competing.PutProfileAsync(scope, new StaffProfile(user, "Other"), At, null, Audit(scope, StaffIdentityAuditKind.ProfileChanged, "ada", null))));
        Assert.AreEqual("applied:2", Describe(await competing.PutProfileAsync(scope, new StaffProfile(user, "Ada L."), At.AddMinutes(1), 1, Audit(scope, StaffIdentityAuditKind.ProfileChanged, "ada", null))));
        Assert.AreEqual("conflict:2", Describe(await primary.PutProfileAsync(scope, new StaffProfile(user, "Stale"), At, 1, Audit(scope, StaffIdentityAuditKind.ProfileChanged, "ada", null))));
        var stored = await primary.GetProfileAsync(scope, user);
        Assert.AreEqual(2, stored!.Revision);
        Assert.AreEqual("Ada L.", stored.Profile.DisplayName);
        Assert.IsNull(stored.Profile.NotificationEmail);
        Assert.AreEqual(At.AddMinutes(1), stored.UpdatedAt);
        Assert.IsNull(await primary.GetProfileAsync(other, user));
    }
}
