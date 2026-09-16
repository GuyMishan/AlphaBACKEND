using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerInterfaceFeedbackSchemaInitializer
{
    public static async Task EnsureCreatedAsync(AlphaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS reporting.employer_interface_feedback (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "EmployerId" uuid NOT NULL,
                "DocumentType" varchar(40) NOT NULL,
                "InterfaceVersion" varchar(10) NOT NULL,
                "SourceFileName" varchar(260) NOT NULL,
                "InterfaceFileNumber" varchar(100) NOT NULL DEFAULT '',
                "PayloadHash" varchar(128) NOT NULL,
                "RawXml" text NOT NULL,
                "ReceivedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_employer_interface_feedback_organizations_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_employer_interface_feedback_employers_EmployerId"
                    FOREIGN KEY ("EmployerId") REFERENCES employers.employers("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_employer_interface_feedback_EmployerId_PayloadHash"
                ON reporting.employer_interface_feedback ("EmployerId", "PayloadHash");
            CREATE INDEX IF NOT EXISTS "IX_employer_interface_feedback_OrganizationId_EmployerId_ReceivedAt"
                ON reporting.employer_interface_feedback ("OrganizationId", "EmployerId", "ReceivedAt");
            """);
    }
}
