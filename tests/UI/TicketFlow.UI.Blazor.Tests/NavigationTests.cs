using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Reporting;
using TicketFlow.UI.Blazor.Components;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Tests;

[TestClass]
public sealed class NavigationTests
{
    private static readonly Type[] Features = [typeof(IConversationService), typeof(IContactService), typeof(IInboxService), typeof(ITeamService), typeof(IAccessAdministrator), typeof(IAutomationRuleService)];

    [TestMethod]
    public void AgentSeesPermittedSectionsOnly()
    {
        using var context = UiTestSupport.Context(services => services.AddStubs(Features));
        var session = UiTestSupport.Session(context, [AccessPermissions.ConversationRead, AccessPermissions.ContactRead, AccessPermissions.InboxRead]);

        var shell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(session));

        var links = shell.FindAll("a.tf-nav__link").Select(link => link.TextContent.Trim()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Dashboard", "Conversations", "Contacts", "Inboxes", "Settings" }, links);
        CollectionAssert.DoesNotContain(links, "Access", "access:manage is required for the Access section.");
        CollectionAssert.DoesNotContain(links, "Teams");
        CollectionAssert.DoesNotContain(links, "Automation");
        Assert.IsEmpty(shell.FindAll(".tf-nav__link.is-disabled"));
    }

    [TestMethod]
    public void UnregisteredFeaturesAreHiddenEvenWithPermission()
    {
        using var context = UiTestSupport.Context(services => services.AddStubs(typeof(IConversationService)));
        var session = UiTestSupport.Session(context, [AccessPermissions.ConversationRead, AccessPermissions.ReportView, AccessPermissions.ContactRead]);

        var shell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(session));

        var links = shell.FindAll("a.tf-nav__link").Select(link => link.TextContent.Trim()).ToList();
        CollectionAssert.Contains(links, "Conversations");
        CollectionAssert.DoesNotContain(links, "Reports", "IReportingService is not registered.");
        CollectionAssert.DoesNotContain(links, "Contacts", "IContactService is not registered.");
    }

    [TestMethod]
    public void DisabledSectionsRenderLockedWhenConfigured()
    {
        using var context = UiTestSupport.Context(services =>
        {
            services.AddStubs(Features);
            services.Configure<TicketFlowBlazorUIOptions>(options => options.ShowDisabledSections = true);
        });
        var session = UiTestSupport.Session(context, [AccessPermissions.ConversationRead]);

        var shell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(session));

        var disabled = shell.FindAll(".tf-nav__link.is-disabled").Select(item => item.TextContent.Trim()).ToList();
        CollectionAssert.Contains(disabled, "Access");
        CollectionAssert.Contains(disabled, "Contacts");
        Assert.IsTrue(shell.FindAll("a.tf-nav__link").All(link => link.TextContent.Trim() != "Access"), "Disabled sections are not links.");
    }

    [TestMethod]
    public void ForbiddenRouteRendersAccessDeniedState()
    {
        using var context = UiTestSupport.Context(services => services.AddStubs(Features));
        var session = UiTestSupport.Session(context, [AccessPermissions.ConversationRead], path: "access");

        var shell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(session).Add(p => p.Route, UiRoutes.Match("access")).Add(p => p.StatusCode, 403));

        StringAssert.Contains(shell.Find(".tf-state__title").TextContent, "Access denied");
    }

    [TestMethod]
    public void RoutesMatchPagesAndIdentifiers()
    {
        Assert.AreEqual(typeof(TicketFlow.UI.Blazor.Pages.DashboardPage), UiRoutes.Match("")!.Component);
        var detail = UiRoutes.Match("conversations/c-1001")!;
        Assert.AreEqual(typeof(TicketFlow.UI.Blazor.Pages.ConversationDetailPage), detail.Component);
        Assert.AreEqual("c-1001", detail.Parameters["Id"]);
        Assert.AreEqual("automation", UiRoutes.Match("automation/rules/tag-refunds")!.Section);
        Assert.AreEqual(typeof(TicketFlow.UI.Blazor.Pages.ServiceAccountsPage), UiRoutes.Match("access/service-accounts")!.Component);
        Assert.AreEqual("access", UiRoutes.Match("access/service-accounts")!.Section);
        Assert.AreEqual(typeof(TicketFlow.UI.Blazor.Pages.RoleFederationPage), UiRoutes.Match("access/federation")!.Component);
        Assert.AreEqual("access", UiRoutes.Match("access/federation")!.Section);
        Assert.IsNull(UiRoutes.Match("conversations/a/b"));
        Assert.IsNull(UiRoutes.Match("unknown"));
        Assert.IsNull(UiRoutes.Match("inboxes/%20"), "Whitespace-only route parameters must be rejected.");
        Assert.IsNull(UiRoutes.Match("teams/%0A"), "Control-character route parameters must be rejected.");
        Assert.IsNull(UiRoutes.Match("automation/rules/bad%00id"), "NUL control-character route parameters must be rejected.");
        Assert.IsNull(UiRoutes.Match("help-center/articles/bad%7Fid"), "DEL control-character route parameters must be rejected.");
    }

    [TestMethod]
    public void AccessSectionAndTabsRespectManageTenantAndBreakGlassPermissions()
    {
        using var context = UiTestSupport.Context(services =>
        {
            services.AddStubs(Features);
            services.AddStubs(typeof(IServiceAccountService), typeof(IRoleFederationService));
        });

        // Break-glass-only user sees Access nav pointing to access/break-glass and only break-glass route is allowed
        var bgSession = UiTestSupport.Session(context, [AccessPermissions.AccessBreakGlass]);
        var bgShell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(bgSession));
        var accessLink = bgShell.FindAll("a.tf-nav__link").Single(link => link.TextContent.Trim() == "Access");
        Assert.AreEqual("/support/access/break-glass", accessLink.GetAttribute("href"));
        Assert.IsTrue(UiNavigation.Allows(bgSession, UiRoutes.Match("access/break-glass")!));
        Assert.IsFalse(UiNavigation.Allows(bgSession, UiRoutes.Match("access")!));
        Assert.IsFalse(UiNavigation.Allows(bgSession, UiRoutes.Match("access/service-accounts")!));

        var bgTabs = context.Render<TicketFlow.UI.Blazor.Pages.AccessTabs>(p => p.AddCascadingValue(bgSession).Add(t => t.Active, "break-glass"));
        var bgTabLabels = bgTabs.FindAll("a").Select(a => a.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Break-glass" }, bgTabLabels);

        // Tenant-admin-only user sees Access nav pointing to service-accounts and only SA + Federation routes/tabs are allowed
        var tenantSession = UiTestSupport.Session(context, [AccessPermissions.AccessManageTenant]);
        var tenantShell = context.Render<TicketFlowShell>(parameters => parameters.AddCascadingValue(tenantSession));
        var tenantLink = tenantShell.FindAll("a.tf-nav__link").Single(link => link.TextContent.Trim() == "Access");
        Assert.AreEqual("/support/access/service-accounts", tenantLink.GetAttribute("href"));
        Assert.IsTrue(UiNavigation.Allows(tenantSession, UiRoutes.Match("access/service-accounts")!));
        Assert.IsTrue(UiNavigation.Allows(tenantSession, UiRoutes.Match("access/federation")!));
        Assert.IsFalse(UiNavigation.Allows(tenantSession, UiRoutes.Match("access")!));
        Assert.IsFalse(UiNavigation.Allows(tenantSession, UiRoutes.Match("access/break-glass")!));

        var tenantTabs = context.Render<TicketFlow.UI.Blazor.Pages.AccessTabs>(p => p.AddCascadingValue(tenantSession).Add(t => t.Active, "service-accounts"));
        var tenantTabLabels = tenantTabs.FindAll("a").Select(a => a.TextContent.Trim()).ToList();
        CollectionAssert.AreEqual(new[] { "Service Accounts", "Role Federation" }, tenantTabLabels);
    }

    [TestMethod]
    public void BreakGlassAndRolePagesFilterAdminRolesRenderApiClientAndCheckOwnerPermissions()
    {
        var scope = UiTestSupport.Scope;
        var partition = AccessPartition.From(scope);
        using var context = UiTestSupport.Context(services =>
        {
            services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores();
        });

        var grantStore = context.Services.GetRequiredService<IAccessGrantStore>();
        var roleStore = context.Services.GetRequiredService<IAccessRoleStore>();
        var now = TimeProvider.System.GetUtcNow();

        // Seed owner grant for UiTestSupport's "agent" actor + custom role with AccessManageTenant + active ApiClientId break-glass grant
        grantStore.CreateAsync(new AccessGrant(new GrantId("g-owner"), partition, new TicketFlow.Core.UserId("agent"), BuiltInRoles.Owner.Id, TenantGrantScope.Instance)).GetAwaiter().GetResult();
        roleStore.CreateAsync(scope, new AccessRole(new RoleId("tenant-mgr"), "Tenant Manager", [AccessPermissions.AccessManageTenant])).GetAwaiter().GetResult();
        roleStore.CreateAsync(scope, new AccessRole(new RoleId("safe-helper"), "Safe Helper", [AccessPermissions.ConversationRead])).GetAwaiter().GetResult();
        grantStore.CreateAsync(new AccessGrant(new GrantId("g-bg-sa"), partition, new TicketFlow.Core.ApiClientId("sa-emergency"), BuiltInRoles.Supervisor.Id, new WorkspaceGrantScope(scope.WorkspaceId),
            validFrom: now.AddMinutes(-5), validUntil: now.AddHours(1), justification: "Emergency automation", kind: GrantKind.BreakGlass)).GetAwaiter().GetResult();

        var session = UiTestSupport.Session(context, [AccessPermissions.AccessManage, AccessPermissions.AccessManageTenant, AccessPermissions.AccessBreakGlass]);

        // BreakGlassPage excludes Owner, Admin, and tenant-mgr; includes safe-helper; renders ApiClientId subject
        var bgPage = context.Render<TicketFlow.UI.Blazor.Pages.BreakGlassPage>(p => p.AddCascadingValue(session));
        var options = bgPage.FindAll("#tf-b-role option").Select(o => o.GetAttribute("value")).ToList();
        CollectionAssert.DoesNotContain(options, BuiltInRoles.Owner.Id.Value, "Owner (IncludesAllPermissions) must be excluded from break-glass.");
        CollectionAssert.DoesNotContain(options, BuiltInRoles.Admin.Id.Value, "Admin must be excluded from break-glass.");
        CollectionAssert.DoesNotContain(options, "tenant-mgr", "Custom role with AccessManageTenant must be excluded from break-glass.");
        CollectionAssert.Contains(options, "safe-helper");
        StringAssert.Contains(bgPage.Markup, "sa-emergency");

        // BreakGlassPage denies users lacking both AccessBreakGlass and AccessManage
        var deniedSession = UiTestSupport.Session(context, [AccessPermissions.AccessManageTenant]);
        var deniedBg = context.Render<TicketFlow.UI.Blazor.Pages.BreakGlassPage>(p => p.AddCascadingValue(deniedSession));
        StringAssert.Contains(deniedBg.Markup, "Access denied");

        // AccessRolePage checks every permission checkbox when viewing built-in Owner
        var rolePage = context.Render<TicketFlow.UI.Blazor.Pages.AccessRolePage>(p => p.AddCascadingValue(session).Add(r => r.Id, BuiltInRoles.Owner.Id.Value));
        var checkboxes = rolePage.FindAll("input[type=checkbox][name=permission]");
        Assert.IsNotEmpty(checkboxes);
        Assert.IsTrue(checkboxes.All(cb => cb.HasAttribute("checked")), "Owner role must show every catalog permission checked.");
    }

    [TestMethod]
    public void MalformedRouteIdsDoNotThrowAndEmbeddedTicketFlowUiSetsNoStore()
    {
        using var context = UiTestSupport.Context(services =>
        {
            services.AddLogging();
            services.AddAntiforgery();
            services.AddStubs(typeof(IInboxService), typeof(ITeamService), typeof(IAutomationRuleService), typeof(TicketFlow.HelpCenter.IHelpCenterService));
        });
        var session = UiTestSupport.Session(context, [AccessPermissions.InboxRead, AccessPermissions.TeamRead, AutomationPermissions.Read, TicketFlow.HelpCenter.HelpCenterPermissions.Read]);

        // Whitespace / control-char IDs fail gracefully with StatePanel instead of throwing ArgumentException (500)
        var inbox = context.Render<TicketFlow.UI.Blazor.Pages.InboxDetailPage>(p => p.AddCascadingValue(session).Add(c => c.Id, "   "));
        StringAssert.Contains(inbox.Markup, "Inbox not available");

        var team = context.Render<TicketFlow.UI.Blazor.Pages.TeamDetailPage>(p => p.AddCascadingValue(session).Add(c => c.Id, "bad\nid"));
        StringAssert.Contains(team.Markup, "Team not available");

        var rule = context.Render<TicketFlow.UI.Blazor.Pages.AutomationRulePage>(p => p.AddCascadingValue(session).Add(c => c.Id, "bad\0rule"));
        StringAssert.Contains(rule.Markup, "Rule not available");

        var article = context.Render<TicketFlow.UI.Blazor.Pages.ArticleEditorPage>(p => p.AddCascadingValue(session).Add(c => c.Id, "   "));
        StringAssert.Contains(article.Markup, "Article not available");

        // Embedded <TicketFlowUI /> sets Cache-Control: no-store
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = context.Services };
        context.Render<TicketFlowUI>(p => p.AddCascadingValue(http).Add(c => c.Path, ""));
        StringAssert.Contains(http.Response.Headers.CacheControl.ToString(), "no-store");
    }
}
