using TicketFlow.Testing;

namespace TicketFlow.Tests;

[TestClass]
public sealed class TestingHelperTests
{
    [TestMethod]
    public void TestClockIsFixedUntilAdvanced()
    {
        var clock = TestClock.Create();
        Assert.AreEqual(TestClock.DefaultStart, clock.GetUtcNow());
        Assert.AreEqual(TestClock.DefaultStart, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(TestClock.DefaultStart.AddMinutes(5), clock.GetUtcNow());
    }

    [TestMethod]
    public async Task PostgresDatabaseIsInconclusiveWithoutConfiguration()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TICKETFLOW_TEST_POSTGRES")))
        {
            await using var database = await PostgresTestDatabase.CreateAsync();
            await using var command = database.First.CreateCommand("SELECT current_schema()");
            Assert.AreEqual(database.Schema, await command.ExecuteScalarAsync());
            return;
        }

        if (PostgresTestServer.IsRequired)
        {
            await Assert.ThrowsExactlyAsync<AssertFailedException>(() => PostgresTestDatabase.CreateAsync());
            return;
        }
        await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(() => PostgresTestDatabase.CreateAsync());

        // TICKETFLOW_REQUIRE_POSTGRES=1 turns the skip into a failure so CI cannot pass without a database.
        Environment.SetEnvironmentVariable("TICKETFLOW_REQUIRE_POSTGRES", "1");
        try
        {
            await Assert.ThrowsExactlyAsync<AssertFailedException>(() => PostgresTestDatabase.CreateAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TICKETFLOW_REQUIRE_POSTGRES", null);
        }
    }
}
