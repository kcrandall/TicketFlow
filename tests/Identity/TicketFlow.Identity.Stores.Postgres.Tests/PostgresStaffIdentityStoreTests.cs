using TicketFlow.Identity.StoreContracts;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Identity.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresStaffIdentityStoreTests
{
    private static Task<(PostgresTestDatabase Storage, PostgresStaffIdentityStore Primary, PostgresStaffIdentityStore Competing)> CreateAsync() =>
        PostgresTestDatabase.CreateAsync("tf_identity_test_", async storage =>
        {
            var primary = new PostgresStaffIdentityStore(storage.First);
            await primary.InitializeAsync();
            return (storage, primary, new PostgresStaffIdentityStore(storage.Second));
        });

    [TestMethod]
    public async Task LinkContractUsesIndependentClients()
    {
        var (storage, primary, competing) = await CreateAsync();
        await using var _ = storage;
        await StaffIdentityStoreContract.VerifyLinksAsync(primary, competing);
        await using var command = storage.First.CreateCommand("SELECT count(*) FROM tf_staff_identity_audit WHERE kind = 1");
        Assert.AreEqual(35L, await command.ExecuteScalarAsync(), "audit rows commit only with applied links");
    }

    [TestMethod]
    public async Task ConcurrentLinksOfOneIdentityPickOneUser()
    {
        var (storage, primary, competing) = await CreateAsync();
        await using var _ = storage;
        await StaffIdentityStoreContract.VerifyConcurrentLinksAsync(primary, competing);
    }

    [TestMethod]
    public async Task ProfileContractUsesIndependentClients()
    {
        var (storage, primary, competing) = await CreateAsync();
        await using var _ = storage;
        await StaffIdentityStoreContract.VerifyProfilesAsync(primary, competing);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        var (storage, primary, competing) = await CreateAsync();
        await using var _ = storage;
        await competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(storage.First, PostgresStaffIdentityMigrations.Catalog).InspectAsync();
        Assert.AreEqual(1, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.identity", inspection.Component);
    }
}
