using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Alpha.Infrastructure.Persistence;

public sealed class OrganizationEntitlementLock(AlphaDbContext db)
{
    public async Task<IAsyncDisposable> AcquireAsync(Guid organizationId, CancellationToken ct = default)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString()}, 0))", ct);
            return new Lease(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(IDbContextTransaction transaction) : IAsyncDisposable
    {
        private bool completed;

        public async ValueTask DisposeAsync()
        {
            if (completed) return;
            completed = true;
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
        }
    }
}
