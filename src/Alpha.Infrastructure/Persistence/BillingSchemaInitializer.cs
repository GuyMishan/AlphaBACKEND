using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class BillingSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS billing;

            CREATE TABLE IF NOT EXISTS billing.billing_accounts (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NULL,
                "EmployerId" uuid NULL,
                "BillingName" varchar(200) NOT NULL DEFAULT '',
                "TaxId" varchar(30) NOT NULL DEFAULT '',
                "InvoiceEmail" varchar(320) NOT NULL DEFAULT '',
                "BillingAddress" varchar(500) NOT NULL DEFAULT '',
                "PaymentMethodType" varchar(40) NOT NULL DEFAULT 'CreditCard',
                "PaymentMethodStatus" varchar(40) NOT NULL DEFAULT 'NotConfigured',
                "ProviderCustomerId" varchar(200) NOT NULL DEFAULT '',
                "ProviderPaymentMethodId" varchar(200) NOT NULL DEFAULT '',
                "CardBrand" varchar(40) NOT NULL DEFAULT '',
                "CardLast4" varchar(4) NOT NULL DEFAULT '',
                "CardExpiryMonth" integer NULL,
                "CardExpiryYear" integer NULL,
                "BankDebitMandateReference" varchar(200) NOT NULL DEFAULT '',
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "CK_billing_accounts_scope"
                    CHECK ((("OrganizationId" IS NOT NULL)::int + ("EmployerId" IS NOT NULL)::int) = 1),
                CONSTRAINT "FK_billing_accounts_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_billing_accounts_EmployerId"
                    FOREIGN KEY ("EmployerId") REFERENCES employers.employers("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_billing_accounts_organization"
                ON billing.billing_accounts ("OrganizationId")
                WHERE "OrganizationId" IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_billing_accounts_employer"
                ON billing.billing_accounts ("EmployerId")
                WHERE "EmployerId" IS NOT NULL;
            """, ct);
}
