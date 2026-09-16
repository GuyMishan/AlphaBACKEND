using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReportTransmissionSchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS reporting.report_transmissions (
                "Id" uuid PRIMARY KEY,
                "ReportId" uuid NOT NULL,
                "OrganizationId" uuid NOT NULL,
                "EmployerId" uuid NOT NULL,
                "Provider" varchar(120) NOT NULL,
                "AttemptNumber" integer NOT NULL,
                "Status" varchar(30) NOT NULL,
                "ExternalId" varchar(200) NOT NULL DEFAULT '',
                "PayloadHash" varchar(128) NOT NULL DEFAULT '',
                "ResponsePayload" text NOT NULL DEFAULT '',
                "ErrorMessage" varchar(4000) NOT NULL DEFAULT '',
                "StartedAt" timestamptz NULL,
                "SentAt" timestamptz NULL,
                "CompletedAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_report_transmissions_manual_reports_ReportId"
                    FOREIGN KEY ("ReportId") REFERENCES reporting.manual_reports("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_report_transmissions_ReportId_AttemptNumber"
                ON reporting.report_transmissions ("ReportId", "AttemptNumber");
            CREATE INDEX IF NOT EXISTS "IX_report_transmissions_OrganizationId_EmployerId_ReportId"
                ON reporting.report_transmissions ("OrganizationId", "EmployerId", "ReportId");
            """);
    }
}
