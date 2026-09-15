using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReportingSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    private const string Sql = """
CREATE SCHEMA IF NOT EXISTS reporting;

CREATE TABLE IF NOT EXISTS employees.employee_pension_products (
    "Id" uuid PRIMARY KEY,
    "EmploymentId" uuid NOT NULL REFERENCES employees.employments("Id") ON DELETE CASCADE,
    "ProductType" varchar(40) NOT NULL,
    "PolicyNumber" varchar(100) NOT NULL,
    "Salary" numeric(18,2) NOT NULL DEFAULT 0,
    "ReportingType" varchar(80) NOT NULL DEFAULT 'שוטף',
    "SalaryLayer" varchar(80) NOT NULL DEFAULT 'רובד 1',
    "Section14" boolean NOT NULL,
    "Section14StartDate" date NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
ALTER TABLE employees.employee_pension_products
    ADD COLUMN IF NOT EXISTS "Salary" numeric(18,2) NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS "IX_employee_pension_products_employment"
    ON employees.employee_pension_products ("EmploymentId");

CREATE TABLE IF NOT EXISTS employees.employee_pension_contributions (
    "Id" uuid PRIMARY KEY,
    "EmployeePensionProductId" uuid NOT NULL REFERENCES employees.employee_pension_products("Id") ON DELETE CASCADE,
    "Party" varchar(20) NOT NULL,
    "Component" varchar(30) NOT NULL,
    "Percentage" numeric(9,4) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_employee_pension_contribution" UNIQUE ("EmployeePensionProductId", "Party", "Component")
);

CREATE TABLE IF NOT EXISTS reporting.manual_reports (
    "Id" uuid PRIMARY KEY,
    "OrganizationId" uuid NOT NULL REFERENCES organizations.organizations("Id") ON DELETE RESTRICT,
    "EmployerId" uuid NOT NULL REFERENCES employers.employers("Id") ON DELETE RESTRICT,
    "ReportingMonth" date NOT NULL,
    "SalaryPaymentDate" date NULL,
    "Status" varchar(40) NOT NULL,
    "ReportKind" varchar(30) NOT NULL DEFAULT 'Current',
    "SourceReportId" uuid NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
ALTER TABLE reporting.manual_reports
    ADD COLUMN IF NOT EXISTS "ReportKind" varchar(30) NOT NULL DEFAULT 'Current';
ALTER TABLE reporting.manual_reports
    ADD COLUMN IF NOT EXISTS "SourceReportId" uuid NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_manual_reports_source') THEN
        ALTER TABLE reporting.manual_reports
            ADD CONSTRAINT "FK_manual_reports_source"
            FOREIGN KEY ("SourceReportId") REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT;
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS "IX_manual_reports_scope_month"
    ON reporting.manual_reports ("OrganizationId", "EmployerId", "ReportingMonth");
CREATE INDEX IF NOT EXISTS "IX_manual_reports_source"
    ON reporting.manual_reports ("SourceReportId");

CREATE TABLE IF NOT EXISTS reporting.manual_report_employees (
    "Id" uuid PRIMARY KEY,
    "ReportId" uuid NOT NULL REFERENCES reporting.manual_reports("Id") ON DELETE CASCADE,
    "OrganizationId" uuid NOT NULL,
    "EmployerId" uuid NOT NULL,
    "EmploymentId" uuid NOT NULL REFERENCES employees.employments("Id") ON DELETE RESTRICT,
    "PersonId" uuid NOT NULL,
    "NationalId" varchar(30) NOT NULL,
    "FirstName" varchar(100) NOT NULL,
    "LastName" varchar(100) NOT NULL,
    "EmployeeNumber" varchar(50) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_manual_report_employee" UNIQUE ("ReportId", "EmploymentId")
);
CREATE INDEX IF NOT EXISTS "IX_manual_report_employees_scope"
    ON reporting.manual_report_employees ("OrganizationId", "EmployerId", "ReportId");

CREATE TABLE IF NOT EXISTS reporting.manual_report_products (
    "Id" uuid PRIMARY KEY,
    "ReportEmployeeId" uuid NOT NULL REFERENCES reporting.manual_report_employees("Id") ON DELETE CASCADE,
    "ProductType" varchar(40) NOT NULL,
    "PolicyNumber" varchar(100) NOT NULL,
    "SalaryMonth" date NOT NULL,
    "Salary" numeric(18,2) NOT NULL,
    "ReportingType" varchar(80) NOT NULL,
    "SalaryLayer" varchar(80) NOT NULL,
    "Section14" boolean NOT NULL,
    "Section14StartDate" date NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_manual_report_products_employee"
    ON reporting.manual_report_products ("ReportEmployeeId");

CREATE TABLE IF NOT EXISTS reporting.manual_contributions (
    "Id" uuid PRIMARY KEY,
    "ReportProductId" uuid NOT NULL REFERENCES reporting.manual_report_products("Id") ON DELETE CASCADE,
    "Party" varchar(20) NOT NULL,
    "Component" varchar(30) NOT NULL,
    "Amount" numeric(18,2) NOT NULL,
    "Percentage" numeric(9,4) NOT NULL,
    "ExemptPayments" numeric(18,2) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_manual_contribution" UNIQUE ("ReportProductId", "Party", "Component")
);

CREATE OR REPLACE FUNCTION reporting.seed_employee_mix_into_report()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    mix_product record;
    mix_contribution record;
    report_month date;
    report_product_id uuid;
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
        ORDER BY p."CreatedAt"
    LOOP
        report_product_id := md5(random()::text || clock_timestamp()::text || mix_product."Id"::text)::uuid;
        INSERT INTO reporting.manual_report_products
            ("Id", "ReportEmployeeId", "ProductType", "PolicyNumber", "SalaryMonth", "Salary", "ReportingType", "SalaryLayer", "Section14", "Section14StartDate", "CreatedAt", "UpdatedAt")
        VALUES
            (report_product_id, NEW."Id", mix_product."ProductType", mix_product."PolicyNumber", report_month,
             mix_product."Salary", mix_product."ReportingType", mix_product."SalaryLayer", mix_product."Section14",
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
                 round((mix_product."Salary" * mix_contribution."Percentage" / 100.0)::numeric, 2),
                 mix_contribution."Percentage", 0, now(), now());
        END LOOP;
    END LOOP;

    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS "TR_manual_report_employee_seed_mix" ON reporting.manual_report_employees;
CREATE TRIGGER "TR_manual_report_employee_seed_mix"
AFTER INSERT ON reporting.manual_report_employees
FOR EACH ROW EXECUTE FUNCTION reporting.seed_employee_mix_into_report();

CREATE TABLE IF NOT EXISTS reporting.manual_report_payments (
    "Id" uuid PRIMARY KEY,
    "ReportProductId" uuid NOT NULL REFERENCES reporting.manual_report_products("Id") ON DELETE CASCADE,
    "ProviderName" varchar(160) NOT NULL DEFAULT '',
    "ProviderAccount" varchar(120) NOT NULL DEFAULT '',
    "PaymentMethod" varchar(80) NOT NULL DEFAULT 'העברה בנקאית',
    "ValueDate" date NULL,
    "ReferenceNumber" varchar(120) NOT NULL DEFAULT '',
    "EmployerBankName" varchar(120) NOT NULL DEFAULT '',
    "EmployerBankCode" varchar(30) NOT NULL DEFAULT '',
    "EmployerBranch" varchar(30) NOT NULL DEFAULT '',
    "EmployerAccount" varchar(80) NOT NULL DEFAULT '',
    "ConfirmationFileName" varchar(260) NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_manual_report_payment_product" UNIQUE ("ReportProductId")
);

CREATE TABLE IF NOT EXISTS reporting.contribution_percentage_limits (
    "Id" uuid PRIMARY KEY,
    "Year" integer NOT NULL,
    "ProductType" varchar(40) NOT NULL,
    "Party" varchar(20) NOT NULL,
    "Component" varchar(30) NOT NULL,
    "MaxPercentage" numeric(9,4) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_contribution_percentage_limit" UNIQUE ("Year", "ProductType", "Party", "Component")
);
CREATE INDEX IF NOT EXISTS "IX_contribution_percentage_limits_year"
    ON reporting.contribution_percentage_limits ("Year");

INSERT INTO reporting.contribution_percentage_limits
    ("Id", "Year", "ProductType", "Party", "Component", "MaxPercentage", "CreatedAt", "UpdatedAt")
SELECT
    md5('contribution-limit-2026-' || product_type || '-' || party || '-' || component)::uuid,
    2026,
    product_type,
    party,
    component,
    CASE
        WHEN party = 'Employer' AND component = 'Severance' THEN 8.33
        WHEN party = 'Employer' AND component = 'Benefits' THEN 7.50
        WHEN party = 'Employer' AND component = 'Disability' THEN 2.50
        WHEN party = 'Employee' AND component = 'Benefits' AND product_type = 'StudyFund' THEN 2.50
        WHEN party = 'Employee' AND component = 'Benefits' THEN 7.00
        ELSE 100.00
    END,
    now(),
    now()
FROM (VALUES ('PensionFund'), ('StudyFund'), ('ManagersInsurance'), ('ProvidentFund'), ('Other')) AS products(product_type)
CROSS JOIN (VALUES ('Employer'), ('Employee')) AS parties(party)
CROSS JOIN (VALUES ('Severance'), ('Benefits'), ('Disability'), ('Other')) AS components(component)
ON CONFLICT ("Year", "ProductType", "Party", "Component") DO UPDATE
SET "MaxPercentage" = EXCLUDED."MaxPercentage",
    "UpdatedAt" = now();
""";
}
