using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class EmployerInterface006ErrorCodeInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS reference_data.employer_interface_006_error_codes (
                code integer PRIMARY KEY,
                description varchar(700) NOT NULL,
                responsibility varchar(40) NOT NULL,
                is_active boolean NOT NULL DEFAULT true,
                source varchar(100) NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO reference_data.employer_interface_006_error_codes
                (code, description, responsibility, is_active, source, updated_at)
            VALUES
                (4, 'מספר ת.ז/דרכון לא קיים אצל יצרן', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (11, 'ת.ז לא מתאימה לפרטים אישיים', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (13, 'לא ניתן להשיב תשלום שהופקד ביתר - בחשבון אין כספים', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (16, 'לא עומד בתקנה 19-  לא ניתן להפקיד לרכיב תגמולי עובד ללא רכיב תגמולי מעסיק', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (17, 'לא עומד בתקנה 19- לא ניתן להפקיד לרכיב תגמולי מעסיק ללא רכיב תגמולי עובד', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (18, 'חסר ערך בשדה חובה מותנית, שם השדה מופיע בפירוט השגיאה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (19, 'לא ניתן לדווח הפקדה לעובד במקביל לדיווח סטטוס לפיו לא הועברו הפקדות', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (23, 'לא עומד בתקנה 19 - לא ניתן להפקיד לרכיב פיצויים ללא תגמולי עובד ומעסיק', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (27, 'לא ניתן להפקיד בגין חודש שכר עתידי', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (29, 'לא ניתן להשיב תשלום שהופקד ביתר - חשבון מעוקל', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (30, 'לא ניתן להשיב תשלום שהופקד ביתר לשייך / לבטל זכויות - חשבון משועבד', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (31, 'התנועה המקורית תקינה ומותאמת (עבור דיווח מתקן)', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (32, 'מספר מזהה רשומה דווח בעבר', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (33, 'תאריך בשדה "תחילת סטטוס עובד" אינו תקין', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (34, 'העמית נפטר', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (43, 'דווחה תנועה כפולה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (44, 'דיווח לא תקין על אחוז משרה/ימי עבודה בחודש', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (45, 'אין יתרה זמינה בחשבון המעסיק לפירעון רשימת ההפקדה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (50, 'דיווח כפול על מספר זיהוי של פרטי העברת כספים', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (51, 'לא ניתן להשיב תשלום שהופקד ביתר - תנועה שלילית גדולה מהיתרה לעמית בחודש העבודה המדווח', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (52, 'אין הסכם לעמית בקרן ותיקה מול המעסיק', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (53, 'אין התאמה בין אחוז הפרשה, סכום הפרשה ושכר', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (56, 'דווח חשבון עו"ש שגוי להפקדת כספים', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (59, 'שגיאה בשדה סך תשלומים פטורים', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (61, 'הפקדה דווחה לסוג קופה שגוי בחברה המנהלת', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (62, 'ספרת ביקורת שגויה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (66, 'לא ניתן להשיב תשלום שהופקד ביתר - בוצע פדיון', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (69, 'לא ניתן להשיב תשלום שהופקד ביתר - חוסר בתצהיר', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (70, 'לא ניתן להשיב תשלום שהופקד ביתר - תצהיר אינו תקין', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (71, 'תגמולי עובד ותגמולי מעסיק עד 5% מהשכר נדרשים להיות זהים', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (72, 'לא ניתן להפקיד מעבר לתקרת הפקדה המוגדרת בתקנה 19', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (73, 'דווח על סכום פטור במצטבר שהופקד בגין חודשים בשנת המס הגבוה מסך הפקדות מתחילת השנה שהופקדו בגין חודשים של אותה שנת מס', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (74, 'לא ניתן לדווח על תשלום פטור לרכיב ביטוח', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (75, 'במעמד הפקדת שכיר-שוטף חובה לדווח על השכר ממנו הועברו תשלומים לקופה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (78, 'בתאריך בו בוצעה ההפקדה לעובד טרם מלאו 18', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (79, 'שגיאה בהליך הפקדת הכספים אינה מאפשרת קליטת הרשומה - ראה שגיאה בשדה "סטטוס טיפול בכספים" (שדה 43)', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (83, 'השבת כספים יזומה מגוף מוסדי למעסיק - התראה ראשונה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (84, 'השבת כספים יזומה מגוף מוסדי למעסיק - התראה שנייה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (85, 'השבת כספים יזומה מגוף מוסדי למעסיק - השבה בפועל', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (86, 'לא הועברו פרטי קשר של עובד בהצטרפות לקופת ברירת מחדל', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (87, 'לא ניתן להשיב תשלום שהופקד ביתר - בוצע ניוד', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (88, 'לא ניתן להשיב תשלום שהופקד ביתר - תשלום כבר הוחזר למעסיק', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (93, 'סכום שדווח בממשק שלילי לחודש השכר גבוה מהפקדה - לא ניתן לבצע החזר', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (96, 'לא ניתן לבטל תנועה- בוצעה השבת תשלום למעסיק', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (97, 'לא אותרה הפקדה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (98, 'לא אותר מזהה רשומה', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (99, 'החזר למעסיק בגין תשלום פרמיית א.כ.ע בעודף', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (100, 'דווח בממשק השוטף בשדה סוג פעולה קוד 2 או קוד 3 אך לא דווח בממשק שלילי בשדה זה קוד 6 בגין פעולות מתקנות כנדרש.', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (101, 'דווח בממשק השלילי בשדה סוג פעולה קוד 6 אך לא דווח בממשק השוטף בשדה זה קוד 2 או קוד 3 בגין פעולות כנדרש.', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (102, 'תצהיר מעסיק אגב הסכם קיבוצי לא תקין', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (103, 'לא התקבל תצהיר מעסיק אגב הסכם קיבוצי', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (104, 'לא ניתן לדווח סוג תקבול חד פעמי לרכיבי ביטוח', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (109, 'תצהיר עובד לא תקין', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (110, 'לא התקבל תצהיר עובד', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (111, 'לא התקבל תצהיר מעסיק', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (112, 'תצהיר מעסיק לא תקין', 'employer', true, 'EmployerInterface006-Workbook-V6', now()),
                (116, 'הפקדה לקרן שאינה תואמת את מנגנון החלוקה בהתאם לספרת הביקורת בת.ז. של העמית', 'employer', true, 'EmployerInterface006-Workbook-V6', now())
            ON CONFLICT (code) DO UPDATE
            SET description = EXCLUDED.description,
                responsibility = EXCLUDED.responsibility,
                is_active = EXCLUDED.is_active,
                source = EXCLUDED.source,
                updated_at = now();
            """, ct);
}
