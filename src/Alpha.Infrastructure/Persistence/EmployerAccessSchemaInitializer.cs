using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerAccessSchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            ALTER TABLE employers.employer_user_access
                ADD COLUMN IF NOT EXISTS "Role" character varying(40) NULL;

            UPDATE employers.employer_user_access AS access
            SET "Role" = CASE
                WHEN membership."Role" = 'Admin' THEN 'Admin'
                WHEN membership."Role" = 'Viewer' THEN 'Viewer'
                ELSE 'User'
            END
            FROM organizations.organization_memberships AS membership
            WHERE access."Role" IS NULL
              AND membership."UserId" = access."UserId"
              AND membership."OrganizationId" = access."OrganizationId";

            UPDATE employers.employer_user_access
            SET "Role" = 'User'
            WHERE "Role" IS NULL;

            ALTER TABLE employers.employer_user_access
                ALTER COLUMN "Role" SET DEFAULT 'User',
                ALTER COLUMN "Role" SET NOT NULL;

            CREATE INDEX IF NOT EXISTS "IX_employer_user_access_UserId_OrganizationId"
                ON employers.employer_user_access ("UserId", "OrganizationId");
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
