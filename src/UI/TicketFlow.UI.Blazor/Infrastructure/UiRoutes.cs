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
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>(StringComparer.Ordinal);

    private sealed record Entry(string Pattern, string[] Parts, Type Component, string Section, bool HasParam);

    private static readonly Entry[] Table =
    [
        .. new (string Pattern, Type Component, string Section)[]
        {
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
            ("access/service-accounts", typeof(ServiceAccountsPage), "access"),
            ("access/federation", typeof(RoleFederationPage), "access"),
            ("settings", typeof(SettingsPage), "settings"),
            ("settings/theme", typeof(ThemeSettingsPage), "settings"),
        }.Select(item =>
        {
            var parts = item.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return new Entry(item.Pattern, parts, item.Component, item.Section, parts.Any(part => part.StartsWith('{')));
        }),
    ];

    public static UiRoute? Match(string? path)
    {
        var segments = string.IsNullOrEmpty(path) ? [] : path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var e = 0; e < Table.Length; e++)
        {
            var entry = Table[e];
            var parts = entry.Parts;
            if (parts.Length != segments.Length)
                continue;
            Dictionary<string, object?>? parameters = null;
            var matched = true;
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index][0] == '{')
                {
                    var value = Uri.UnescapeDataString(segments[index]);
                    if (value.Length is 0 or > 256)
                    {
                        matched = false;
                        break;
                    }
                    (parameters ??= new Dictionary<string, object?>(1, StringComparer.Ordinal))["Id"] = value;
                }
                else if (!string.Equals(parts[index], segments[index], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
                return new UiRoute(entry.Pattern, entry.Component, entry.Section, parameters ?? EmptyParameters);
        }
        return null;
    }
}
