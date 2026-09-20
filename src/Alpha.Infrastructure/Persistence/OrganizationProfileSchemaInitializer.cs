using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class OrganizationProfileSchemaInitializer
{
    public static async Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS organizations.organization_profile_settings (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "RegistrationNumber" character varying(30) NOT NULL DEFAULT '',
                "City" character varying(100) NOT NULL DEFAULT '',
                "Street" character varying(100) NOT NULL DEFAULT '',
                "HouseNumber" character varying(20) NOT NULL DEFAULT '',
                "Apartment" character varying(20) NOT NULL DEFAULT '',
                "PostalCode" character varying(10) NOT NULL DEFAULT '',
                "PostOfficeBox" character varying(20) NOT NULL DEFAULT '',
                "ContactName" character varying(150) NOT NULL DEFAULT '',
                "ContactEmail" character varying(320) NOT NULL DEFAULT '',
                "ContactPhone" character varying(20) NOT NULL DEFAULT '',
                "BillingStatus" character varying(40) NOT NULL DEFAULT 'NotConfigured',
                "InvoiceName" character varying(200) NOT NULL DEFAULT '',
                "InvoiceRegistrationNumber" character varying(30) NOT NULL DEFAULT '',
                "InvoiceEmail" character varying(320) NOT NULL DEFAULT '',
                "BillingContactName" character varying(150) NOT NULL DEFAULT '',
                "BillingContactPhone" character varying(20) NOT NULL DEFAULT '',
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_organization_profile_settings_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations ("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_organization_profile_settings_OrganizationId"
                ON organizations.organization_profile_settings ("OrganizationId");
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);

        var existingIds = await db.OrganizationProfileSettings.AsNoTracking()
            .Select(x => x.OrganizationId).ToListAsync(ct);
        var existing = existingIds.ToHashSet();
        var organizationIds = await db.Organizations.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        foreach (var organizationId in organizationIds.Where(x => !existing.Contains(x)))
            db.OrganizationProfileSettings.Add(new OrganizationProfileSettings(organizationId));

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }
}
