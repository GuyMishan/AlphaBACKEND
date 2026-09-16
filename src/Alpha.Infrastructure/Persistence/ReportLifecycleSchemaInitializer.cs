using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReportLifecycleSchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    private const string Sql = """
ALTER TABLE reporting.manual_reports
    ADD COLUMN IF NOT EXISTS "SnapshotTakenAt" timestamptz NULL;
ALTER TABLE reporting.manual_reports
    ADD COLUMN IF NOT EXISTS "ValidatedAt" timestamptz NULL;
ALTER TABLE reporting.manual_reports
    ADD COLUMN IF NOT EXISTS "ValidationError" varchar(2000) NOT NULL DEFAULT '';

ALTER TABLE reporting.manual_report_employees
    ADD COLUMN IF NOT EXISTS "ValidationStatus" varchar(40) NOT NULL DEFAULT 'Draft';
ALTER TABLE reporting.manual_report_employees
    ADD COLUMN IF NOT EXISTS "ValidationError" varchar(2000) NOT NULL DEFAULT '';

ALTER TABLE reporting.manual_report_products
    ADD COLUMN IF NOT EXISTS "ValidationStatus" varchar(40) NOT NULL DEFAULT 'Draft';
ALTER TABLE reporting.manual_report_products
    ADD COLUMN IF NOT EXISTS "ValidationError" varchar(2000) NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS "IX_manual_reports_status"
    ON reporting.manual_reports ("OrganizationId", "EmployerId", "Status");
CREATE INDEX IF NOT EXISTS "IX_manual_report_employees_validation"
    ON reporting.manual_report_employees ("ReportId", "ValidationStatus");
CREATE INDEX IF NOT EXISTS "IX_manual_report_products_validation"
    ON reporting.manual_report_products ("ReportEmployeeId", "ValidationStatus");
""";
}
