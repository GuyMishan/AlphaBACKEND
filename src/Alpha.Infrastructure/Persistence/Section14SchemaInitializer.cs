using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class Section14SchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE employees.employee_pension_products
                ADD COLUMN IF NOT EXISTS "Section14Code" integer NOT NULL DEFAULT 3;
            UPDATE employees.employee_pension_products
               SET "Section14Code" = CASE
                    WHEN "Section14" = false THEN 3
                    WHEN "Section14StartDate" IS NULL THEN 1
                    ELSE 2
               END
             WHERE "Section14Code" = 3 AND "Section14" = true;

            ALTER TABLE reporting.manual_report_products
                ADD COLUMN IF NOT EXISTS "Section14Code" integer NOT NULL DEFAULT 3;
            UPDATE reporting.manual_report_products
               SET "Section14Code" = CASE
                    WHEN "Section14" = false THEN 3
                    WHEN "Section14StartDate" IS NULL THEN 1
                    ELSE 2
               END
             WHERE "Section14Code" = 3 AND "Section14" = true;

            CREATE INDEX IF NOT EXISTS "IX_employee_pension_products_section14_code"
                ON employees.employee_pension_products ("Section14Code");
            CREATE INDEX IF NOT EXISTS "IX_manual_report_products_section14_code"
                ON reporting.manual_report_products ("Section14Code");
            """, ct);
}
