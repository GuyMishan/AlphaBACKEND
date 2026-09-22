using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class BillingV2SchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS billing;

            ALTER TABLE billing.billing_accounts
                ADD COLUMN IF NOT EXISTS "BillingMode" varchar(40) NOT NULL DEFAULT 'OrganizationBilling',
                ADD COLUMN IF NOT EXISTS "Status" varchar(40) NOT NULL DEFAULT 'PendingSetup',
                ADD COLUMN IF NOT EXISTS "DefaultPaymentMethodId" uuid NULL;

            UPDATE billing.billing_accounts
            SET "Status" = CASE "PaymentMethodStatus"
                WHEN 'Active' THEN 'Active'
                WHEN 'Failed' THEN 'PastDue'
                WHEN 'Suspended' THEN 'Suspended'
                WHEN 'Cancelled' THEN 'Cancelled'
                ELSE "Status"
            END
            WHERE "Status" = 'PendingSetup' AND "PaymentMethodStatus" <> 'NotConfigured';

            ALTER TABLE subscriptions.plans
                ADD COLUMN IF NOT EXISTS "Description" varchar(1000) NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS "Currency" varchar(3) NOT NULL DEFAULT 'ILS',
                ADD COLUMN IF NOT EXISTS "BillingInterval" varchar(30) NOT NULL DEFAULT 'Monthly',
                ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 1,
                ADD COLUMN IF NOT EXISTS "EffectiveFrom" timestamptz NOT NULL DEFAULT now(),
                ADD COLUMN IF NOT EXISTS "EffectiveTo" timestamptz NULL,
                ADD COLUMN IF NOT EXISTS "CorrectionBillingMode" varchar(40) NOT NULL DEFAULT 'Free',
                ADD COLUMN IF NOT EXISTS "CorrectionUnitPrice" numeric(18,4) NULL,
                ADD COLUMN IF NOT EXISTS "IncludedCorrections" numeric(18,4) NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "IncludedCorrectionRows" numeric(18,4) NOT NULL DEFAULT 0;

            CREATE TABLE IF NOT EXISTS billing.plan_pricing_components (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PlanId" uuid NOT NULL REFERENCES subscriptions.plans("Id") ON DELETE CASCADE,
                "Version" integer NOT NULL DEFAULT 1,
                "EffectiveFrom" timestamptz NOT NULL DEFAULT now(),
                "EffectiveTo" timestamptz NULL,
                "CorrectionMode" varchar(40) NULL,
                "MetricType" varchar(40) NOT NULL,
                "PricingType" varchar(40) NOT NULL,
                "UnitPrice" numeric(18,4) NOT NULL,
                "IncludedQuantity" numeric(18,4) NOT NULL DEFAULT 0,
                "MinimumCharge" numeric(18,2) NULL,
                "MaximumCharge" numeric(18,2) NULL,
                "IsEnabled" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            ALTER TABLE billing.plan_pricing_components
                ADD COLUMN IF NOT EXISTS "Version" integer NOT NULL DEFAULT 1,
                ADD COLUMN IF NOT EXISTS "EffectiveFrom" timestamptz NOT NULL DEFAULT now(),
                ADD COLUMN IF NOT EXISTS "EffectiveTo" timestamptz NULL,
                ADD COLUMN IF NOT EXISTS "CorrectionMode" varchar(40) NULL;
            DROP INDEX IF EXISTS billing."UX_plan_pricing_components_plan_metric";
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_plan_pricing_components_plan_version_metric"
                ON billing.plan_pricing_components ("PlanId", "Version", "MetricType");

            CREATE TABLE IF NOT EXISTS billing.plan_pricing_tiers (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ComponentId" uuid NOT NULL REFERENCES billing.plan_pricing_components("Id") ON DELETE CASCADE,
                "FromQuantity" numeric(18,4) NOT NULL,
                "ToQuantity" numeric(18,4) NULL,
                "UnitPrice" numeric(18,4) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_plan_pricing_tiers_component_from"
                ON billing.plan_pricing_tiers ("ComponentId", "FromQuantity");

            CREATE TABLE IF NOT EXISTS billing.billing_account_pricing_components (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BillingAccountId" uuid NOT NULL REFERENCES billing.billing_accounts("Id") ON DELETE CASCADE,
                "Version" integer NOT NULL DEFAULT 1,
                "EffectiveFrom" timestamptz NOT NULL DEFAULT now(),
                "EffectiveTo" timestamptz NULL,
                "CorrectionMode" varchar(40) NULL,
                "MetricType" varchar(40) NOT NULL,
                "PricingType" varchar(40) NOT NULL,
                "UnitPrice" numeric(18,4) NOT NULL,
                "IncludedQuantity" numeric(18,4) NOT NULL DEFAULT 0,
                "MinimumCharge" numeric(18,2) NULL,
                "MaximumCharge" numeric(18,2) NULL,
                "IsEnabled" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_account_pricing_components_account_version_metric"
                ON billing.billing_account_pricing_components ("BillingAccountId", "Version", "MetricType");

            CREATE TABLE IF NOT EXISTS billing.billing_account_pricing_tiers (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ComponentId" uuid NOT NULL REFERENCES billing.billing_account_pricing_components("Id") ON DELETE CASCADE,
                "FromQuantity" numeric(18,4) NOT NULL,
                "ToQuantity" numeric(18,4) NULL,
                "UnitPrice" numeric(18,4) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_account_pricing_tiers_component_from"
                ON billing.billing_account_pricing_tiers ("ComponentId", "FromQuantity");

            CREATE TABLE IF NOT EXISTS billing.billing_periods (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BillingAccountId" uuid NOT NULL REFERENCES billing.billing_accounts("Id") ON DELETE RESTRICT,
                "PlanId" uuid NOT NULL REFERENCES subscriptions.plans("Id") ON DELETE RESTRICT,
                "PeriodStart" timestamptz NOT NULL,
                "PeriodEnd" timestamptz NOT NULL,
                "Status" varchar(40) NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "Subtotal" numeric(18,2) NOT NULL DEFAULT 0,
                "Total" numeric(18,2) NOT NULL DEFAULT 0,
                "CalculationSnapshotJson" jsonb NOT NULL DEFAULT '{}'::jsonb,
                "CalculatedAt" timestamptz NULL,
                "ChargedAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_billing_periods_account_range"
                ON billing.billing_periods ("BillingAccountId", "PeriodStart", "PeriodEnd");

            CREATE TABLE IF NOT EXISTS billing.billing_usage (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BillingAccountId" uuid NOT NULL REFERENCES billing.billing_accounts("Id") ON DELETE RESTRICT,
                "BillingPeriodId" uuid NOT NULL REFERENCES billing.billing_periods("Id") ON DELETE CASCADE,
                "EmployerId" uuid NULL REFERENCES employers.employers("Id") ON DELETE RESTRICT,
                "MetricType" varchar(40) NOT NULL,
                "Quantity" numeric(18,4) NOT NULL,
                "IncludedQuantity" numeric(18,4) NOT NULL,
                "BillableQuantity" numeric(18,4) NOT NULL,
                "UnitPrice" numeric(18,4) NOT NULL,
                "Amount" numeric(18,2) NOT NULL,
                "SourceType" varchar(80) NOT NULL DEFAULT '',
                "SourceId" varchar(160) NOT NULL DEFAULT '',
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_billing_usage_period_metric" ON billing.billing_usage ("BillingPeriodId", "MetricType", "EmployerId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_billing_usage_source"
                ON billing.billing_usage ("BillingAccountId", "SourceType", "SourceId", "MetricType") WHERE "SourceId" <> '';

            CREATE TABLE IF NOT EXISTS billing.payment_methods (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BillingAccountId" uuid NOT NULL REFERENCES billing.billing_accounts("Id") ON DELETE CASCADE,
                "Provider" varchar(40) NOT NULL,
                "Type" varchar(40) NOT NULL,
                "Status" varchar(40) NOT NULL,
                "ProviderCustomerId" varchar(200) NOT NULL DEFAULT '',
                "ProviderPaymentMethodId" varchar(200) NOT NULL DEFAULT '',
                "CardBrand" varchar(40) NOT NULL DEFAULT '',
                "CardLast4" varchar(4) NOT NULL DEFAULT '',
                "CardExpiryMonth" integer NULL,
                "CardExpiryYear" integer NULL,
                "MandateReference" varchar(200) NOT NULL DEFAULT '',
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_payment_methods_provider_external"
                ON billing.payment_methods ("Provider", "ProviderPaymentMethodId") WHERE "ProviderPaymentMethodId" <> '';

            CREATE TABLE IF NOT EXISTS billing.payments (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BillingAccountId" uuid NOT NULL REFERENCES billing.billing_accounts("Id") ON DELETE RESTRICT,
                "BillingPeriodId" uuid NOT NULL REFERENCES billing.billing_periods("Id") ON DELETE RESTRICT,
                "Amount" numeric(18,2) NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "Status" varchar(40) NOT NULL,
                "Provider" varchar(40) NOT NULL DEFAULT '',
                "ProviderTransactionId" varchar(200) NOT NULL DEFAULT '',
                "InvoiceReference" varchar(200) NOT NULL DEFAULT '',
                "FailureCode" varchar(120) NOT NULL DEFAULT '',
                "FailureMessage" varchar(1000) NOT NULL DEFAULT '',
                "IdempotencyKey" varchar(160) NOT NULL,
                "PaidAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_payments_idempotency" ON billing.payments ("IdempotencyKey");
            CREATE INDEX IF NOT EXISTS "IX_payments_period" ON billing.payments ("BillingPeriodId");

            CREATE TABLE IF NOT EXISTS billing.payment_attempts (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PaymentId" uuid NOT NULL REFERENCES billing.payments("Id") ON DELETE CASCADE,
                "AttemptNumber" integer NOT NULL,
                "Provider" varchar(40) NOT NULL,
                "Status" varchar(40) NOT NULL,
                "IdempotencyKey" varchar(160) NOT NULL,
                "ProviderTransactionId" varchar(200) NOT NULL DEFAULT '',
                "ErrorCode" varchar(120) NOT NULL DEFAULT '',
                "ErrorMessage" varchar(1000) NOT NULL DEFAULT '',
                "CompletedAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_payment_attempts_idempotency" ON billing.payment_attempts ("IdempotencyKey");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_payment_attempts_payment_number" ON billing.payment_attempts ("PaymentId", "AttemptNumber");

            CREATE TABLE IF NOT EXISTS billing.refunds (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PaymentId" uuid NOT NULL REFERENCES billing.payments("Id") ON DELETE RESTRICT,
                "Amount" numeric(18,2) NOT NULL,
                "Reason" varchar(500) NOT NULL DEFAULT '',
                "Status" varchar(40) NOT NULL,
                "ProviderRefundId" varchar(200) NOT NULL DEFAULT '',
                "ErrorMessage" varchar(1000) NOT NULL DEFAULT '',
                "IdempotencyKey" varchar(160) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_refunds_idempotency" ON billing.refunds ("IdempotencyKey");

            CREATE TABLE IF NOT EXISTS billing.provider_webhook_events (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Provider" varchar(40) NOT NULL,
                "EventKey" varchar(200) NOT NULL,
                "PayloadHash" varchar(128) NOT NULL DEFAULT '',
                "Payload" text NOT NULL DEFAULT '',
                "Status" varchar(40) NOT NULL,
                "ErrorMessage" varchar(1000) NOT NULL DEFAULT '',
                "ProcessedAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_provider_webhook_events_provider_key"
                ON billing.provider_webhook_events ("Provider", "EventKey");
            """, ct);
}
