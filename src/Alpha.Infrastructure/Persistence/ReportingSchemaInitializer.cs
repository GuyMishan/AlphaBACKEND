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
    "IsActive" boolean NOT NULL DEFAULT true,
    "EffectiveFrom" date NOT NULL DEFAULT CURRENT_DATE,
    "EffectiveTo" date NULL,
    "InstitutionalBody" varchar(160) NOT NULL DEFAULT '',
    "Manufacturer" varchar(160) NOT NULL DEFAULT '',
    "SalaryAllocationType" varchar(30) NOT NULL DEFAULT 'Fixed',
    "SalaryAllocationValue" numeric(18,4) NULL,
    "AllocationOrder" integer NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "Salary" numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "IsActive" boolean NOT NULL DEFAULT true;
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "EffectiveFrom" date NOT NULL DEFAULT CURRENT_DATE;
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "EffectiveTo" date NULL;
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "InstitutionalBody" varchar(160) NOT NULL DEFAULT '';
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "Manufacturer" varchar(160) NOT NULL DEFAULT '';
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "SalaryAllocationType" varchar(30) NOT NULL DEFAULT 'Fixed';
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "SalaryAllocationValue" numeric(18,4) NULL;
ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS "AllocationOrder" integer NOT NULL DEFAULT 0;
UPDATE employees.employee_pension_products
SET "SalaryAllocationValue" = "Salary"
WHERE "SalaryAllocationType" = 'Fixed' AND "SalaryAllocationValue" IS NULL AND "Salary" > 0;
CREATE INDEX IF NOT EXISTS "IX_employee_pension_products_employment"
    ON employees.employee_pension_products ("EmploymentId");
CREATE INDEX IF NOT EXISTS "IX_employee_pension_products_active_period"
    ON employees.employee_pension_products ("EmploymentId", "IsActive", "EffectiveFrom", "EffectiveTo");

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

CREATE TABLE IF NOT EXISTS reporting.employer_interface_file_sequences (
    "SenderIdentifier" varchar(16) NOT NULL,
    "BusinessDate" date NOT NULL,
    "LastSequence" integer NOT NULL,
    PRIMARY KEY ("SenderIdentifier", "BusinessDate"),
    CONSTRAINT "CK_employer_interface_file_sequence" CHECK ("LastSequence" BETWEEN 1 AND 9999)
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
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "ReportKind" varchar(30) NOT NULL DEFAULT 'Current';
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "SourceReportId" uuid NULL;
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "PaymentAccountId" uuid NULL;
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "PaymentBankId" integer NULL;
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "PaymentBranchId" integer NULL;
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "PaymentAccountNumberMasked" varchar(40) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_reports ADD COLUMN IF NOT EXISTS "PaymentMandateReference" varchar(200) NOT NULL DEFAULT '';
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
    "MonthlySalary" numeric(18,2) NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_manual_report_employee" UNIQUE ("ReportId", "EmploymentId")
);
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "MonthlySalary" numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "InterfaceIdentifierType" integer NOT NULL DEFAULT 1;
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "InterfaceIdentifier" varchar(60) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "BirthDateSnapshot" date NULL;
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "GenderSnapshot" integer NULL;
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "EmailSnapshot" varchar(100) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "MobileSnapshot" varchar(30) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "CitySnapshot" varchar(120) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "StreetSnapshot" varchar(120) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "HouseNumberSnapshot" varchar(30) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "ApartmentSnapshot" varchar(30) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "PostalCodeSnapshot" varchar(20) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "PostOfficeBoxSnapshot" varchar(30) NOT NULL DEFAULT '';
ALTER TABLE reporting.manual_report_employees ADD COLUMN IF NOT EXISTS "EmploymentStartDateSnapshot" date NULL;
UPDATE reporting.manual_report_employees re
SET "InterfaceIdentifier" = CASE WHEN re."InterfaceIdentifier" = '' THEN re."NationalId" ELSE re."InterfaceIdentifier" END,
    "BirthDateSnapshot" = COALESCE(re."BirthDateSnapshot", p."BirthDate"),
    "GenderSnapshot" = COALESCE(re."GenderSnapshot", CASE p."Gender" WHEN 'Male' THEN 1 WHEN 'Female' THEN 2 ELSE NULL END),
    "EmailSnapshot" = CASE WHEN re."EmailSnapshot" = '' THEN COALESCE(p."Email", '') ELSE re."EmailSnapshot" END,
    "MobileSnapshot" = CASE WHEN re."MobileSnapshot" = '' THEN COALESCE(p."Mobile", '') ELSE re."MobileSnapshot" END,
    "CitySnapshot" = CASE WHEN re."CitySnapshot" = '' THEN COALESCE(p."City", '') ELSE re."CitySnapshot" END,
    "StreetSnapshot" = CASE WHEN re."StreetSnapshot" = '' THEN COALESCE(p."Street", '') ELSE re."StreetSnapshot" END,
    "HouseNumberSnapshot" = CASE WHEN re."HouseNumberSnapshot" = '' THEN COALESCE(p."HouseNumber", '') ELSE re."HouseNumberSnapshot" END,
    "ApartmentSnapshot" = CASE WHEN re."ApartmentSnapshot" = '' THEN COALESCE(p."Apartment", '') ELSE re."ApartmentSnapshot" END,
    "PostalCodeSnapshot" = CASE WHEN re."PostalCodeSnapshot" = '' THEN COALESCE(p."PostalCode", '') ELSE re."PostalCodeSnapshot" END,
    "PostOfficeBoxSnapshot" = CASE WHEN re."PostOfficeBoxSnapshot" = '' THEN COALESCE(p."PostOfficeBox", '') ELSE re."PostOfficeBoxSnapshot" END,
    "EmploymentStartDateSnapshot" = COALESCE(re."EmploymentStartDateSnapshot", e."StartDate")
FROM employees.people p, employees.employments e
WHERE re."PersonId" = p."Id" AND re."EmploymentId" = e."Id";
CREATE INDEX IF NOT EXISTS "IX_manual_report_employees_scope"
    ON reporting.manual_report_employees ("OrganizationId", "EmployerId", "ReportId");

CREATE TABLE IF NOT EXISTS reporting.manual_report_products (
    "Id" uuid PRIMARY KEY,
    "ReportEmployeeId" uuid NOT NULL REFERENCES reporting.manual_report_employees("Id") ON DELETE CASCADE,
    "ProductType" varchar(40) NOT NULL,
    "PolicyNumber" varchar(100) NOT NULL,
    "SalaryMonth" date NOT NULL,
    "Salary" numeric(18,2) NOT NULL,
    "SalaryAllocationType" varchar(30) NOT NULL DEFAULT 'Fixed',
    "SalaryAllocationValue" numeric(18,4) NULL,
    "AllocationOrder" integer NOT NULL DEFAULT 0,
    "ReportingType" varchar(80) NOT NULL,
    "SalaryLayer" varchar(80) NOT NULL,
    "Section14" boolean NOT NULL,
    "Section14StartDate" date NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS "SalaryAllocationType" varchar(30) NOT NULL DEFAULT 'Fixed';
ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS "SalaryAllocationValue" numeric(18,4) NULL;
ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS "AllocationOrder" integer NOT NULL DEFAULT 0;
UPDATE reporting.manual_report_products
SET "SalaryAllocationValue" = "Salary"
WHERE "SalaryAllocationType" = 'Fixed' AND "SalaryAllocationValue" IS NULL AND "Salary" > 0;
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
    "PreviousRecordIdentifier" varchar(36) NOT NULL DEFAULT '',
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_manual_contribution" UNIQUE ("ReportProductId", "Party", "Component")
);

ALTER TABLE reporting.manual_contributions ADD COLUMN IF NOT EXISTS "PreviousRecordIdentifier" varchar(36) NOT NULL DEFAULT '';

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
    "TrustAccountValueDate" date NULL,
    "ActualDepositAmount" numeric(15,2) NULL,
    "MasavSenderCode" varchar(16) NOT NULL DEFAULT '',
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

CREATE TABLE IF NOT EXISTS reporting.manual_report_attachments (
    "Id" uuid PRIMARY KEY,
    "ReportId" uuid NOT NULL REFERENCES reporting.manual_reports("Id") ON DELETE CASCADE,
    "ReportProductId" uuid NULL REFERENCES reporting.manual_report_products("Id") ON DELETE CASCADE,
    "DocumentTypeCode" integer NOT NULL,
    "OriginalFileName" varchar(260) NOT NULL,
    "TransmissionFileName" varchar(100) NOT NULL,
    "ContentType" varchar(120) NOT NULL,
    "Content" bytea NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "Sha256" varchar(64) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "CK_manual_report_attachments_document_type" CHECK ("DocumentTypeCode" IN (3,4,5,6))
);
CREATE INDEX IF NOT EXISTS "IX_manual_report_attachments_report"
    ON reporting.manual_report_attachments ("ReportId");
CREATE INDEX IF NOT EXISTS "IX_manual_report_attachments_product"
    ON reporting.manual_report_attachments ("ReportProductId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_manual_report_attachments_transmission_name"
    ON reporting.manual_report_attachments ("ReportId", "TransmissionFileName");

ALTER TABLE reporting.manual_report_attachments
    DROP CONSTRAINT IF EXISTS "CK_manual_report_attachments_document_type";
ALTER TABLE reporting.manual_report_attachments
    ADD CONSTRAINT "CK_manual_report_attachments_document_type"
    CHECK ("DocumentTypeCode" IN (3,4,5,6));

ALTER TABLE reporting.manual_report_payments
    ADD COLUMN IF NOT EXISTS "TrustAccountValueDate" date NULL;
ALTER TABLE reporting.manual_report_payments
    ADD COLUMN IF NOT EXISTS "ActualDepositAmount" numeric(15,2) NULL;
ALTER TABLE reporting.manual_report_payments
    ADD COLUMN IF NOT EXISTS "MasavSenderCode" varchar(16) NOT NULL DEFAULT '';

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
