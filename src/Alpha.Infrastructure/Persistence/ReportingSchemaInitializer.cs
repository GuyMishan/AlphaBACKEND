using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReportingSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    private const string Sql = """
CREATE SCHEMA IF NOT EXISTS reporting;

CREATE TABLE IF NOT EXISTS reporting.manual_reports (
    "Id" uuid PRIMARY KEY,
    "OrganizationId" uuid NOT NULL REFERENCES organizations.organizations("Id") ON DELETE RESTRICT,
    "EmployerId" uuid NOT NULL REFERENCES employers.employers("Id") ON DELETE RESTRICT,
    "ReportingMonth" date NOT NULL,
    "SalaryPaymentDate" date NULL,
    "Status" varchar(40) NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_manual_reports_scope_month"
    ON reporting.manual_reports ("OrganizationId", "EmployerId", "ReportingMonth");

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
""";
}
