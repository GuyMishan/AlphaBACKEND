using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class InvitationSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS identity.user_invitations (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Email" varchar(320) NOT NULL,
                "OrganizationId" uuid NOT NULL,
                "EmployerId" uuid NULL,
                "OrganizationRole" varchar(40) NULL,
                "EmployerRole" varchar(40) NULL,
                "TokenHash" varchar(64) NOT NULL,
                "Status" varchar(40) NOT NULL DEFAULT 'Pending',
                "ExpiresAt" timestamptz NOT NULL,
                "CreatedBy" uuid NOT NULL,
                "AcceptedAt" timestamptz NULL,
                "AcceptedByUserId" uuid NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_user_invitations_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_user_invitations_EmployerId"
                    FOREIGN KEY ("EmployerId") REFERENCES employers.employers("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_user_invitations_CreatedBy"
                    FOREIGN KEY ("CreatedBy") REFERENCES identity.users("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_user_invitations_AcceptedByUserId"
                    FOREIGN KEY ("AcceptedByUserId") REFERENCES identity.users("Id") ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_user_invitations_token_hash"
                ON identity.user_invitations ("TokenHash");

            CREATE INDEX IF NOT EXISTS "IX_user_invitations_org_status"
                ON identity.user_invitations ("OrganizationId", "Status", "ExpiresAt");

            CREATE INDEX IF NOT EXISTS "IX_user_invitations_email_status"
                ON identity.user_invitations ("Email", "Status", "ExpiresAt");

            ALTER TABLE identity.registration_otp_challenges
                ADD COLUMN IF NOT EXISTS "InvitationTokenHash" varchar(64) NULL;
            """, ct);
}
