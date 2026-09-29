using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class OrganizationEntitlementLockConcurrencyTests
{
    [Fact]
    public async Task Same_organization_is_serialized_until_first_transaction_commits()
    {
        var cs = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var first = CreateDb(cs);
        await using var second = CreateDb(cs);
        var organizationId = Guid.NewGuid();

        await using var firstLease = await new OrganizationEntitlementLock(first).AcquireAsync(organizationId, TestContext.Current.CancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var secondAcquire = new OrganizationEntitlementLock(second).AcquireAsync(organizationId, timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await secondAcquire);

        await firstLease.CommitAsync(TestContext.Current.CancellationToken);

        await using var third = CreateDb(cs);
        using var successTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var nextLease = await new OrganizationEntitlementLock(third).AcquireAsync(organizationId, successTimeout.Token);
    }

    [Fact]
    public async Task Different_organizations_do_not_block_each_other()
    {
        var cs = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;

        await using var first = CreateDb(cs);
        await using var second = CreateDb(cs);
        await using var firstLease = await new OrganizationEntitlementLock(first).AcquireAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var secondLease = await new OrganizationEntitlementLock(second).AcquireAsync(Guid.NewGuid(), timeout.Token);
    }

    [Fact]
    public async Task Concurrent_limit_one_check_and_write_allows_exactly_one_winner()
    {
        var cs = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var ct = TestContext.Current.CancellationToken;
        var organizationId = Guid.NewGuid();

        await using (var setup = CreateDb(cs))
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS public.entitlement_race_probe (
                    id uuid PRIMARY KEY,
                    organization_id uuid NOT NULL
                )
                """, ct);
        }

        var attempts = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = CreateDb(cs);
            await using var lease = await new OrganizationEntitlementLock(db).AcquireAsync(organizationId, ct);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "SELECT COUNT(*) FROM public.entitlement_race_probe WHERE organization_id = @organizationId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "organizationId";
            parameter.Value = organizationId;
            command.Parameters.Add(parameter);
            var current = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            if (current >= 1) return false;

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO public.entitlement_race_probe (id, organization_id) VALUES ({Guid.NewGuid()}, {organizationId})", ct);
            await lease.CommitAsync(ct);
            return true;
        });

        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(x => x));

        await using var verify = CreateDb(cs);
        await using var verifyCommand = verify.Database.GetDbConnection().CreateCommand();
        await verify.Database.OpenConnectionAsync(ct);
        verifyCommand.CommandText = "SELECT COUNT(*) FROM public.entitlement_race_probe WHERE organization_id = @organizationId";
        var verifyParameter = verifyCommand.CreateParameter();
        verifyParameter.ParameterName = "organizationId";
        verifyParameter.Value = organizationId;
        verifyCommand.Parameters.Add(verifyParameter);
        Assert.Equal(1, Convert.ToInt32(await verifyCommand.ExecuteScalarAsync(ct)));
    }

    [Fact]
    public async Task Disposing_without_commit_rolls_back_guarded_write()
    {
        var cs = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var ct = TestContext.Current.CancellationToken;
        var organizationId = Guid.NewGuid();

        await using (var setup = CreateDb(cs))
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS public.entitlement_race_probe (
                    id uuid PRIMARY KEY,
                    organization_id uuid NOT NULL
                )
                """, ct);

        await using (var db = CreateDb(cs))
        {
            await using var lease = await new OrganizationEntitlementLock(db).AcquireAsync(organizationId, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO public.entitlement_race_probe (id, organization_id) VALUES ({Guid.NewGuid()}, {organizationId})", ct);
        }

        await using var verify = CreateDb(cs);
        await verify.Database.OpenConnectionAsync(ct);
        await using var command = verify.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM public.entitlement_race_probe WHERE organization_id = @organizationId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organizationId";
        parameter.Value = organizationId;
        command.Parameters.Add(parameter);
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(ct)));
    }

    [Fact]
    public async Task Existing_transaction_can_join_same_organization_lock()
    {
        var cs = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var ct = TestContext.Current.CancellationToken;
        var organizationId = Guid.NewGuid();

        await using var first = CreateDb(cs);
        await using var transaction = await first.Database.BeginTransactionAsync(ct);
        await new OrganizationEntitlementLock(first).AcquireInCurrentTransactionAsync(organizationId, ct);

        await using var second = CreateDb(cs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new OrganizationEntitlementLock(second).AcquireAsync(organizationId, timeout.Token));

        await transaction.CommitAsync(ct);
    }

    private static AlphaDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AlphaDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AlphaDbContext(options);
    }
}
