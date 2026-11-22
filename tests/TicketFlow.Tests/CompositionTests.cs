using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Testing;

namespace TicketFlow.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void DefaultCompositionRegistersOnlyTheSystemClock()
    {
        var services = new ServiceCollection();
        var builder = services.AddTicketFlow();
        Assert.AreSame(services, builder.Services);
        using var provider = services.BuildServiceProvider();
        Assert.AreSame(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.HasCount(1, services);
    }

    [TestMethod]
    public void ApplicationClockRegisteredBeforeOrAfterIsPreserved()
    {
        var before = new ServiceCollection();
        var clock = TestClock.Create();
        before.AddSingleton<TimeProvider>(clock);
        before.AddTicketFlow();
        before.AddTicketFlow();
        using (var provider = before.BuildServiceProvider())
            Assert.AreSame(clock, provider.GetRequiredService<TimeProvider>());
        Assert.AreEqual(1, before.Count(descriptor => descriptor.ServiceType == typeof(TimeProvider)));

        // Registering after AddTicketFlow follows normal last-registration-wins semantics.
        var after = new ServiceCollection();
        after.AddTicketFlow();
        after.AddSingleton<TimeProvider>(clock);
        using var later = after.BuildServiceProvider();
        Assert.AreSame(clock, later.GetRequiredService<TimeProvider>());
    }

    [TestMethod]
    public void RepeatedCompositionIsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow();
        services.AddTicketFlow();
        Assert.HasCount(1, services);
    }

    [TestMethod]
    public void NullServicesAreRejected() =>
        Assert.AreEqual("services", Assert.ThrowsExactly<ArgumentNullException>(() => TicketFlowServiceCollectionExtensions.AddTicketFlow(null!)).ParamName);
}
