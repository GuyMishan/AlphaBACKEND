using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReferentSchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "IsReferent" boolean NOT NULL DEFAULT false;
            CREATE TABLE IF NOT EXISTS identity.referent_organization_assignments
            (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES identity.users("Id") ON DELETE CASCADE,
                "OrganizationId" uuid NOT NULL REFERENCES organizations.organizations("Id") ON DELETE CASCADE,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_referent_organization" UNIQUE ("UserId", "OrganizationId")
            );
            CREATE INDEX IF NOT EXISTS "IX_referent_organization_by_org" ON identity.referent_organization_assignments ("OrganizationId");
            CREATE TABLE IF NOT EXISTS identity.referent_employer_assignments
            (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES identity.users("Id") ON DELETE CASCADE,
                "EmployerId" uuid NOT NULL REFERENCES employers.employers("Id") ON DELETE CASCADE,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "UQ_referent_employer" UNIQUE ("UserId", "EmployerId")
            );
            CREATE INDEX IF NOT EXISTS "IX_referent_employer_by_employer" ON identity.referent_employer_assignments ("EmployerId");
            """, ct);
}
