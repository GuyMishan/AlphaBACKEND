using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerInterface006ReferenceRulesInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            INSERT INTO reference_data.employer_interface_006_options
                (category, scope, code, name, sort_order, is_active, source, updated_at)
            VALUES
                ('previous-reference-exception', 'all', 1, 'הדיווח המקורי לא בוצע באמצעות ממשק מעסיקים', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('previous-reference-exception', 'all', 2, 'הדיווח המקורי נשלח באמצעות גורם מתפעל אחר', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('previous-reference-exception', 'all', 3, 'בוצע ניוד והדיווח המקורי נשלח לחברה המעבירה', 3, true, 'EmployerInterface006-Workbook-V6', now())
            ON CONFLICT (category, scope, code) DO UPDATE
            SET name = EXCLUDED.name,
                sort_order = EXCLUDED.sort_order,
                is_active = true,
                source = EXCLUDED.source,
                updated_at = now();
            """, ct);
}
