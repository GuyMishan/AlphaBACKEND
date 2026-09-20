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

            CREATE TABLE IF NOT EXISTS employers.employer_pension_payment_accounts (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "EmployerId" uuid NOT NULL,
                "AccountName" character varying(100) NOT NULL,
                "BankCode" integer NOT NULL,
                "BranchCode" integer NOT NULL,
                "AccountNumber" character varying(30) NOT NULL,
                "AccountHolderName" character varying(150) NOT NULL,
                "IsDefault" boolean NOT NULL DEFAULT false,
                "DebitAuthorizationStatus" character varying(40) NOT NULL DEFAULT 'NotConfigured',
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_employer_pension_payment_accounts_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_employer_pension_payment_accounts_EmployerId"
                    FOREIGN KEY ("EmployerId") REFERENCES employers.employers ("Id") ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_employer_pension_payment_accounts_EmployerId_AccountNumber"
                ON employers.employer_pension_payment_accounts ("EmployerId", "AccountNumber");
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
