using TicketFlow.UI.Blazor.Pages;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>A matched UI page: the component, its section (for navigation and gating) and route parameters.</summary>
public sealed record UiRoute(string Pattern, Type Component, string Section, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// The page table, relative to <see cref="TicketFlowBlazorUIOptions.BasePath"/>. A table instead of <c>@page</c> directives keeps the
/// base path configurable and lets hosts render the same pages inside their own Blazor router (<c>TicketFlowUI</c>).
/// </summary>
public static class UiRoutes
{
    private static readonly (string Pattern, Type Component, string Section)[] Table =
    [
        ("", typeof(DashboardPage), "dashboard"),
        ("conversations", typeof(ConversationsPage), "conversations"),
        ("conversations/{id}", typeof(ConversationDetailPage), "conversations"),
        ("contacts", typeof(ContactsPage), "contacts"),
        ("contacts/{id}", typeof(ContactDetailPage), "contacts"),
        ("reports", typeof(ReportsPage), "reports"),
        ("inboxes", typeof(InboxesPage), "inboxes"),
        ("inboxes/{id}", typeof(InboxDetailPage), "inboxes"),
        ("teams", typeof(TeamsPage), "teams"),
        ("teams/{id}", typeof(TeamDetailPage), "teams"),
        ("calendars", typeof(CalendarsPage), "calendars"),
        ("channels", typeof(ChannelsPage), "channels"),
        ("routing", typeof(RoutingPage), "routing"),
        ("sla", typeof(SlaPage), "sla"),
        ("automation", typeof(AutomationPage), "automation"),
        ("automation/rules/{id}", typeof(AutomationRulePage), "automation"),
        ("automation/macros", typeof(MacrosPage), "automation"),
        ("automation/canned", typeof(CannedResponsesPage), "automation"),
        ("help-center", typeof(HelpCenterPage), "help-center"),
        ("help-center/articles/{id}", typeof(ArticleEditorPage), "help-center"),
        ("surveys", typeof(SurveysPage), "surveys"),
        ("access", typeof(AccessPage), "access"),
        ("access/roles/{id}", typeof(AccessRolePage), "access"),
        ("access/grants", typeof(AccessGrantsPage), "access"),
        ("access/field-policies", typeof(FieldPoliciesPage), "access"),
        ("access/break-glass", typeof(BreakGlassPage), "access"),
        ("settings", typeof(SettingsPage), "settings"),
        ("settings/theme", typeof(ThemeSettingsPage), "settings"),
    ];

    public static UiRoute? Match(string? path)
    {
        var segments = (path ?? string.Empty).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (pattern, component, section) in Table)
        {
            var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != segments.Length)
                continue;
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            var matched = true;
            for (var index = 0; index < parts.Length && matched; index++)
            {
                if (parts[index].StartsWith('{'))
                {
                    var value = Uri.UnescapeDataString(segments[index]);
                    if (value.Length is 0 or > 256)
                        matched = false;
                    else
                        parameters["Id"] = value;
                }
                else if (!string.Equals(parts[index], segments[index], StringComparison.OrdinalIgnoreCase))
                    matched = false;
            }
            if (matched)
                return new UiRoute(pattern, component, section, parameters);
        }
        return null;
    }
}
