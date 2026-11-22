using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;

namespace TicketFlow.Identity;

public static class StaffIdentityTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers staff identity mapping: <see cref="StaffIdentityPermissions"/>, scoped <see cref="IStaffIdentityService"/> and
    /// <see cref="IStaffIdentityResolver"/>. Requires the access engine (<c>AddAccessControl()</c>), an
    /// <see cref="Workspaces.ITeamMembershipResolver"/> (for example <c>AddWorkspaces()</c>) and a store. Uses TryAdd so application
    /// registrations win.
    /// </summary>
    public static TicketFlowBuilder AddStaffIdentity(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(StaffIdentityPermissionsMarker)))
        {
            services.AddSingleton<StaffIdentityPermissionsMarker>();
            services.AddAccessPermissions(StaffIdentityPermissions.Definitions);
        }
        services.TryAddScoped<IStaffIdentityService, StaffIdentityService>();
        services.TryAddScoped<IStaffIdentityResolver, StoreStaffIdentityResolver>();
        return builder;
    }

    /// <summary>Registers <see cref="InMemoryStaffIdentityStore"/> (development only: process-local, bounded, not durable).</summary>
    public static TicketFlowBuilder AddInMemoryStaffIdentityStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<InMemoryStaffIdentityStore>();
        builder.Services.TryAddSingleton<IStaffIdentityStore>(provider => provider.GetRequiredService<InMemoryStaffIdentityStore>());
        return builder;
    }

    private sealed class StaffIdentityPermissionsMarker;
}
