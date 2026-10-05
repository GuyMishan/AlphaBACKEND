using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Alpha.Api.Services;

public static class EmployerInterfaceLineFeedbackParser
{
    public sealed record RightsStatus(
        int? ContributionTypeCode,
        decimal? CalculatedSalary,
        DateOnly? SalaryMonth,
        string PolicyNumber,
        decimal? ContributionRate,
        decimal? ContributionAmount);

    public sealed record RecordStatus(
        string RecordIdentifier,
        int? IntakeStatus,
        int? ErrorCode,
        string Description,
        decimal? ErrorAmount = null,
        DateOnly? ErrorDate = null,
        IReadOnlyList<RightsStatus>? Rights = null,
        string SourceFileName = "",
        DateTimeOffset? ReceivedAt = null);

    public sealed record TransferStatus(
        string TransferIdentifier,
        string ClearingIdentifier,
        decimal ReportedDepositAmount,
        decimal ActualReceivedAmount,
        decimal AllocatedAmount,
        decimal InTransitAmount,
        decimal? ProactiveRefundAmount,
        decimal? EmployerAccountRefundAmount,
        int? MoneyTreatmentStatus,
        string StatusDetail,
        string PaymentReference,
        DateOnly? ValueDate,
        DateOnly? TrustAccountValueDate,
        string CorrectnessTimestamp);

    public sealed record ParsedFeedback(IReadOnlyList<TransferStatus> Transfers, IReadOnlyList<RecordStatus> Records);

    private static string Field(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";

    private static int? IntField(XElement parent, string name) =>
        int.TryParse(Field(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal? DecimalField(XElement parent, string name) =>
        decimal.TryParse(Field(parent, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateOnly? DateField(XElement parent, string name) =>
        DateOnly.TryParse(Field(parent, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    public static ParsedFeedback ParseSummary(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new([], []);
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 12_000_000
        });
        var document = XDocument.Load(reader);

        var transfers = document.Descendants()
            .Where(x => x.Name.LocalName == "StatosPirteiHaavaratKsafim")
            .Select(x => new TransferStatus(
                Field(x, "MISPAR-ZIHUI").ToUpperInvariant(),
                Field(x, "MISPAR-MISLAKA").ToUpperInvariant(),
                DecimalField(x, "SACH-HAFKADA-KUPA-H-P") ?? 0m,
                DecimalField(x, "SACH-HAFKADA-KLITA-BAPOAL") ?? 0m,
                DecimalField(x, "SACH-KSAFIM-SHUICHU") ?? 0m,
                DecimalField(x, "KSAFIM-BEMAHAVAR") ?? 0m,
                DecimalField(x, "HASHAVAT-KSAFIM-YEZUMA"),
                DecimalField(x, "HASHAVAT-KSAFIM-CHESHBON-MAASIK"),
                IntField(x, "STATUS-TIPUL-BEKSAFIM"),
                Field(x, "PERUT-STATUS"),
                Field(x, "MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM"),
                DateField(x, "TAARICH-ERECH-HAFKADA"),
                DateField(x, "TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT"),
                Field(x, "TAARICH-NECHONUT")))
            .ToArray();

        var records = document.Descendants()
            .Where(x => x.Name.LocalName == "StatosPirteiKlitatReshuma")
            .Select(x =>
            {
                var identifier = Field(x, "MISPAR-MEZAHE-RESHUMA").ToUpperInvariant();
                var status = IntField(x, "RESHUMA-NIKLETA");
                var error = IntField(x, "SUG-SHGIHA");
                var rights = x.Elements()
                    .Where(child => child.Name.LocalName == "OfenRishumZchuiot")
                    .Select(right => new RightsStatus(
                        IntField(right, "SUG-HAFRASHA"),
                        DecimalField(right, "SACHAR-MECHUSHAV"),
                        DateField(right, "CHODESH-MASKORET"),
                        Field(right, "MISPAR-POLISA-O-HESHBON"),
                        DecimalField(right, "SHIUR-HAFRASHA"),
                        DecimalField(right, "SCHUM-HAFRASHA")))
                    .ToArray();
                return new RecordStatus(identifier, status, error, Description(error),
                    DecimalField(x, "PERUT-SHGIHA-SHUM"), DateField(x, "PERUT-SHGIHA-TAARICH"), rights);
            })
            .Where(x => Guid.TryParse(x.RecordIdentifier, out _))
            .ToArray();

        return new(transfers, records);
    }

    public static bool TryParseSummary(string xml, out ParsedFeedback parsed)
    {
        try
        {
            parsed = ParseSummary(xml);
            return true;
        }
        catch (XmlException)
        {
            parsed = new([], []);
            return false;
        }
    }

    public static IReadOnlyList<RecordStatus> Parse(string xml) => ParseSummary(xml).Records;

    public static IReadOnlyList<int> OfficialErrorCodes { get; } =
    [
        1, 2, 3, 4, 5, 6, 7, 11, 13, 15, 16, 17, 18, 19, 20, 21, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        39, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 61, 62, 63, 64, 66, 67, 68, 69,
        70, 71, 72, 73, 74, 75, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96,
        97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116
    ];

    public static IReadOnlyList<int> OfficialFailureCodes { get; } =
        OfficialErrorCodes.Where(code => code != 1).ToArray();

    public static bool IsOfficialErrorCode(int code) => OfficialErrorCodes.Contains(code);

    public enum FeedbackErrorScope
    {
        Contribution,
        Deposit,
        Employee,
        Money,
        Report,
        Informational
    }

    private static readonly IReadOnlyDictionary<int, FeedbackErrorScope> ErrorScopes =
        new Dictionary<int, FeedbackErrorScope>
        {
            [1] = FeedbackErrorScope.Informational,
            [2] = FeedbackErrorScope.Deposit,
            [3] = FeedbackErrorScope.Deposit,
            [4] = FeedbackErrorScope.Employee,
            [5] = FeedbackErrorScope.Deposit,
            [6] = FeedbackErrorScope.Contribution,
            [7] = FeedbackErrorScope.Contribution,
            [11] = FeedbackErrorScope.Employee,
            [13] = FeedbackErrorScope.Money,
            [15] = FeedbackErrorScope.Deposit,
            [16] = FeedbackErrorScope.Contribution,
            [17] = FeedbackErrorScope.Contribution,
            [18] = FeedbackErrorScope.Report,
            [19] = FeedbackErrorScope.Employee,
            [20] = FeedbackErrorScope.Contribution,
            [21] = FeedbackErrorScope.Contribution,
            [23] = FeedbackErrorScope.Contribution,
            [24] = FeedbackErrorScope.Deposit,
            [25] = FeedbackErrorScope.Deposit,
            [26] = FeedbackErrorScope.Deposit,
            [27] = FeedbackErrorScope.Report,
            [28] = FeedbackErrorScope.Deposit,
            [29] = FeedbackErrorScope.Money,
            [30] = FeedbackErrorScope.Money,
            [31] = FeedbackErrorScope.Informational,
            [32] = FeedbackErrorScope.Deposit,
            [33] = FeedbackErrorScope.Employee,
            [34] = FeedbackErrorScope.Employee,
            [39] = FeedbackErrorScope.Deposit,
            [42] = FeedbackErrorScope.Deposit,
            [43] = FeedbackErrorScope.Deposit,
            [44] = FeedbackErrorScope.Employee,
            [45] = FeedbackErrorScope.Money,
            [46] = FeedbackErrorScope.Deposit,
            [47] = FeedbackErrorScope.Employee,
            [48] = FeedbackErrorScope.Employee,
            [49] = FeedbackErrorScope.Employee,
            [50] = FeedbackErrorScope.Money,
            [51] = FeedbackErrorScope.Money,
            [52] = FeedbackErrorScope.Deposit,
            [53] = FeedbackErrorScope.Contribution,
            [54] = FeedbackErrorScope.Deposit,
            [55] = FeedbackErrorScope.Deposit,
            [56] = FeedbackErrorScope.Money,
            [57] = FeedbackErrorScope.Deposit,
            [58] = FeedbackErrorScope.Deposit,
            [59] = FeedbackErrorScope.Contribution,
            [61] = FeedbackErrorScope.Deposit,
            [62] = FeedbackErrorScope.Employee,
            [63] = FeedbackErrorScope.Contribution,
            [64] = FeedbackErrorScope.Contribution,
            [66] = FeedbackErrorScope.Money,
            [67] = FeedbackErrorScope.Deposit,
            [68] = FeedbackErrorScope.Deposit,
            [69] = FeedbackErrorScope.Money,
            [70] = FeedbackErrorScope.Money,
            [71] = FeedbackErrorScope.Deposit,
            [72] = FeedbackErrorScope.Deposit,
            [73] = FeedbackErrorScope.Deposit,
            [74] = FeedbackErrorScope.Contribution,
            [75] = FeedbackErrorScope.Deposit,
            [77] = FeedbackErrorScope.Deposit,
            [78] = FeedbackErrorScope.Employee,
            [79] = FeedbackErrorScope.Money,
            [80] = FeedbackErrorScope.Employee,
            [81] = FeedbackErrorScope.Contribution,
            [82] = FeedbackErrorScope.Contribution,
            [83] = FeedbackErrorScope.Money,
            [84] = FeedbackErrorScope.Money,
            [85] = FeedbackErrorScope.Money,
            [86] = FeedbackErrorScope.Employee,
            [87] = FeedbackErrorScope.Money,
            [88] = FeedbackErrorScope.Money,
            [89] = FeedbackErrorScope.Money,
            [90] = FeedbackErrorScope.Money,
            [91] = FeedbackErrorScope.Deposit,
            [92] = FeedbackErrorScope.Money,
            [93] = FeedbackErrorScope.Money,
            [94] = FeedbackErrorScope.Money,
            [95] = FeedbackErrorScope.Money,
            [96] = FeedbackErrorScope.Money,
            [97] = FeedbackErrorScope.Money,
            [98] = FeedbackErrorScope.Deposit,
            [99] = FeedbackErrorScope.Money,
            [100] = FeedbackErrorScope.Report,
            [101] = FeedbackErrorScope.Report,
            [102] = FeedbackErrorScope.Report,
            [103] = FeedbackErrorScope.Report,
            [104] = FeedbackErrorScope.Contribution,
            [105] = FeedbackErrorScope.Contribution,
            [106] = FeedbackErrorScope.Contribution,
            [107] = FeedbackErrorScope.Contribution,
            [108] = FeedbackErrorScope.Contribution,
            [109] = FeedbackErrorScope.Employee,
            [110] = FeedbackErrorScope.Employee,
            [111] = FeedbackErrorScope.Report,
            [112] = FeedbackErrorScope.Report,
            [113] = FeedbackErrorScope.Contribution,
            [114] = FeedbackErrorScope.Contribution,
            [115] = FeedbackErrorScope.Deposit,
            [116] = FeedbackErrorScope.Deposit
        };

    public static FeedbackErrorScope ErrorScope(int? code)
    {
        if (!code.HasValue) return FeedbackErrorScope.Informational;
        return ErrorScopes.TryGetValue(code.Value, out var scope)
            ? scope
            : FeedbackErrorScope.Report;
    }

    public static bool HasExplicitErrorScope(int code) => ErrorScopes.ContainsKey(code);

    public static string Description(int? code) => code switch
    {
        1 => "אין שגיאה",
        2 => "רשומה פוצלה בין פוליסות שונות",
        3 => "רשומה פוצלה בין קרן חדשה מקיפה לקרן חדשה כללית",
        4 => "מספר ת.ז/דרכון לא קיים אצל יצרן",
        5 => "הפוליסות/חשבונות של המבוטח אינן פעילות ואינן ניתנות לחידוש",
        6 => "חוסר תשלום לכיסוי ביטוחי - מעסיק",
        7 => "עודף תשלום לכיסוי ביטוחי - מעסיק",
        11 => "ת.ז לא מתאימה לפרטים אישיים",
        13 => "לא ניתן להשיב תשלום שהופקד ביתר - בחשבון אין כספים",
        15 => "הפקדה לתכנית מסולקת",
        16 => "לא עומד בתקנה 19- לא ניתן להפקיד לרכיב תגמולי עובד ללא רכיב תגמולי מעסיק",
        17 => "לא עומד בתקנה 19- לא ניתן להפקיד לרכיב תגמולי מעסיק ללא רכיב תגמולי עובד",
        18 => "חסר ערך בשדה חובה מותנית, מספר השדה מופיע בפירוט השגיאה",
        19 => "לא ניתן לדווח הפקדה לעובד במקביל לדיווח סטטוס לפיו לא הועברו הפקדות",
        20 => "הפקדת רכיב פיצויים לעמית/מבוטח במעמד עצמאי",
        21 => "הפקדת רכיב תגמולי מעביד לעמית/מבוטח במעמד עצמאי",
        23 => "לא עומד בתקנה 19 - לא ניתן להפקיד לרכיב פיצויים ללא תגמולי עובד ומעסיק",
        24 => "אין קופת גמל לעובד תחת המעסיק",
        25 => "אין קרן השתלמות לעובד תחת המעסיק",
        26 => "אין קרן פנסיה לעובד תחת המעסיק",
        27 => "לא ניתן להפקיד בגין חודש שכר עתידי",
        28 => "חודש שכר דווח כפול",
        29 => "לא ניתן להשיב תשלום שהופקד ביתר - חשבון מעוקל",
        30 => "לא ניתן להשיב תשלום שהופקד ביתר לשייך / לבטל זכויות - חשבון משועבד",
        31 => "התנועה המקורית תקינה ומותאמת (עבור דיווח מתקן)",
        32 => "מספר מזהה רשומה דווח בעבר",
        33 => "תאריך בשדה \"תחילת סטטוס עובד\" אינו תקין",
        34 => "העמית נפטר",
        39 => "הפקדה להצעה שטרם הושלם בגינה תהליך הפקת הפוליסה/חשבון",
        42 => "אין קופת ביטוח לעובד תחת המעסיק",
        43 => "דווחה תנועה כפולה",
        44 => "דיווח לא תקין על אחוז משרה/ימי עבודה בחודש",
        45 => "אין יתרה זמינה בחשבון המעסיק לפירעון רשימת ההפקדה",
        46 => "לא ניתן לקלוט תשלום לחשבון העמית בקרן ותיקה, שכן לא דווחה תנועה לעמית יותר מ-24 חודשים",
        47 => "לא ניתן לקלוט תנועה לעמית המקבל קצבת זקנה בקרן ותיקה",
        48 => "לא ניתן לקלוט תנועה לעמית המקבל קצבת נכות מלאה",
        49 => "לא ניתן לקלוט תנועה לעמית שמשך כספים מקרן ותיקה",
        50 => "דיווח כפול על מספר זיהוי של פרטי העברת כספים",
        51 => "לא ניתן להשיב תשלום שהופקד ביתר - תנועה שלילית גדולה מהיתרה לעמית בחודש העבודה המדווח",
        52 => "אין הסכם לעמית בקרן ותיקה מול המעסיק",
        53 => "אין התאמה בין אחוז הפרשה, סכום הפרשה ושכר",
        54 => "הפקדה בגין חודש שכר קודם למועד פתיחת התכנית",
        55 => "לא ניתן לקלוט הפקדה בשל אי השלמת מסמכים",
        56 => "דווח חשבון עו\"ש שגוי להפקדת כספים",
        57 => "רשומה פוצלה במלואה לקרן חדשה כללית",
        58 => "קרן ההשתלמות של העובד חסומה להפקדות",
        59 => "שגיאה בשדה סך תשלומים פטורים",
        61 => "הפקדה דווחה לסוג קופה שגוי בחברה המנהלת",
        62 => "ספרת ביקורת שגויה",
        63 => "הפקדה לרכיב שאינו קיים בפוליסת הביטוח",
        64 => "הפקדה לרכיב שאינו קיים במוצר",
        66 => "לא ניתן להשיב תשלום שהופקד ביתר - בוצע פדיון",
        67 => "לא ניתן לקלוט הפקדה בשל אי השלמת מסמכים לביצוע שינויים בפוליסה",
        68 => "אין התאמה בין דיווח המעסיק לבין תנאי הפוליסה",
        69 => "לא ניתן להשיב תשלום שהופקד ביתר - חוסר בתצהיר",
        70 => "לא ניתן להשיב תשלום שהופקד ביתר - תצהיר אינו תקין",
        71 => "תגמולי עובד ותגמולי מעסיק עד 5% מהשכר נדרשים להיות זהים",
        72 => "לא ניתן להפקיד מעבר לתקרת הפקדה המוגדרת בתקנה 19",
        73 => "דיווח על סכום פטור במצטבר שהופקדו עבור חודשים בשנת המס גבוה מסך הפקדות מתחילת השנה שהופקדו עבור חודשים של אותה שנת מס",
        74 => "לא ניתן לדווח על תשלום פטור לרכיב ביטוח",
        75 => "במעמד הפקדת שכיר - שוטף חובה לדווח על השכר ממנו הועברו תשלומים לקופה",
        77 => "הפוליסות/חשבונות של המבוטח אינן פעילות וניתנות לחידוש",
        78 => "בתאריך בו בוצעה ההפקדה לעובד טרם מלאו 18",
        79 => "שגיאה בהליך הפקדת הכספים אינה מאפשרת קליטת הרשומה - ראה שגיאה בשדה \"סטטוס טיפול בכספים\" (שדה 43)",
        80 => "בקשת העובד להתקבל כעמית/מבוטח נדחתה",
        81 => "חוסר תשלום לכיסוי ביטוחי - עובד",
        82 => "עודף תשלום לכיסוי ביטוחי - עובד",
        83 => "השבת כספים יזומה מגוף מוסדי למעסיק - התראה ראשונה",
        84 => "השבת כספים יזומה מגוף מוסדי למעסיק - התראה שנייה",
        85 => "השבת כספים יזומה מגוף מוסדי למעסיק - השבה בפועל",
        86 => "לא הועברו פרטי קשר של עובד בהצטרפות לקופת ברירת מחדל",
        87 => "לא ניתן להשיב תשלום שהופקד ביתר - בוצע ניוד",
        88 => "לא ניתן להשיב תשלום שהופקד ביתר - תשלום כבר הוחזר למעסיק",
        89 => "לא ניתן להשיב תשלום שהופקד ביתר - הפוליסה בסטטוס תביעה",
        90 => "לא ניתן להשיב תשלום שהופקד ביתר - הפוליסה בסטטוס ביטול",
        91 => "לא נקלט כתוצאה מאי התאמה לחוקת הפיצול - בטיפול הגוף המוסדי.",
        92 => "לא ניתן לבצע החזר - הפוליסה בסטטוס תום תקופה, הועבר לטיפול מחלקת גביה",
        93 => "סכום שדווח בממשק שלילי לחודש השכר גבוה מהפקדה - לא ניתן לבצע החזר",
        94 => "לא ניתן לבטל תנועה- בוצעה משיכה",
        95 => "לא ניתן לבטל תנועה- בוצע ניוד",
        96 => "לא ניתן לבטל תנועה- בוצעה השבת תשלום למעסיק",
        97 => "לא אותרה הפקדה",
        98 => "לא אותר מזהה רשומה",
        99 => "החזר למעסיק בגין תשלום פרמיית א.כ.ע בעודף",
        100 => "דווח בממשק השוטף בשדה סוג פעולה קוד 2 או קוד 3 אך לא דווח בממשק שלילי בשדה זה קוד 6 בגין פעולות מתקנות כנדרש.",
        101 => "דווח בממשק השלילי בשדה סוג פעולה קוד 6 אך לא דווח בממשק השוטף בשדה זה קוד 2 או קוד 3 בגין פעולות כנדרש.",
        102 => "תצהיר מעסיק אגב הסכם קיבוצי לא תקין",
        103 => "לא התקבל תצהיר מעסיק אגב הסכם קיבוצי",
        104 => "לא ניתן לדווח סוג תקבול חד פעמי לרכיבי ביטוח",
        105 => "חוסר תשלום לתגמולי מעסיק",
        106 => "עודף תשלום לתגמולי מעסיק",
        107 => "חוסר תשלום לתגמולי עובד",
        108 => "עודף תשלום לתגמולי עובד",
        109 => "תצהיר עובד לא תקין",
        110 => "לא התקבל תצהיר עובד",
        111 => "לא התקבל תצהיר מעסיק",
        112 => "תצהיר מעסיק לא תקין",
        113 => "חוסר תשלום לפיצויי מעסיק",
        114 => "עודף תשלום לפיצויי מעסיק",
        115 => "אין בפוליסה כיסוי אכ\"ע לעובד",
        116 => "הפקדה לקרן שאינה תואמת את מנגנון החלוקה בהתאם לספרת הביקורת בת.ז. של העמית",
        null => "לא התקבל קוד שגיאה",
        _ => $"משוב מסלקה: קוד שגיאה {code} (ראו פירוט במשוב הרשמי)",
    };}
