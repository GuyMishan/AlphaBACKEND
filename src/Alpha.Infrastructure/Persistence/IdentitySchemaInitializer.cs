using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class IdentitySchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "NationalId" character varying(30) NULL;
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "Phone" character varying(30) NULL;
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "Appearance" character varying(16) NOT NULL DEFAULT 'system';
            UPDATE identity.users SET "Appearance" = 'system' WHERE "Appearance" IS NULL OR "Appearance" NOT IN ('system', 'light', 'dark');
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_NationalId" ON identity.users ("NationalId") WHERE "NationalId" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_Phone" ON identity.users ("Phone") WHERE "Phone" IS NOT NULL;
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
