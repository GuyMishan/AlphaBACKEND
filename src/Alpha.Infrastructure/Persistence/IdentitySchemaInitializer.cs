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
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "IsPlatformAdmin" boolean NOT NULL DEFAULT false;
            UPDATE identity.users SET "Appearance" = 'system' WHERE "Appearance" IS NULL OR "Appearance" NOT IN ('system', 'light', 'dark');
            DROP INDEX IF EXISTS identity."IX_users_Email";
            CREATE INDEX IF NOT EXISTS "IX_users_Email" ON identity.users ("Email");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_NationalId" ON identity.users ("NationalId") WHERE "NationalId" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_Phone" ON identity.users ("Phone") WHERE "Phone" IS NOT NULL;

            CREATE TABLE IF NOT EXISTS identity.data_fixes
            (
                "Key" character varying(200) PRIMARY KEY,
                "AppliedAt" timestamp with time zone NOT NULL DEFAULT now()
            );

            WITH applied AS (
                INSERT INTO identity.data_fixes ("Key")
                VALUES ('set-all-existing-user-emails-gaiu01999-20260921')
                ON CONFLICT ("Key") DO NOTHING
                RETURNING 1
            )
            UPDATE identity.users
            SET "Email" = 'gaiu01999@gmail.com'
            WHERE EXISTS (SELECT 1 FROM applied);
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
