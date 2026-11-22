using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.StoreContracts;

/// <summary>Seeded random grants, principals and resources for filter/point-check parity.</summary>
public static class RandomAccessFixture
{
    public static readonly string[] Teams = ["t0", "t1", "t2", "t3"];
    public static readonly string[] Inboxes = ["i0", "i1", "i2"];
    public static readonly string[] Users = ["u0", "u1", "u2", "u3"];
    public static readonly string[] Labels = ["l0", "l1", "l2"];
    public static readonly string[] Tiers = ["vip", "standard", "enterprise"];

    public static readonly Permission[] Permissions =
    [
        AccessPermissions.ConversationRead, AccessPermissions.ConversationReply, AccessPermissions.NoteWrite,
        AccessPermissions.ContactExport, new("triage:run"),
    ];

    public static AccessRole CustomRole { get; } = new(new RoleId("triager"), "Triager", [new Permission("triage:run"), AccessPermissions.ConversationRead]);

    /// <summary>A custom role that is created and then deleted: allow grants on it never apply, deny grants on it deny everything.</summary>
    public static AccessRole DeletedRole { get; } = new(new RoleId("retired"), "Retired", [AccessPermissions.NoteWrite]);

    public static IReadOnlyList<AccessGrant> Grants(Random random, TicketScope scope, DateTimeOffset now, int count)
    {
        var partition = AccessPartition.From(scope);
        AccessRole[] roles = [.. BuiltInRoles.All, CustomRole, CustomRole];
        var grants = new List<AccessGrant>();
        for (var index = 0; index < count; index++)
        {
            GrantSubject subject = random.Next(6) switch
            {
                0 or 1 => new UserId(Pick(random, Users)),
                2 or 3 or 4 => new TeamId(Pick(random, Teams)),
                _ => new ApiClientId("client"),
            };
            var role = Pick(random, roles);
            GrantScope grantScope = random.Next(5) switch
            {
                0 => TenantGrantScope.Instance,
                1 => new WorkspaceGrantScope(scope.WorkspaceId),
                2 => new TeamGrantScope(scope.WorkspaceId, new TeamId(Pick(random, Teams))),
                3 => new InboxGrantScope(scope.WorkspaceId, new InboxId(Pick(random, Inboxes))),
                _ => new WorkspaceGrantScope(new WorkspaceId("elsewhere")),
            };
            var effect = random.Next(4) == 0 ? GrantEffect.Deny : GrantEffect.Allow;
            var condition = random.Next(3) == 0 ? null : Condition(random, random.Next(1, 4));
            (DateTimeOffset? from, DateTimeOffset? until) = random.Next(6) switch
            {
                0 => ((DateTimeOffset?)now.AddHours(-2), (DateTimeOffset?)now.AddHours(-1)),
                1 => (now.AddHours(1), null),
                2 => (now.AddHours(-1), now.AddHours(1)),
                _ => ((DateTimeOffset?)null, (DateTimeOffset?)null),
            };
            var kind = GrantKind.Standard;
            string? justification = null;
            switch (random.Next(12))
            {
                case 0 when subject.Value is not TeamId:
                    // Break-glass: an expiring allow with a justification.
                    (effect, kind, justification) = (GrantEffect.Allow, GrantKind.BreakGlass, "Randomized incident coverage");
                    (from, until) = random.Next(3) == 0 ? (now.AddHours(-2), now.AddHours(-1)) : (now.AddMinutes(-5), now.AddMinutes(30));
                    break;
                case 1:
                    // A deny on a deleted role at a narrow scope (it denies every permission there).
                    (role, effect) = (DeletedRole, GrantEffect.Deny);
                    grantScope = new InboxGrantScope(scope.WorkspaceId, new InboxId(Pick(random, Inboxes)));
                    break;
                case 2:
                    (role, effect) = (DeletedRole, GrantEffect.Allow);
                    break;
            }
            grants.Add(new AccessGrant(new GrantId($"r{index:D3}"), partition, subject, role.Id, grantScope, effect, condition, from, until, justification, kind));
        }
        return grants;
    }

    public static AccessCondition Condition(Random random, int depth)
    {
        if (depth > 1)
        {
            switch (random.Next(5))
            {
                case 0:
                    return new AndCondition(Enumerable.Range(0, random.Next(1, 4)).Select(_ => Condition(random, depth - 1)).ToArray());
                case 1:
                    return new OrCondition(Enumerable.Range(0, random.Next(1, 4)).Select(_ => Condition(random, depth - 1)).ToArray());
                case 2:
                    return new NotCondition(Condition(random, depth - 1));
            }
        }
        return random.Next(7) switch
        {
            0 => new AssigneeIsSelfCondition(),
            1 => new TeamInPrincipalTeamsCondition(),
            2 => new AttributeEqualsCondition("tier", Pick(random, Tiers)),
            3 => new AttributeInCondition("tier", Pick(random, Tiers), Pick(random, Tiers)),
            4 => new HasLabelCondition(new LabelId(Pick(random, Labels))),
            5 => new TimeWindowCondition(new(random.Next(24), 0), new(random.Next(24), 30)),
            _ => new AttributeEqualsCondition("region", "eu"),
        };
    }

    public static IReadOnlyList<PrincipalRef> Principals(Random random, TicketScope scope)
    {
        var principals = new List<PrincipalRef>();
        foreach (var user in Users)
            principals.Add(TestPrincipals.User(scope, user, [.. Teams.Where(_ => random.Next(3) == 0)]));
        principals.Add(TestPrincipals.ApiClient(scope, "client"));
        principals.Add(TestPrincipals.Automation(scope));
        return principals;
    }

    public static IReadOnlyList<AccessResource> Resources(Random random, TicketScope scope, int count)
    {
        var scopes = TestScopes.LevelVariants(scope);
        var resources = new List<AccessResource>();
        for (var index = 0; index < count; index++)
        {
            var resourceScope = random.Next(10) == 0 ? Pick(random, scopes) : scope;
            var attributes = new Dictionary<string, string>();
            if (random.Next(4) != 0)
                attributes["tier"] = Pick(random, Tiers);
            if (random.Next(2) == 0)
                attributes["region"] = random.Next(2) == 0 ? "eu" : "us";
            resources.Add(new AccessResource(
                resourceScope,
                $"resource-{index}",
                random.Next(4) == 0 ? null : new TeamId(Pick(random, Teams)),
                random.Next(4) == 0 ? null : new InboxId(Pick(random, Inboxes)),
                random.Next(3) == 0 ? null : new UserId(Pick(random, Users)),
                random.Next(5) == 0 ? null : Labels.Where(_ => random.Next(2) == 0).Select(label => new LabelId(label)).ToArray(),
                attributes));
        }
        return resources;
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];
}
