using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class BillingInheritanceSchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE employers.employer_profile_settings
            ADD COLUMN IF NOT EXISTS "BillingModeOverridden" boolean NOT NULL DEFAULT false;
            """, ct);

        var organizations = await db.Organizations.AsNoTracking()
            .Select(x => new { x.Id, x.Type })
            .ToListAsync(ct);

        foreach (var organization in organizations)
        {
            var employerIds = await db.Employers.AsNoTracking()
                .Where(x => x.OrganizationId == organization.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            if (employerIds.Count == 0) continue;

            var defaultMode = organization.Type == OrganizationType.SelfService || employerIds.Count <= 1
                ? EmployerBillingMode.EmployerDirect
                : EmployerBillingMode.InheritOrganization;

            var settings = await db.EmployerProfileSettings
                .Where(x => employerIds.Contains(x.EmployerId) && !x.BillingModeOverridden)
                .ToListAsync(ct);
            foreach (var item in settings)
                item.ApplyDefaultBillingMode(defaultMode);
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }
}
