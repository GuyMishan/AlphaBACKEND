using System.Xml;
using System.Xml.Linq;

namespace Alpha.Api.Services;

/// <summary>
/// Extracts only public record-status fields from already XSD-validated 006
/// clearinghouse feedback; caller enforces employer/report scope and maps
/// record identifiers to exported report contributions.
/// </summary>
public static class EmployerInterfaceLineFeedbackParser
{
    public sealed record RecordStatus(
        string RecordIdentifier,
        int? IntakeStatus,
        int? ErrorCode,
        string Description,
        string SourceFileName = "",
        DateTimeOffset? ReceivedAt = null);

    private static string Field(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";

    public static IReadOnlyList<RecordStatus> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 12_000_000
        });
        var document = XDocument.Load(reader);
        return document.Descendants()
            .Where(x => x.Name.LocalName == "StatosPirteiKlitatReshuma")
            .Select(x => {
                var identifier = Field(x, "MISPAR-MEZAHE-RESHUMA").ToUpperInvariant();
                var status = int.TryParse(Field(x, "RESHUMA-NIKLETA"), out var intake) ? intake : (int?)null;
                var error = int.TryParse(Field(x, "SUG-SHGIHA"), out var code) ? code : (int?)null;
                return new RecordStatus(identifier, status, error, Description(error));
            })
            .Where(x => Guid.TryParse(x.RecordIdentifier, out _))
            .ToArray();
    }

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
