using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;

namespace TicketFlow.Workspaces;

public static class WorkspacesTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers scoped <see cref="IWorkspaceSettingsService"/>, <see cref="IInboxService"/>, <see cref="ITeamService"/>,
    /// <see cref="IBusinessCalendarService"/> and <see cref="ITeamMembershipResolver"/>, and the workspace permissions (identical to
    /// the built-in Access definitions; registered once). Uses TryAdd, so application registrations win. Requires
    /// <c>AddAccessControl()</c> plus access stores, and selects no workspace store: call <see cref="AddInMemoryWorkspaceStores"/> for
    /// development or register durable stores.
    /// </summary>
    public static TicketFlowBuilder AddWorkspaces(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(WorkspacePermissionsMarker)))
        {
            services.AddSingleton<WorkspacePermissionsMarker>();
            services.AddAccessPermissions(WorkspacePermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IWorkspaceSettingsService, WorkspaceSettingsService>();
        services.TryAddScoped<IInboxService, InboxService>();
        services.TryAddScoped<ITeamService, TeamService>();
        services.TryAddScoped<IBusinessCalendarService, BusinessCalendarService>();
        services.TryAddScoped<ITeamMembershipResolver, StoreTeamMembershipResolver>();
        return builder;
    }

    /// <summary>
    /// Registers development-only, non-durable in-memory workspace settings, inbox, team and calendar stores (and the settings store as
    /// <see cref="IWorkspaceDirectory"/>) with TryAdd. State is lost on restart and never shared between processes.
    /// </summary>
    public static TicketFlowBuilder AddInMemoryWorkspaceStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<IWorkspaceSettingsStore, InMemoryWorkspaceSettingsStore>();
        services.TryAddSingleton<IInboxStore, InMemoryInboxStore>();
        services.TryAddSingleton<ITeamStore, InMemoryTeamStore>();
        services.TryAddSingleton<IBusinessCalendarStore, InMemoryBusinessCalendarStore>();
        services.TryAddSingleton<IWorkspaceDirectory>(provider => provider.GetRequiredService<IWorkspaceSettingsStore>() as IWorkspaceDirectory
            ?? throw new InvalidOperationException("The registered workspace settings store does not implement IWorkspaceDirectory; register one explicitly."));
        return builder;
    }

    private sealed class WorkspacePermissionsMarker;
}
