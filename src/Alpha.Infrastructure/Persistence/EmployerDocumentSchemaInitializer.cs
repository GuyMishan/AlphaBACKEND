using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerDocumentSchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS employers.employer_documents (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL REFERENCES organizations.organizations("Id"),
                "EmployerId" uuid NOT NULL REFERENCES employers.employers("Id"),
                "EmploymentId" uuid NULL,
                "EmployeePensionProductId" uuid NULL,
                "DocumentTypeCode" integer NOT NULL CHECK ("DocumentTypeCode" IN (3,4,5,6,7)),
                "OriginalFileName" varchar(255) NOT NULL,
                "Content" bytea NOT NULL,
                "SizeBytes" bigint NOT NULL,
                "Sha256" varchar(64) NOT NULL,
                "CreatedAt" timestamptz NOT NULL
            );
            ALTER TABLE employers.employer_documents DROP CONSTRAINT IF EXISTS "employer_documents_DocumentTypeCode_check";
            ALTER TABLE employers.employer_documents DROP CONSTRAINT IF EXISTS employer_documents_document_type_check;
            ALTER TABLE employers.employer_documents ADD CONSTRAINT employer_documents_document_type_check CHECK ("DocumentTypeCode" IN (3,4,5,6,7));
            ALTER TABLE employers.employer_documents ADD COLUMN IF NOT EXISTS "EmployeePensionProductId" uuid NULL;
            CREATE INDEX IF NOT EXISTS ix_employer_documents_product ON employers.employer_documents ("EmployeePensionProductId");
            CREATE INDEX IF NOT EXISTS ix_employer_documents_scope
              ON employers.employer_documents ("OrganizationId", "EmployerId", "CreatedAt");
            CREATE INDEX IF NOT EXISTS ix_employer_documents_assignment
              ON employers.employer_documents ("EmployerId", "EmploymentId", "DocumentTypeCode");
            """, ct);
    }
}
