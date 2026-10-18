using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

/// <summary>Append-only legal and marketing consent evidence, including OTP-bound registration intent.</summary>
public static class RegistrationConsentSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS identity.registration_consent_intents (
            challenge_id uuid PRIMARY KEY REFERENCES identity.registration_otp_challenges("Id") ON DELETE CASCADE,
            legal_version integer NOT NULL REFERENCES reference_data.legal_document_versions(version),
            marketing_opt_in boolean NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS identity.user_consent_events (
            id uuid PRIMARY KEY,
            user_id uuid NOT NULL REFERENCES identity.users("Id"),
            legal_version integer NULL REFERENCES reference_data.legal_document_versions(version),
            event_type varchar(40) NOT NULL CHECK (event_type IN ('terms_accepted', 'marketing_opt_in', 'marketing_opt_out')),
            accepted boolean NOT NULL,
            occurred_at timestamptz NOT NULL,
            source varchar(32) NOT NULL,
            CHECK (event_type <> 'terms_accepted' OR (legal_version IS NOT NULL AND accepted))
        );
        CREATE INDEX IF NOT EXISTS ix_user_consent_events_user_time
            ON identity.user_consent_events (user_id, occurred_at DESC);
        ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "AcceptedLegalVersion" integer NULL;
        ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "AcceptedLegalAt" timestamptz NULL;
        ALTER TABLE identity.users ADD COLUMN IF NOT EXISTS "MarketingEmailOptIn" boolean NOT NULL DEFAULT false;
        """);
}
