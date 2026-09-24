using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class PensionFundSnapshotSchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    private const string Sql = """
ALTER TABLE employees.employee_pension_products
    ADD COLUMN IF NOT EXISTS fund_external_key varchar(500) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_code varchar(100) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_name varchar(300) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_company_name varchar(300) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_classification varchar(200) NOT NULL DEFAULT '';

ALTER TABLE reporting.manual_report_products
    ADD COLUMN IF NOT EXISTS fund_external_key varchar(500) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_code varchar(100) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_name varchar(300) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_company_name varchar(300) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS fund_classification varchar(200) NOT NULL DEFAULT '';

-- Report products are seeded explicitly by ManualReportEndpoints. The older database trigger
-- also seeded the same employee mix and could create duplicate report products.
DROP TRIGGER IF EXISTS "TR_manual_report_employee_seed_mix" ON reporting.manual_report_employees;

-- Keep insured salary calculation correct at the moment the report snapshot is created.
-- This intentionally reacts only to report-product insertion; later changes to the employee
-- or to the employee mix do not mutate historical report snapshots.
CREATE OR REPLACE FUNCTION reporting.recalculate_report_product_salaries(p_report_employee_id uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    report_product record;
    employee_monthly_salary numeric(18,2);
    insured_salary numeric(18,2);
    allocated_salary numeric(18,2) := 0;
BEGIN
    SELECT COALESCE("MonthlySalary", 0)
    INTO employee_monthly_salary
    FROM reporting.manual_report_employees
    WHERE "Id" = p_report_employee_id;

    FOR report_product IN
        SELECT p.*
        FROM reporting.manual_report_products p
        WHERE p."ReportEmployeeId" = p_report_employee_id
        ORDER BY p."AllocationOrder", p."CreatedAt", p."Id"
    LOOP
        insured_salary := CASE report_product."SalaryAllocationType"
            WHEN 'Fixed' THEN COALESCE(report_product."SalaryAllocationValue", report_product."Salary", 0)
            WHEN 'Percentage' THEN round((employee_monthly_salary * COALESCE(report_product."SalaryAllocationValue", 0) / 100.0)::numeric, 2)
            WHEN 'Cap' THEN LEAST(employee_monthly_salary, COALESCE(report_product."SalaryAllocationValue", 0))
            WHEN 'Remainder' THEN GREATEST(employee_monthly_salary - allocated_salary, 0)
            ELSE COALESCE(report_product."Salary", 0)
        END;

        IF employee_monthly_salary > 0 AND allocated_salary + COALESCE(insured_salary, 0) > employee_monthly_salary + 0.01 THEN
            RAISE EXCEPTION 'Salary allocations exceed employee monthly salary.';
        END IF;

        allocated_salary := allocated_salary + COALESCE(insured_salary, 0);

        UPDATE reporting.manual_report_products
        SET "Salary" = COALESCE(insured_salary, 0)
        WHERE "Id" = report_product."Id";

        UPDATE reporting.manual_contributions
        SET "Amount" = round((COALESCE(insured_salary, 0) * "Percentage" / 100.0)::numeric, 2)
        WHERE "ReportProductId" = report_product."Id";
    END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION reporting.recalculate_report_product_salaries_trigger()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM reporting.recalculate_report_product_salaries(NEW."ReportEmployeeId");
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS "TR_manual_report_product_recalculate_salary" ON reporting.manual_report_products;
CREATE TRIGGER "TR_manual_report_product_recalculate_salary"
AFTER INSERT ON reporting.manual_report_products
FOR EACH ROW EXECUTE FUNCTION reporting.recalculate_report_product_salaries_trigger();

-- Contributions are derived from the insured salary snapshot and the saved percentage.
CREATE OR REPLACE FUNCTION reporting.sync_manual_contribution_amount()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    insured_salary numeric(18,2);
BEGIN
    SELECT COALESCE("Salary", 0)
    INTO insured_salary
    FROM reporting.manual_report_products
    WHERE "Id" = NEW."ReportProductId";

    NEW."Amount" := round((COALESCE(insured_salary, 0) * COALESCE(NEW."Percentage", 0) / 100.0)::numeric, 2);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS "TR_manual_contribution_sync_amount" ON reporting.manual_contributions;
CREATE TRIGGER "TR_manual_contribution_sync_amount"
BEFORE INSERT OR UPDATE OF "Percentage", "ReportProductId" ON reporting.manual_contributions
FOR EACH ROW EXECUTE FUNCTION reporting.sync_manual_contribution_amount();
""";
}
