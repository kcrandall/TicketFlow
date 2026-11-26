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
    }
}
