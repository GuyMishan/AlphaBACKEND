using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerInterface006CodebookInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            INSERT INTO reference_data.employer_interface_006_options
                (category, scope, code, name, sort_order, is_active, source, updated_at)
            VALUES
                ('gender', 'all', 1, 'זכר', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('gender', 'all', 2, 'נקבה', 2, true, 'EmployerInterface006-Workbook-V6', now()),

                ('receipt-type', 'all', 1, 'שוטף', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('receipt-type', 'all', 2, 'חד פעמי', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('receipt-type', 'all', 4, 'הפרשים', 4, true, 'EmployerInterface006-Workbook-V6', now()),
                ('receipt-type', 'all', 6, 'חד פעמי - השלמת פיצויים עד שכר מבוטח כפול וותק', 6, true, 'EmployerInterface006-Workbook-V6', now()),
                ('receipt-type', 'all', 8, 'חד פעמי - הפקדה לפיצויים מעל שכר מבוטח כפול וותק', 8, true, 'EmployerInterface006-Workbook-V6', now()),

                ('operation-code', 'current', 1, 'דיווח רגיל/שוטף', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('operation-code', 'current', 2, 'דיווח על תיקון תנועות ללא הפקדה נוספת לאחר קוד 6', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('operation-code', 'current', 3, 'דיווח על הפקדה נוספת ותיקון תנועות לאחר קוד 6', 3, true, 'EmployerInterface006-Workbook-V6', now()),
                ('operation-code', 'current', 7, 'תיקון תשלומים פטורים בלבד', 7, true, 'EmployerInterface006-Workbook-V6', now()),
                ('operation-code', 'negative', 5, 'בקשה להחזר תשלום על הפקדה ביתר', 5, true, 'EmployerInterface006-Workbook-V6', now()),
                ('operation-code', 'negative', 6, 'בקשה לביטול חלקי או מלא של תנועה ללא החזר תשלום למעסיק', 6, true, 'EmployerInterface006-Workbook-V6', now()),

                ('deposit-status', 'all', 1, 'שכיר', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('deposit-status', 'all', 2, 'עצמאי', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('deposit-status', 'all', 3, 'בעל שליטה', 3, true, 'EmployerInterface006-Workbook-V6', now()),

                ('employee-status', 'all', 1, 'חודשי/רגיל', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 2, 'שעתי/יומי', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 3, 'היעדר שכר', 3, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 4, 'עונתי', 4, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 5, 'עזיבת עבודה', 5, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 8, 'חופשה ללא תשלום', 8, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 9, 'פטירה', 9, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 10, 'עובד החל להפקיד בקופה אחרת', 10, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 11, 'מעבר ממשרד למשרד (מעבר בין חברות בתוך אותה קבוצה)', 11, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 12, 'פרישה לפנסיה', 12, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 14, 'עובד/עמית חדש', 14, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 17, 'עזיבת עבודה - עובד זכאי לכספי פיצויים', 17, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employee-status', 'all', 18, 'תשלום + דיווח עזיבת עבודה עתידי', 18, true, 'EmployerInterface006-Workbook-V6', now()),

                ('last-deposit', 'all', 1, 'כן', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('last-deposit', 'all', 2, 'לא', 2, true, 'EmployerInterface006-Workbook-V6', now()),

                ('refund-reason', 'negative', 1, 'תשלום ביתר לעובד לאחר עזיבת עבודה', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 2, 'סוג מוצר שגוי', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 3, 'הפקדה לעובד שגוי', 3, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 4, 'הפקדה בגין חודש שכר שגוי', 4, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 5, 'תיקון טעות בחישוב השכר', 5, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 6, 'הפקדה לחברה מנהלת לא נכונה', 6, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 7, 'עובד עבר לקופת גמל אחרת', 7, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 8, 'הסכם קיבוצי', 8, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 9, 'הפקדה ביתר לרכיב', 9, true, 'EmployerInterface006-Workbook-V6', now()),
                ('refund-reason', 'negative', 10, 'החזר למעסיק בגין תשלום פרמיית א.כ.ע בעודף', 10, true, 'EmployerInterface006-Workbook-V6', now()),

                ('payment-method', 'all', 1, 'העברה בנקאית', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('payment-method', 'all', 3, 'כרטיס אשראי', 3, true, 'EmployerInterface006-Workbook-V6', now()),
                ('payment-method', 'all', 4, 'שובר תשלום', 4, true, 'EmployerInterface006-Workbook-V6', now()),
                ('payment-method', 'all', 5, 'סליקה באמצעות מסלקה פנסיונית', 5, true, 'EmployerInterface006-Workbook-V6', now()),
                ('payment-method', 'all', 6, 'הרשאה לחיוב חשבון/הוראת קבע', 6, true, 'EmployerInterface006-Workbook-V6', now()),
                ('payment-method', 'all', 7, 'סליקה באמצעות מס"ב', 7, true, 'EmployerInterface006-Workbook-V6', now()),
                ('payment-method', 'all', 9, 'הרשאה לחיוב חשבון של מעסיק על סמך קובץ דיווח שהועבר לגוף מוסדי', 9, true, 'EmployerInterface006-Workbook-V6', now()),

                ('employer-account-type', 'current', 1, 'חשבון מעסיק', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('employer-account-type', 'current', 2, 'חשבון נאמנות', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('receiver-account-type', 'current', 1, 'חשבון יצרן', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('receiver-account-type', 'current', 2, 'חשבון נאמנות', 2, true, 'EmployerInterface006-Workbook-V6', now()),

                ('old-pension-type', 'current', 1, 'מקיפה', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('old-pension-type', 'current', 2, 'יסוד', 2, true, 'EmployerInterface006-Workbook-V6', now()),

                ('section14-code', 'all', 1, 'כן - סעיף 14 חל ממועד תחילת ההעסקה', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('section14-code', 'all', 2, 'כן - סעיף 14 חל מתאריך שונה ממועד תחילת ההעסקה', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('section14-code', 'all', 3, 'העובד אינו חתום על סעיף 14', 3, true, 'EmployerInterface006-Workbook-V6', now()),
                ('section14-code', 'all', 4, 'סעיף 14 אינו חל עוד על העובד החל מתאריך אחר', 4, true, 'EmployerInterface006-Workbook-V6', now()),
                ('section14-code', 'all', 5, 'קיים קושי משפטי לקבוע אם חל סעיף 14 עקב הסכמים שונים', 5, true, 'EmployerInterface006-Workbook-V6', now()),

                ('contribution-type', 'all', 1, 'פיצויים', 1, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 2, 'תגמולי עובד', 2, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 3, 'תגמולי מעביד', 3, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 4, 'תגמולים 47', 4, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 5, 'א.כ.ע עובד', 5, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 6, 'א.כ.ע מעביד', 6, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 7, 'שונות עובד', 7, true, 'EmployerInterface006-Workbook-V6', now()),
                ('contribution-type', 'all', 8, 'שונות מעביד', 8, true, 'EmployerInterface006-Workbook-V6', now())
            ON CONFLICT (category, scope, code) DO UPDATE
            SET name = EXCLUDED.name,
                sort_order = EXCLUDED.sort_order,
                is_active = EXCLUDED.is_active,
                source = EXCLUDED.source,
                updated_at = now();

            UPDATE reference_data.select_options
               SET is_active = false, updated_at = now()
             WHERE category = 'manual-payment-method';
            """, ct);
}
