using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReferenceDataSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS reference_data;

            CREATE TABLE IF NOT EXISTS reference_data.sync_runs (
                id uuid PRIMARY KEY,
                integration_key varchar(80) NOT NULL,
                integration_name varchar(160) NOT NULL,
                status varchar(30) NOT NULL,
                started_at timestamptz NOT NULL,
                finished_at timestamptz NULL,
                records_received integer NOT NULL DEFAULT 0,
                records_inserted integer NOT NULL DEFAULT 0,
                records_updated integer NOT NULL DEFAULT 0,
                records_deactivated integer NOT NULL DEFAULT 0,
                error_message text NULL,
                details_json jsonb NULL,
                triggered_by_user_id uuid NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sync_runs_integration_started
                ON reference_data.sync_runs (integration_key, started_at DESC);

            CREATE TABLE IF NOT EXISTS reference_data.banks (
                bank_code integer PRIMARY KEY,
                bank_name text NOT NULL,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(80) NOT NULL,
                last_seen_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_data.bank_branches (
                bank_code integer NOT NULL,
                branch_code integer NOT NULL,
                branch_name text NOT NULL,
                branch_address text NULL,
                city text NULL,
                zip_code text NULL,
                telephone text NULL,
                branch_type text NULL,
                open_date text NULL,
                close_date text NULL,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(80) NOT NULL,
                last_seen_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (bank_code, branch_code)
            );
            CREATE INDEX IF NOT EXISTS ix_bank_branches_city ON reference_data.bank_branches (city);

            CREATE TABLE IF NOT EXISTS reference_data.pension_products (
                external_key text PRIMARY KEY,
                source varchar(80) NOT NULL,
                domain text NOT NULL,
                product_type text NULL,
                fund_code text NULL,
                fund_name text NOT NULL,
                short_name text NULL,
                company_legal_id text NULL,
                company_name text NULL,
                investment_track_code text NULL,
                investment_track_name text NULL,
                classification text NULL,
                bank_code integer NULL,
                bank_name text NULL,
                branch_code integer NULL,
                account_number text NULL,
                source_updated_at text NULL,
                is_active boolean NOT NULL DEFAULT true,
                last_seen_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_pension_products_domain ON reference_data.pension_products (domain);
            CREATE INDEX IF NOT EXISTS ix_pension_products_fund_code ON reference_data.pension_products (fund_code);
            CREATE INDEX IF NOT EXISTS ix_pension_products_company ON reference_data.pension_products (company_name);

            ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS fund_external_key text NOT NULL DEFAULT '';
            ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS fund_code text NOT NULL DEFAULT '';
            ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS fund_name text NOT NULL DEFAULT '';
            ALTER TABLE employees.employee_pension_products ADD COLUMN IF NOT EXISTS fund_company_name text NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS ix_employee_pension_products_fund_external_key
                ON employees.employee_pension_products (fund_external_key);

            ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS fund_external_key text NOT NULL DEFAULT '';
            ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS fund_code text NOT NULL DEFAULT '';
            ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS fund_name text NOT NULL DEFAULT '';
            ALTER TABLE reporting.manual_report_products ADD COLUMN IF NOT EXISTS fund_company_name text NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS ix_manual_report_products_fund_external_key
                ON reporting.manual_report_products (fund_external_key);
            """, ct);
}
