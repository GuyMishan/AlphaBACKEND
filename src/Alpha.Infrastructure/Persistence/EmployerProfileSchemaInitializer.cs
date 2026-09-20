using Alpha.Domain.Employers;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerProfileSchemaInitializer
{
    public static async Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS employers.employer_profile_settings (
                "Id" uuid NOT NULL PRIMARY KEY,
                "EmployerId" uuid NOT NULL,
                "City" character varying(100) NOT NULL DEFAULT '',
                "Street" character varying(100) NOT NULL DEFAULT '',
                "HouseNumber" character varying(20) NOT NULL DEFAULT '',
                "Apartment" character varying(20) NOT NULL DEFAULT '',
                "PostalCode" character varying(10) NOT NULL DEFAULT '',
                "PostOfficeBox" character varying(20) NOT NULL DEFAULT '',
                "BillingMode" character varying(40) NOT NULL DEFAULT 'EmployerDirect',
                "BillingStatus" character varying(40) NOT NULL DEFAULT 'NotConfigured',
                "DefaultSalaryPaymentDay" integer NULL,
                "DefaultPaymentMethodCode" integer NULL,
                "DefaultEmployerAccountType" integer NULL,
                "DefaultReceiverAccountType" integer NULL,
                "ReportingNotes" character varying(500) NOT NULL DEFAULT '',
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_employer_profile_settings_EmployerId"
                    FOREIGN KEY ("EmployerId") REFERENCES employers.employers ("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_employer_profile_settings_EmployerId"
                ON employers.employer_profile_settings ("EmployerId");

            CREATE TABLE IF NOT EXISTS employers.employer_payment_accounts (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "EmployerId" uuid NOT NULL,
                "BankId" integer NOT NULL,
                "BranchId" integer NOT NULL,
                "AccountNumber" character varying(30) NOT NULL,
                "AccountHolderName" character varying(150) NOT NULL,
                "AccountHolderId" character varying(20) NOT NULL,
                "IsDefault" boolean NOT NULL DEFAULT false,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_employer_payment_accounts_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_employer_payment_accounts_EmployerId"
                    FOREIGN KEY ("EmployerId") REFERENCES employers.employers ("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_employer_payment_accounts_unique"
                ON employers.employer_payment_accounts ("EmployerId", "BankId", "BranchId", "AccountNumber");
            CREATE INDEX IF NOT EXISTS "IX_employer_payment_accounts_EmployerId"
                ON employers.employer_payment_accounts ("EmployerId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_employer_payment_accounts_one_default"
                ON employers.employer_payment_accounts ("EmployerId")
                WHERE "IsDefault" = true AND "IsActive" = true;

            CREATE TABLE IF NOT EXISTS employers.bank_debit_mandates (
                "Id" uuid NOT NULL PRIMARY KEY,
                "EmployerPaymentAccountId" uuid NOT NULL,
                "Status" character varying(40) NOT NULL DEFAULT 'Pending',
                "ExternalMandateId" character varying(120) NOT NULL DEFAULT '',
                "ApprovedAt" timestamp with time zone NULL,
                "CancelledAt" timestamp with time zone NULL,
                "DocumentId" character varying(200) NOT NULL DEFAULT '',
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_bank_debit_mandates_EmployerPaymentAccountId"
                    FOREIGN KEY ("EmployerPaymentAccountId") REFERENCES employers.employer_payment_accounts ("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_bank_debit_mandates_EmployerPaymentAccountId"
                ON employers.bank_debit_mandates ("EmployerPaymentAccountId");

            DO $
            BEGIN
                IF to_regclass('employers.employer_pension_payment_accounts') IS NOT NULL THEN
                    INSERT INTO employers.employer_payment_accounts (
                        "Id", "OrganizationId", "EmployerId", "BankId", "BranchId", "AccountNumber",
                        "AccountHolderName", "AccountHolderId", "IsDefault", "IsActive", "CreatedAt", "UpdatedAt"
                    )
                    SELECT "Id", "OrganizationId", "EmployerId", "BankCode", "BranchCode", "AccountNumber",
                           "AccountHolderName", '', "IsDefault", true, "CreatedAt", "UpdatedAt"
                    FROM employers.employer_pension_payment_accounts
                    ON CONFLICT ("Id") DO NOTHING;

                    INSERT INTO employers.bank_debit_mandates (
                        "Id", "EmployerPaymentAccountId", "Status", "ExternalMandateId",
                        "ApprovedAt", "CancelledAt", "DocumentId", "CreatedAt", "UpdatedAt"
                    )
                    SELECT gen_random_uuid(), old."Id",
                           CASE old."DebitAuthorizationStatus"
                               WHEN 'Active' THEN 'Active'
                               WHEN 'Revoked' THEN 'Cancelled'
                               ELSE 'Pending'
                           END,
                           '',
                           CASE WHEN old."DebitAuthorizationStatus" = 'Active' THEN old."UpdatedAt" ELSE NULL END,
                           CASE WHEN old."DebitAuthorizationStatus" = 'Revoked' THEN old."UpdatedAt" ELSE NULL END,
                           '',
                           old."CreatedAt",
                           old."UpdatedAt"
                    FROM employers.employer_pension_payment_accounts old
                    WHERE NOT EXISTS (
                        SELECT 1 FROM employers.bank_debit_mandates m
                        WHERE m."EmployerPaymentAccountId" = old."Id"
                    );
                END IF;
            END $;
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);

        var existingEmployerIds = await db.EmployerProfileSettings.AsNoTracking()
            .Select(x => x.EmployerId)
            .ToListAsync(ct);
        var existing = existingEmployerIds.ToHashSet();
        var employerIds = await db.Employers.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        foreach (var employerId in employerIds.Where(x => !existing.Contains(x)))
            db.EmployerProfileSettings.Add(new EmployerProfileSettings(employerId));

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }
}
