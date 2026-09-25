using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerInterface006SchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);

    private const string Sql = """
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "BirthDate" date NULL;
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "Gender" varchar(20) NULL;
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "Email" varchar(50) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "Mobile" varchar(15) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "City" varchar(100) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "Street" varchar(100) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "HouseNumber" varchar(20) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "Apartment" varchar(20) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "PostalCode" varchar(10) NOT NULL DEFAULT '';
ALTER TABLE employees.people ADD COLUMN IF NOT EXISTS "PostOfficeBox" varchar(20) NOT NULL DEFAULT '';

ALTER TABLE employers.employers ADD COLUMN IF NOT EXISTS "ContactFirstName" varchar(20) NOT NULL DEFAULT '';
ALTER TABLE employers.employers ADD COLUMN IF NOT EXISTS "ContactLastName" varchar(20) NOT NULL DEFAULT '';
ALTER TABLE employers.employers ADD COLUMN IF NOT EXISTS "ContactPhone" varchar(20) NOT NULL DEFAULT '';
ALTER TABLE employers.employers ADD COLUMN IF NOT EXISTS "ContactEmail" varchar(50) NOT NULL DEFAULT '';
ALTER TABLE employers.employers ADD COLUMN IF NOT EXISTS "ContactMobile" varchar(15) NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS reporting.employer_interface_report_product_data (
    "Id" uuid PRIMARY KEY,
    "ReportProductId" uuid NOT NULL REFERENCES reporting.manual_report_products("Id") ON DELETE CASCADE,
    "OperationCode" integer NULL,
    "DepositStatus" integer NULL,
    "EmployeeStatus" integer NULL,
    "StatusStartDate" date NULL,
    "EmploymentPercentage" numeric(5,2) NULL,
    "WorkDaysInMonth" integer NULL,
    "LastDeposit" integer NULL,
    "RefundReason" integer NULL,
    "PaymentMethodCode" integer NULL,
    "EmployerAccountType" integer NULL,
    "ReceiverAccountType" integer NULL,
    "OldPensionTypeCode" integer NULL,
    "InterfaceTransferIdentifier" varchar(36) NULL,
    "PreviousIdentifier" varchar(36) NULL,
    "PreviousClearingIdentifier" varchar(36) NULL,
    "PreviousReferenceExceptionCode" integer NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "UX_employer_interface_report_product_data_product" UNIQUE ("ReportProductId")
);
ALTER TABLE reporting.employer_interface_report_product_data ADD COLUMN IF NOT EXISTS "OperationCode" integer NULL;
ALTER TABLE reporting.employer_interface_report_product_data ADD COLUMN IF NOT EXISTS "OldPensionTypeCode" integer NULL;
ALTER TABLE reporting.employer_interface_report_product_data ADD COLUMN IF NOT EXISTS "InterfaceTransferIdentifier" varchar(36) NULL;
ALTER TABLE reporting.employer_interface_report_product_data ADD COLUMN IF NOT EXISTS "PreviousIdentifier" varchar(36) NULL;
ALTER TABLE reporting.employer_interface_report_product_data ADD COLUMN IF NOT EXISTS "PreviousClearingIdentifier" varchar(36) NULL;
ALTER TABLE reporting.employer_interface_report_product_data ADD COLUMN IF NOT EXISTS "PreviousReferenceExceptionCode" integer NULL;
""";
}
