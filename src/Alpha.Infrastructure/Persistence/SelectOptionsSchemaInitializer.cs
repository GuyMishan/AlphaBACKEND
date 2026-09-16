using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class SelectOptionsSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS reference_data;

            CREATE TABLE IF NOT EXISTS reference_data.select_options (
                category varchar(80) NOT NULL,
                scope varchar(40) NOT NULL DEFAULT 'all',
                value varchar(80) NOT NULL,
                label varchar(160) NOT NULL,
                sort_order integer NOT NULL DEFAULT 0,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(80) NOT NULL DEFAULT 'Alpha',
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (category, scope, value)
            );
            CREATE INDEX IF NOT EXISTS ix_select_options_lookup
                ON reference_data.select_options (category, scope, is_active, sort_order, value);

            INSERT INTO reference_data.select_options (category, scope, value, label, sort_order, source)
            VALUES
                ('pension-product-type', 'all', '1', 'קרן פנסיה', 1, 'Alpha.Domain'),
                ('pension-product-type', 'all', '2', 'קרן השתלמות', 2, 'Alpha.Domain'),
                ('pension-product-type', 'all', '3', 'ביטוח מנהלים', 3, 'Alpha.Domain'),
                ('pension-product-type', 'all', '4', 'קופת גמל', 4, 'Alpha.Domain'),
                ('pension-product-type', 'all', '99', 'אחר', 99, 'Alpha.Domain'),

                ('salary-allocation-type', 'all', '1', 'שכר קבוע', 1, 'Alpha.Domain'),
                ('salary-allocation-type', 'all', '2', 'אחוז מהשכר', 2, 'Alpha.Domain'),
                ('salary-allocation-type', 'all', '3', 'עד תקרה', 3, 'Alpha.Domain'),
                ('salary-allocation-type', 'all', '4', 'יתרת שכר', 4, 'Alpha.Domain'),

                ('product-active-status', 'all', 'active', 'פעיל', 1, 'Alpha.UI'),
                ('product-active-status', 'all', 'inactive', 'לא פעיל', 2, 'Alpha.UI'),

                ('organization-role', 'all', '1', 'מנהל ארגון', 1, 'Alpha.Authorization'),
                ('organization-role', 'all', '2', 'מנהל שכר', 2, 'Alpha.Authorization'),
                ('organization-role', 'all', '3', 'נציג תפעול', 3, 'Alpha.Authorization'),
                ('organization-role', 'all', '4', 'צפייה בלבד', 4, 'Alpha.Authorization'),

                ('employer-access-mode', 'all', '1', 'כל המעסיקים בארגון', 1, 'Alpha.Authorization'),
                ('employer-access-mode', 'all', '2', 'מעסיקים מסוימים בלבד', 2, 'Alpha.Authorization'),

                ('manual-payment-method', 'all', 'bank-transfer', 'העברה בנקאית', 1, 'Alpha.Reporting'),
                ('manual-payment-method', 'all', 'masav', 'מס״ב', 2, 'Alpha.Reporting'),
                ('manual-payment-method', 'all', 'check', 'המחאה', 3, 'Alpha.Reporting'),
                ('manual-payment-method', 'all', 'other', 'אחר', 4, 'Alpha.Reporting'),

                ('manual-report-kind', 'all', '1', 'שוטף', 1, 'Alpha.Reporting'),
                ('manual-report-kind', 'all', '2', 'הפרשים', 2, 'Alpha.Reporting'),
                ('manual-report-kind', 'all', '3', 'שלילי', 3, 'Alpha.Reporting'),

                ('employment-status', 'all', '1', 'פעיל', 1, 'Alpha.Domain'),
                ('employment-status', 'all', '2', 'חל״ת', 2, 'Alpha.Domain'),
                ('employment-status', 'all', '3', 'סיים עבודה', 3, 'Alpha.Domain')
            ON CONFLICT (category, scope, value) DO UPDATE
            SET label = EXCLUDED.label,
                sort_order = EXCLUDED.sort_order,
                is_active = true,
                source = EXCLUDED.source,
                updated_at = now();
            """, ct);
}
