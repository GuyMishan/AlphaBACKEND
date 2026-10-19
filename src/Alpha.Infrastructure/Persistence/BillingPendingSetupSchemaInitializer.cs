using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

/// <summary>Staged billing details are committed only after a verified provider callback.</summary>
public static class BillingPendingSetupSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS billing.pending_payment_setup_details (
            billing_account_id uuid PRIMARY KEY REFERENCES billing.billing_accounts("Id") ON DELETE CASCADE,
            setup_reference varchar(200) NOT NULL,
            details_json jsonb NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        );
        """);
}
