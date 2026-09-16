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

            CREATE TABLE IF NOT EXISTS reference_data.cities (
                city_code integer PRIMARY KEY,
                city_name text NOT NULL,
                region_code integer NULL,
                region_name text NULL,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(80) NOT NULL,
                last_seen_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cities_name ON reference_data.cities (city_name);

            CREATE TABLE IF NOT EXISTS reference_data.streets (
                city_code integer NOT NULL,
                street_code integer NOT NULL,
                street_name text NOT NULL,
                official_code integer NOT NULL,
                street_name_status text NOT NULL,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(80) NOT NULL,
                last_seen_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (city_code, street_code)
            );
            CREATE INDEX IF NOT EXISTS ix_streets_city_name ON reference_data.streets (city_code, street_name);
            CREATE INDEX IF NOT EXISTS ix_streets_official ON reference_data.streets (city_code, official_code);

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
            ALTER TABLE reference_data.pension_products ADD COLUMN IF NOT EXISTS investment_track_code text NULL;
            ALTER TABLE reference_data.pension_products ADD COLUMN IF NOT EXISTS investment_track_name text NULL;
            CREATE INDEX IF NOT EXISTS ix_pension_products_domain ON reference_data.pension_products (domain);
            CREATE INDEX IF NOT EXISTS ix_pension_products_fund_code ON reference_data.pension_products (fund_code);
            CREATE INDEX IF NOT EXISTS ix_pension_products_company ON reference_data.pension_products (company_name);

            CREATE TABLE IF NOT EXISTS reference_data.employer_interface_006_options (
                category varchar(60) NOT NULL,
                scope varchar(20) NOT NULL DEFAULT 'all',
                code integer NOT NULL,
                name varchar(160) NOT NULL,
                sort_order integer NOT NULL DEFAULT 0,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(80) NOT NULL DEFAULT 'EmployerInterface006-XSD',
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (category, scope, code)
            );
            CREATE INDEX IF NOT EXISTS ix_employer_interface_006_options_lookup
                ON reference_data.employer_interface_006_options (category, scope, is_active, sort_order, code);

            INSERT INTO reference_data.employer_interface_006_options (category, scope, code, name, sort_order)
            VALUES
                ('gender', 'all', 1, 'זכר', 1),
                ('gender', 'all', 2, 'נקבה', 2),
                ('receipt-type', 'all', 1, 'קוד 1', 1),
                ('receipt-type', 'all', 2, 'קוד 2', 2),
                ('receipt-type', 'all', 4, 'קוד 4', 4),
                ('receipt-type', 'all', 6, 'קוד 6', 6),
                ('receipt-type', 'all', 8, 'קוד 8', 8),
                ('operation-code', 'current', 1, 'קוד 1', 1),
                ('operation-code', 'current', 2, 'קוד 2', 2),
                ('operation-code', 'current', 3, 'קוד 3', 3),
                ('operation-code', 'current', 7, 'קוד 7', 7),
                ('operation-code', 'negative', 5, 'קוד 5', 5),
                ('operation-code', 'negative', 6, 'קוד 6', 6),
                ('deposit-status', 'all', 1, 'קוד 1', 1),
                ('deposit-status', 'all', 2, 'קוד 2', 2),
                ('deposit-status', 'all', 3, 'קוד 3', 3),
                ('employee-status', 'all', 1, 'קוד 1', 1),
                ('employee-status', 'all', 2, 'קוד 2', 2),
                ('employee-status', 'all', 3, 'קוד 3', 3),
                ('employee-status', 'all', 4, 'קוד 4', 4),
                ('employee-status', 'all', 5, 'קוד 5', 5),
                ('employee-status', 'all', 8, 'קוד 8', 8),
                ('employee-status', 'all', 9, 'קוד 9', 9),
                ('employee-status', 'all', 10, 'קוד 10', 10),
                ('employee-status', 'all', 11, 'קוד 11', 11),
                ('employee-status', 'all', 12, 'קוד 12', 12),
                ('employee-status', 'all', 14, 'קוד 14', 14),
                ('employee-status', 'all', 17, 'קוד 17', 17),
                ('employee-status', 'all', 18, 'קוד 18', 18),
                ('last-deposit', 'all', 1, 'קוד 1', 1),
                ('last-deposit', 'all', 2, 'קוד 2', 2),
                ('refund-reason', 'negative', 1, 'קוד 1', 1),
                ('refund-reason', 'negative', 2, 'קוד 2', 2),
                ('refund-reason', 'negative', 3, 'קוד 3', 3),
                ('refund-reason', 'negative', 4, 'קוד 4', 4),
                ('refund-reason', 'negative', 5, 'קוד 5', 5),
                ('refund-reason', 'negative', 6, 'קוד 6', 6),
                ('refund-reason', 'negative', 7, 'קוד 7', 7),
                ('refund-reason', 'negative', 8, 'קוד 8', 8),
                ('refund-reason', 'negative', 9, 'קוד 9', 9),
                ('refund-reason', 'negative', 10, 'קוד 10', 10),
                ('payment-method', 'all', 1, 'קוד 1', 1),
                ('payment-method', 'all', 3, 'קוד 3', 3),
                ('payment-method', 'all', 4, 'קוד 4', 4),
                ('payment-method', 'all', 5, 'קוד 5', 5),
                ('payment-method', 'all', 6, 'קוד 6', 6),
                ('payment-method', 'all', 7, 'קוד 7', 7),
                ('payment-method', 'all', 9, 'קוד 9', 9),
                ('account-type', 'all', 1, 'קוד 1', 1),
                ('account-type', 'all', 2, 'קוד 2', 2)
            ON CONFLICT (category, scope, code) DO UPDATE
            SET name = EXCLUDED.name,
                sort_order = EXCLUDED.sort_order,
                is_active = true,
                source = 'EmployerInterface006-XSD',
                updated_at = now();

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
