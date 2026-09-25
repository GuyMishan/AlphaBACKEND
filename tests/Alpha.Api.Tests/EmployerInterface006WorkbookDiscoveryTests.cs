using System.IO.Compression;
using System.Xml.Linq;
using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

/// <summary>
/// Guardrail against silent drift between ALPHA and the official Employer Interface V6 workbook
/// committed under docs/specifications. These assertions intentionally target the business rules
/// that are stricter than (or explanatory around) the XSD.
/// </summary>
public sealed class EmployerInterface006OfficialWorkbookTests
{
    private const string CurrentSheet = "ממשק מעסיקים - דיווח שוטף";
    private const string NegativeSheet = "ממשק מעסיקים - דיווח שלילי";

    [Fact]
    public void Official_workbook_contains_the_current_v6_rules_alpha_depends_on()
    {
        using var workbook = OpenWorkbook();

        AssertRowContains(workbook, CurrentSheet, "SUG-PEULA",
            "1 = דיווח רגיל/שוטף", "קוד 2", "קוד 3", "קוד 7", "קוד 6");
        AssertRowContains(workbook, CurrentSheet, "KOD-EMTZAI-TASHLUM",
            "1 = העברה בנקאית", "3 = כרטיס אשראי", "5 = סליקה באמצעות מסלקה פנסיונית",
            "6= הרשאה לחיוב חשבון", "7= סליקה באמצעות מס\"ב", "9 = הרשאה לחיוב חשבון");
        AssertRowContains(workbook, CurrentSheet, "SUG-MAFKID", "1 = מעסיק", "2 = עצמאי", "3 = מעסיק קטן");
        AssertRowContains(workbook, CurrentSheet, "SUG-MEZAHE-OVED", "1 = תעודת זהות", "2 = דרכון");
        AssertRowContains(workbook, CurrentSheet, "SUG-KUPA",
            "1 = קופת ביטוח", "2 = קרן פנסיה", "3 = קופת גמל", "4 = קרן השתלמות");
        AssertRowContains(workbook, CurrentSheet, "SEIF-ARBA-ESRE-LAOVED", "5 = קיים קושי משפטי");
        AssertRowContains(workbook, CurrentSheet, "HAFKADA-ACHRONA", "1 = כן", "2 = לא");
        AssertRowContains(workbook, CurrentSheet, "SUG-HAFRASHA",
            "1 = פיצויים", "2 = תגמולי עובד", "3 = תגמולי מעביד", "4 = תגמולים47",
            "5 = א.כ.ע עובד", "6 = א.כ.ע מעביד", "7 = שונות עובד", "8 = שונות מעביד");
        AssertRowContains(workbook, CurrentSheet, "MISPAR-MEZAHE-RESHUMA-KODEM", "קודים", "2, 3", "רשות בלבד");
        AssertRowContains(workbook, CurrentSheet, "MISPAR-ZIHUI-KODEM", "GUID", "גרסה 4");
        AssertRowContains(workbook, CurrentSheet, "MISPAR-MISLAKA-KODEM", "דיווח מתוקן");
    }

    [Fact]
    public void Official_workbook_contains_the_negative_v6_rules_alpha_depends_on()
    {
        using var workbook = OpenWorkbook();

        AssertRowContains(workbook, NegativeSheet, "SUG-PEULA", "קוד 5", "קוד 6", "ללא החזר תשלום");
        AssertRowContains(workbook, NegativeSheet, "KOD-EMTZAI-TASHLUM",
            "קוד 5", "קוד 6", "לא יועבר מידע בשדה זה");
        AssertRowContains(workbook, NegativeSheet, "MISPAR-ZIHUI-KODEM", "ההפקדה המקורית", "GUID", "גרסה 4");
        AssertRowContains(workbook, NegativeSheet, "SUG-MISMACH",
            "3 = תצהיר מעסיק", "4 = אישור עובד", "6 = הצהרת מעסיק");
        AssertRowContains(workbook, NegativeSheet, "SEIF-ARBA-ESRE-LAOVED", "לא רלבנטי");
        AssertRowContains(workbook, NegativeSheet, "HAFKADA-ACHRONA", "לא רלבנטי");
    }

    [Fact]
    public void Official_workbook_closing_record_matches_alpha_counting_model()
    {
        using var workbook = OpenWorkbook();

        AssertRowContains(workbook, CurrentSheet, "MISPAR-KUPOT-YATZRANIM-BAKOVETZ",
            "מספר המופעים", "קוד זיהוי קופה");
        AssertRowContains(workbook, CurrentSheet, "MISPAR-MAASIKIM",
            "מספר המופעים", "מספר זיהוי מעסיק", "לספור כל מופע בנפרד");
        AssertRowContains(workbook, CurrentSheet, "MISPAR-RESHUMOT",
            "מספר מזהה רשומה");
        AssertRowContains(workbook, CurrentSheet, "MISPAR-AMITIM",
            "מספר מזהה עובד", "לספור כל מופע בנפרד");
        AssertRowContains(workbook, CurrentSheet, "SACH-HAFRASHOT-BAKOVETZ",
            "סכום הפרשה");
        AssertRowContains(workbook, CurrentSheet, "SACH-HAFKADOT-BAKOVETZ",
            "סך הפקדת מעסיק לקופה");
    }

    [Fact]
    public void Alpha_operation_payment_matrix_remains_the_v6_matrix()
    {
        Assert.Equal([1, 3, 5, 6, 7, 9], EmployerInterface006WorkbookRules.AllowedPaymentMethods(1));
        Assert.Equal([1], EmployerInterface006WorkbookRules.AllowedPaymentMethods(2));
        Assert.Equal([1, 3, 5, 6, 7, 9], EmployerInterface006WorkbookRules.AllowedPaymentMethods(3));
        Assert.Equal([1, 3, 6, 7, 9], EmployerInterface006WorkbookRules.AllowedPaymentMethods(5));
        Assert.Empty(EmployerInterface006WorkbookRules.AllowedPaymentMethods(6));
        Assert.Equal([1], EmployerInterface006WorkbookRules.AllowedPaymentMethods(7));
    }

    private static WorkbookReader OpenWorkbook()
    {
        var root = FindRepoRoot();
        return new WorkbookReader(Path.Combine(root, "docs", "specifications", "employer-interface", "006", "Employer interface V 6.xlsx"));
    }

    private static void AssertRowContains(WorkbookReader workbook, string sheetName, string xmlElement, params string[] expected)
    {
        var row = workbook.FindRow(sheetName, xmlElement);
        Assert.False(string.IsNullOrWhiteSpace(row), $"Official workbook row {sheetName}/{xmlElement} was not found.");
        foreach (var value in expected)
            Assert.Contains(value, row, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class WorkbookReader : IDisposable
    {
        private readonly ZipArchive _zip;
        private readonly IReadOnlyList<string> _shared;
        private readonly Dictionary<string, string> _sheetTargets;

        public WorkbookReader(string path)
        {
            Assert.True(File.Exists(path), path);
            _zip = ZipFile.OpenRead(path);
            _shared = ReadSharedStrings(_zip);

            var workbook = XDocument.Load(_zip.GetEntry("xl/workbook.xml")!.Open());
            var rels = XDocument.Load(_zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open());
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
            var relMap = rels.Root!.Elements(pkg + "Relationship")
                .ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!);
            _sheetTargets = workbook.Descendants(main + "sheet").ToDictionary(
                x => ((string)x.Attribute("name")!).Trim(),
                x =>
                {
                    var target = relMap[(string)x.Attribute(rel + "id")!].Replace("../", "");
                    return target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target.TrimStart('/');
                },
                StringComparer.Ordinal);
        }

        public string FindRow(string sheetName, string xmlElement)
        {
            var target = _sheetTargets[sheetName.Trim()];
            var entry = _zip.GetEntry(target) ?? throw new InvalidOperationException($"Workbook entry {target} was not found.");
            return ReadRows(entry, _shared)
                .Where(row => row.Any(cell => string.Equals(cell.Trim(), xmlElement, StringComparison.OrdinalIgnoreCase)))
                .Select(row => string.Join(" | ", row))
                .FirstOrDefault() ?? string.Empty;
        }

        public void Dispose() => _zip.Dispose();

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return [];
            var doc = XDocument.Load(entry.Open());
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            return doc.Descendants(ns + "si")
                .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
                .ToList();
        }

        private static List<List<string>> ReadRows(ZipArchiveEntry entry, IReadOnlyList<string> shared)
        {
            var doc = XDocument.Load(entry.Open());
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = new List<List<string>>();
            foreach (var row in doc.Descendants(ns + "row"))
            {
                var values = new List<string>();
                foreach (var cell in row.Elements(ns + "c"))
                {
                    var type = (string?)cell.Attribute("t");
                    var raw = cell.Element(ns + "v")?.Value ?? "";
                    if (type == "s" && int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Count) raw = shared[idx];
                    else if (type == "inlineStr") raw = string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
                    values.Add(raw.Replace("\r", " ").Replace("\n", " ").Trim());
                }
                if (values.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(values);
            }
            return rows;
        }
    }
}
