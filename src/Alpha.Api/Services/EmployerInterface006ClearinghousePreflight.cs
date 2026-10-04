using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

/// <summary>
/// Local preflight for the subset of clearing-house FEDBKA technical checks that ALPHA can
/// deterministically perform before transmission. This does not claim to replace the
/// clearing-house acknowledgement because remote identity/authorization/duplicate state is
/// only knowable by the clearing house.
/// </summary>
public static class EmployerInterface006ClearinghousePreflight
{
    public sealed record Finding(int FedbkaCode, string Message);

    public static IReadOnlyList<Finding> Validate(
        EmployerInterfaceService.GeneratedDocument generated,
        DateTimeOffset? nowOverride = null)
    {
        var findings = new List<Finding>();
        var fileName = generated.PayloadFileName ?? string.Empty;

        if (!EmployerInterface006FileNaming.IsOfficialOutboundEmployerInterfaceName(fileName))
        {
            findings.Add(new(1, "שם קובץ הממשק אינו תואם לפורמט הרשמי של ממשק מעסיקים."));
        }

        if (generated.Bytes is null || generated.Bytes.Length == 0)
        {
            findings.Add(new(2, "קובץ הממשק ריק או אינו ניתן לקריאה."));
            return findings;
        }

        XDocument document;
        try
        {
            document = EmployerInterfaceSchemaRegistry.LoadXml(generated.Bytes);
        }
        catch (XmlException)
        {
            findings.Add(new(3, "מבנה ה-XML אינו חוקי."));
            return findings;
        }
        catch (InvalidDataException)
        {
            findings.Add(new(2, "קובץ הממשק אינו ניתן לקריאה."));
            return findings;
        }

        if (!string.Equals(document.Root?.Name.LocalName, "MimshakMaasikim", StringComparison.Ordinal))
            findings.Add(new(4, "ההיררכיה הראשית בקובץ אינה תקינה לממשק מעסיקים."));

        if (!generated.Validation.IsValid)
        {
            if (generated.Validation.Issues.Any(x =>
                    x.Contains("Invalid XML", StringComparison.OrdinalIgnoreCase)))
                AddUnique(findings, new(3, "מבנה ה-XML אינו חוקי."));
            else
                AddUnique(findings, new(4, "מבנה הקובץ אינו תואם ל-XSD הרשמי של ממשק מעסיקים 006."));
        }

        var now = nowOverride ?? IsraelNow();
        var executionText = document.Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "TAARICH-BITZUA")?.Value.Trim();

        if (TryParseTimestamp(executionText, out var executionAt)
            && executionAt > now.AddMinutes(5))
        {
            findings.Add(new(11, "תאריך ביצוע הקובץ עתידי."));
        }

        if (TryReadFileTimestamp(fileName, out var fileAt)
            && fileAt > now.AddMinutes(5))
        {
            AddUnique(findings, new(11, "תאריך הקובץ בשם הקובץ עתידי."));
        }

        if (TryParseTimestamp(executionText, out executionAt)
            && TryReadFileTimestamp(fileName, out fileAt)
            && executionAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                != fileAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture))
        {
            AddUnique(findings, new(1, "חותמת הזמן בשם הקובץ אינה תואמת לתאריך הביצוע שבתוכן הקובץ."));
        }

        return findings;
    }

    private static void AddUnique(List<Finding> findings, Finding finding)
    {
        if (!findings.Any(x => x.FedbkaCode == finding.FedbkaCode
            && string.Equals(x.Message, finding.Message, StringComparison.Ordinal)))
            findings.Add(finding);
    }

    private static bool TryReadFileTimestamp(string fileName, out DateTimeOffset value)
    {
        value = default;
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (!EmployerInterface006FileNaming.IsOfficialOutboundEmployerInterfaceName(name) || name.Length < 41)
            return false;
        return TryParseTimestamp(name.Substring(27, 14), out value);
    }

    private static bool TryParseTimestamp(string? raw, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParseExact(raw, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return false;

        var offset = IsraelOffset(local);
        value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
        return true;
    }

    private static TimeSpan IsraelOffset(DateTime local)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem").GetUtcOffset(local);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeSpan.Zero;
        }
    }

    private static DateTimeOffset IsraelNow()
    {
        try
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem"));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
