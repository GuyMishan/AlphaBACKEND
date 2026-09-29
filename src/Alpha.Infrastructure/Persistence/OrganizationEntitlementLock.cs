using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Alpha.Infrastructure.Persistence;

public sealed class OrganizationEntitlementLock(AlphaDbContext db)
{
    public async Task<Lease> AcquireAsync(Guid organizationId, CancellationToken ct = default)
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
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            throw;
        }
    }

    public sealed class Lease(IDbContextTransaction transaction) : IAsyncDisposable
    {
        private bool completed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (completed) throw new InvalidOperationException("The entitlement transaction has already completed.");
            await transaction.CommitAsync(ct);
            completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!completed)
                await transaction.RollbackAsync(CancellationToken.None);
            completed = true;
            await transaction.DisposeAsync();
        }
    }
}
