using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployeeIdentitySchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    internal const string Sql = """
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "IdentifierType" integer NOT NULL DEFAULT 1;
UPDATE employees.people SET "IdentifierType" = 1 WHERE "IdentifierType" IS NULL;
DROP INDEX IF EXISTS employees."IX_people_OrganizationId_NationalIdLookupHash";
DROP INDEX IF EXISTS employees."IX_people_OrganizationId_IdentifierType_NationalIdLookupHash";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_people_OrganizationId_IdentifierType_NationalIdLookupHash"
    ON employees.people ("OrganizationId", "IdentifierType", "NationalIdLookupHash")
    WHERE "NationalIdLookupHash" IS NOT NULL;
""";
}
