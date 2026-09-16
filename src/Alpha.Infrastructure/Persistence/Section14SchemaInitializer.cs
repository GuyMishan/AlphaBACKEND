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

            CREATE OR REPLACE FUNCTION reporting.seed_employee_mix_into_report()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                mix_product record;
                mix_contribution record;
                report_month date;
                report_product_id uuid;
                insured_salary numeric(18,2);
                allocated_salary numeric(18,2) := 0;
            BEGIN
                IF EXISTS (SELECT 1 FROM reporting.manual_report_products p WHERE p."ReportEmployeeId" = NEW."Id") THEN
                    RETURN NEW;
                END IF;

                SELECT r."ReportingMonth" INTO report_month
                FROM reporting.manual_reports r
                WHERE r."Id" = NEW."ReportId";

                FOR mix_product IN
                    SELECT p.* FROM employees.employee_pension_products p
                    WHERE p."EmploymentId" = NEW."EmploymentId"
                      AND p."IsActive" = true
                      AND p."EffectiveFrom" <= (date_trunc('month', report_month)::date + interval '1 month - 1 day')::date
                      AND (p."EffectiveTo" IS NULL OR p."EffectiveTo" >= date_trunc('month', report_month)::date)
                    ORDER BY p."AllocationOrder", p."CreatedAt"
                LOOP
                    insured_salary := CASE mix_product."SalaryAllocationType"
                        WHEN 'Fixed' THEN COALESCE(mix_product."SalaryAllocationValue", mix_product."Salary")
                        WHEN 'Percentage' THEN round((NEW."MonthlySalary" * COALESCE(mix_product."SalaryAllocationValue", 0) / 100.0)::numeric, 2)
                        WHEN 'Cap' THEN LEAST(NEW."MonthlySalary", COALESCE(mix_product."SalaryAllocationValue", 0))
                        WHEN 'Remainder' THEN GREATEST(NEW."MonthlySalary" - allocated_salary, 0)
                        ELSE mix_product."Salary"
                    END;
                    allocated_salary := allocated_salary + COALESCE(insured_salary, 0);

                    report_product_id := md5(random()::text || clock_timestamp()::text || mix_product."Id"::text)::uuid;
                    INSERT INTO reporting.manual_report_products
                        ("Id", "ReportEmployeeId", "ProductType", "PolicyNumber", "SalaryMonth", "Salary",
                         "SalaryAllocationType", "SalaryAllocationValue", "AllocationOrder", "ReportingType", "SalaryLayer",
                         "Section14", "Section14Code", "Section14StartDate", "CreatedAt", "UpdatedAt")
                    VALUES
                        (report_product_id, NEW."Id", mix_product."ProductType", mix_product."PolicyNumber", report_month,
                         COALESCE(insured_salary, 0), mix_product."SalaryAllocationType", mix_product."SalaryAllocationValue",
                         mix_product."AllocationOrder", mix_product."ReportingType", mix_product."SalaryLayer", mix_product."Section14",
                         mix_product."Section14Code", mix_product."Section14StartDate", now(), now());

                    FOR mix_contribution IN
                        SELECT c.* FROM employees.employee_pension_contributions c
                        WHERE c."EmployeePensionProductId" = mix_product."Id"
                        ORDER BY c."Party", c."Component"
                    LOOP
                        INSERT INTO reporting.manual_contributions
                            ("Id", "ReportProductId", "Party", "Component", "Amount", "Percentage", "ExemptPayments", "CreatedAt", "UpdatedAt")
                        VALUES
                            (md5(random()::text || clock_timestamp()::text || mix_contribution."Id"::text)::uuid,
                             report_product_id, mix_contribution."Party", mix_contribution."Component",
                             round((COALESCE(insured_salary, 0) * mix_contribution."Percentage" / 100.0)::numeric, 2),
                             mix_contribution."Percentage", 0, now(), now());
                    END LOOP;
                END LOOP;

                RETURN NEW;
            END;
            $$;
            """, ct);
}
