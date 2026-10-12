using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class IdentitySchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "Appearance" character varying(16) NOT NULL DEFAULT 'system';
            ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "IsPlatformAdmin" boolean NOT NULL DEFAULT false;
            UPDATE identity.users SET "Appearance" = 'system' WHERE "Appearance" IS NULL OR "Appearance" NOT IN ('system', 'light', 'dark');
            DROP INDEX IF EXISTS identity."IX_users_Email";
            CREATE INDEX IF NOT EXISTS "IX_users_Email" ON identity.users ("Email");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_NationalIdLookupHash" ON identity.users ("NationalIdLookupHash") WHERE "NationalIdLookupHash" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_users_PhoneLookupHash" ON identity.users ("PhoneLookupHash") WHERE "PhoneLookupHash" IS NOT NULL;

            CREATE TABLE IF NOT EXISTS identity.user_sessions
            (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES identity.users("Id") ON DELETE CASCADE,
                "CreatedAt" timestamp with time zone NOT NULL,
                "LastActivityAt" timestamp with time zone NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "RevokedAt" timestamp with time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_user_sessions_UserId" ON identity.user_sessions ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_user_sessions_ExpiresAt" ON identity.user_sessions ("ExpiresAt");

            -- Bookkeeping for keyed one-time data fixes (for example the sensitive-data backfill marker).
            -- The former "set all user emails" fix (applied 2026-09-21) was removed: it rewrote every user's
            -- e-mail on any database without its marker, including restored backups and copies.
            CREATE TABLE IF NOT EXISTS identity.data_fixes
            (
                "Key" character varying(200) PRIMARY KEY,
                "AppliedAt" timestamp with time zone NOT NULL DEFAULT now()
            );
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
