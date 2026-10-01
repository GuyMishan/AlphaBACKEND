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

    public static IReadOnlyList<RecordStatus> Parse(string xml) => ParseSummary(xml).Records;

    public static string Description(int? code) => code switch
    {
        1 => "לא דווחה שגיאה ברשומה",
        4 => "מזהה העובד אינו קיים אצל היצרן",
        5 => "הפוליסה או החשבון אינם פעילים",
        18 => "חסר נתון בשדה חובה מותנית",
        24 => "לא נמצא חשבון קופת גמל לעובד אצל המעסיק",
        25 => "לא נמצאה קרן השתלמות לעובד אצל המעסיק",
        26 => "לא נמצאה קרן פנסיה לעובד אצל המעסיק",
        28 => "חודש השכר דווח פעמיים",
        32 => "מזהה הרשומה כבר דווח בעבר",
        43 => "נמצא דיווח כפול על תנועת ההפקדה",
        53 => "אין התאמה בין שיעור ההפרשה, הסכום והשכר",
        55 => "חסרים מסמכים הדרושים לקליטת ההפקדה",
        56 => "חשבון הבנק שדווח אינו תקין",
        75 => "חסר השכר שממנו חושבה ההפקדה",
        null => "לא התקבל קוד שגיאה",
        _ => $"משוב מסלקה: קוד שגיאה {code} (ראו פירוט במשוב הרשמי)",
    };
}
