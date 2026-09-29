using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        await using var firstLease = await new OrganizationEntitlementLock(first).AcquireAsync(organizationId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var secondAcquire = new OrganizationEntitlementLock(second).AcquireAsync(organizationId, timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await secondAcquire);

        await firstLease.DisposeAsync();

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
        await using var firstLease = await new OrganizationEntitlementLock(first).AcquireAsync(Guid.NewGuid());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var secondLease = await new OrganizationEntitlementLock(second).AcquireAsync(Guid.NewGuid(), timeout.Token);
    }

    private static AlphaDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AlphaDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AlphaDbContext(options);
    }
}
