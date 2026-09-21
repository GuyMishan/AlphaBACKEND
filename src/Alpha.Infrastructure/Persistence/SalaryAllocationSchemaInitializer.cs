using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class SalaryAllocationSchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    private const string Sql = """
ALTER TABLE employees.employments
    ADD COLUMN IF NOT EXISTS "MonthlySalary" numeric(18,2) NOT NULL DEFAULT 0;

ALTER TABLE employees.employee_pension_contributions
    ADD COLUMN IF NOT EXISTS "Amount" numeric(18,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "ExemptPayments" numeric(18,2) NOT NULL DEFAULT 0;

CREATE OR REPLACE FUNCTION reporting.seed_employee_mix_into_report()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    mix_product record;
    mix_contribution record;
    report_month date;
    report_product_id uuid;
    employee_monthly_salary numeric(18,2);
    insured_salary numeric(18,2);
    allocated_salary numeric(18,2) := 0;
    remainder_count integer := 0;
BEGIN
    IF EXISTS (SELECT 1 FROM reporting.manual_report_products p WHERE p."ReportEmployeeId" = NEW."Id") THEN
        RETURN NEW;
    END IF;

    SELECT r."ReportingMonth" INTO report_month
    FROM reporting.manual_reports r
    WHERE r."Id" = NEW."ReportId";

    SELECT COALESCE(NULLIF(NEW."MonthlySalary", 0), e."MonthlySalary", 0)
    INTO employee_monthly_salary
    FROM employees.employments e
    WHERE e."Id" = NEW."EmploymentId";

    employee_monthly_salary := COALESCE(employee_monthly_salary, NEW."MonthlySalary", 0);

    IF NEW."MonthlySalary" IS DISTINCT FROM employee_monthly_salary THEN
        UPDATE reporting.manual_report_employees
        SET "MonthlySalary" = employee_monthly_salary,
            "UpdatedAt" = now()
        WHERE "Id" = NEW."Id";
    END IF;

    SELECT count(*) INTO remainder_count
    FROM employees.employee_pension_products p
    WHERE p."EmploymentId" = NEW."EmploymentId"
      AND p."IsActive" = true
      AND p."SalaryAllocationType" = 'Remainder'
      AND p."EffectiveFrom" <= (date_trunc('month', report_month)::date + interval '1 month - 1 day')::date
      AND (p."EffectiveTo" IS NULL OR p."EffectiveTo" >= date_trunc('month', report_month)::date);

    IF remainder_count > 1 THEN
        RAISE EXCEPTION 'Only one product may use remainder salary allocation for a reporting month.';
    END IF;

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
            WHEN 'Percentage' THEN round((employee_monthly_salary * COALESCE(mix_product."SalaryAllocationValue", 0) / 100.0)::numeric, 2)
            WHEN 'Cap' THEN LEAST(employee_monthly_salary, COALESCE(mix_product."SalaryAllocationValue", 0))
            WHEN 'Remainder' THEN GREATEST(employee_monthly_salary - allocated_salary, 0)
            ELSE mix_product."Salary"
        END;

        IF employee_monthly_salary > 0 AND allocated_salary + COALESCE(insured_salary, 0) > employee_monthly_salary + 0.01 THEN
            RAISE EXCEPTION 'Salary allocations exceed employee monthly salary.';
        END IF;

        allocated_salary := allocated_salary + COALESCE(insured_salary, 0);
        report_product_id := md5(random()::text || clock_timestamp()::text || mix_product."Id"::text)::uuid;

        INSERT INTO reporting.manual_report_products
            ("Id", "ReportEmployeeId", "ProductType", "PolicyNumber", "SalaryMonth", "Salary",
             "SalaryAllocationType", "SalaryAllocationValue", "AllocationOrder", "ReportingType", "SalaryLayer",
             "Section14", "Section14StartDate", "CreatedAt", "UpdatedAt")
        VALUES
            (report_product_id, NEW."Id", mix_product."ProductType", mix_product."PolicyNumber", report_month,
             COALESCE(insured_salary, 0), mix_product."SalaryAllocationType", mix_product."SalaryAllocationValue",
             mix_product."AllocationOrder", mix_product."ReportingType", mix_product."SalaryLayer", mix_product."Section14",
             mix_product."Section14StartDate", now(), now());

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
                 mix_contribution."Percentage", COALESCE(mix_contribution."ExemptPayments", 0), now(), now());
        END LOOP;
    END LOOP;

    RETURN NEW;
END;
$$;
""";
}
