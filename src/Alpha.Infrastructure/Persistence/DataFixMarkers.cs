using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

/// <summary>
/// Completion markers for one-time data maintenance jobs, stored in <c>identity.data_fixes</c>. A job records its
/// key only after a pass that left nothing unresolved, so later starts can skip it.
/// </summary>
public static class DataFixMarkers
{
    public static Task<bool> IsAppliedAsync(AlphaDbContext db, string key, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM identity.data_fixes WHERE \"Key\" = {key}) AS \"Value\"")
            .SingleAsync(ct);

    public static Task MarkAppliedAsync(AlphaDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"INSERT INTO identity.data_fixes (\"Key\") VALUES ({key}) ON CONFLICT (\"Key\") DO NOTHING", ct);
}
