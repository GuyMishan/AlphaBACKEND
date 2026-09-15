using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class SalaryLayerSchemaInitializer
{
    public static async Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS reference_data;

            CREATE TABLE IF NOT EXISTS reference_data.salary_layers (
                code integer PRIMARY KEY,
                name varchar(120) NOT NULL,
                sort_order integer NOT NULL,
                is_active boolean NOT NULL DEFAULT true
            );

            INSERT INTO reference_data.salary_layers (code, name, sort_order, is_active)
            VALUES
                (1, 'שכר יסוד', 1, true),
                (3, 'דמי הבראה', 2, true),
                (5, 'שעות נוספות', 3, true),
                (6, 'החזר הוצאות', 4, true),
                (7, 'אחר', 5, true)
            ON CONFLICT (code) DO UPDATE SET
                name = EXCLUDED.name,
                sort_order = EXCLUDED.sort_order,
                is_active = EXCLUDED.is_active;
            """, ct);
    }
}
