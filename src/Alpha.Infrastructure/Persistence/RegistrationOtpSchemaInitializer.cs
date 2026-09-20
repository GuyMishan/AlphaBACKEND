using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class RegistrationOtpSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS identity.registration_otp_challenges (
            "Id" uuid PRIMARY KEY,
            "DisplayName" varchar(120) NOT NULL,
            "Email" varchar(320) NOT NULL,
            "NationalId" varchar(9) NOT NULL,
            "Phone" varchar(10) NOT NULL,
            "CodeHash" varchar(64) NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL,
            "ExpiresAt" timestamp with time zone NOT NULL,
            "ConsumedAt" timestamp with time zone NULL,
            "Attempts" integer NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS "IX_registration_otp_email_created"
            ON identity.registration_otp_challenges ("Email", "CreatedAt" DESC);
        CREATE INDEX IF NOT EXISTS "IX_registration_otp_nationalid_created"
            ON identity.registration_otp_challenges ("NationalId", "CreatedAt" DESC);
        """);
}
