using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class AccessPermissionsSchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            ALTER TABLE organizations.organization_memberships
                ADD COLUMN IF NOT EXISTS "CanCreateEmployer" boolean NULL,
                ADD COLUMN IF NOT EXISTS "CanEditEmployer" boolean NULL,
                ADD COLUMN IF NOT EXISTS "CanCreateEmployee" boolean NULL,
                ADD COLUMN IF NOT EXISTS "CanEditEmployee" boolean NULL;

            UPDATE organizations.organization_memberships
            SET
                "CanCreateEmployer" = CASE WHEN "Role" = 'Admin' AND "EmployerAccessMode" = 'AllEmployers' THEN true ELSE false END,
                "CanEditEmployer" = CASE WHEN "Role" = 'Viewer' THEN false ELSE true END,
                "CanCreateEmployee" = CASE WHEN "Role" = 'Viewer' THEN false ELSE true END,
                "CanEditEmployee" = CASE WHEN "Role" = 'Viewer' THEN false ELSE true END
            WHERE "CanCreateEmployer" IS NULL
               OR "CanEditEmployer" IS NULL
               OR "CanCreateEmployee" IS NULL
               OR "CanEditEmployee" IS NULL;

            ALTER TABLE organizations.organization_memberships
                ALTER COLUMN "CanCreateEmployer" SET DEFAULT false,
                ALTER COLUMN "CanCreateEmployer" SET NOT NULL,
                ALTER COLUMN "CanEditEmployer" SET DEFAULT false,
                ALTER COLUMN "CanEditEmployer" SET NOT NULL,
                ALTER COLUMN "CanCreateEmployee" SET DEFAULT false,
                ALTER COLUMN "CanCreateEmployee" SET NOT NULL,
                ALTER COLUMN "CanEditEmployee" SET DEFAULT false,
                ALTER COLUMN "CanEditEmployee" SET NOT NULL;
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
