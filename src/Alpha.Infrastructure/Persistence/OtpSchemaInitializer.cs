using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class OtpSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS identity.otp_challenges (
            "Id" uuid PRIMARY KEY,
            "UserId" uuid NOT NULL,
            "Channel" varchar(10) NOT NULL,
            "CodeHash" varchar(64) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "ExpiresAt" timestamp with time zone NOT NULL,
            "ConsumedAt" timestamp with time zone NULL,
            "Attempts" integer NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS "IX_otp_challenges_UserId_CreatedAt" ON identity.otp_challenges ("UserId", "CreatedAt" DESC);
        """);
}
