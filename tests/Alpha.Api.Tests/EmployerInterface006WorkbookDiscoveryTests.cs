using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006WorkbookDiscoveryTests
{
    [Fact]
    public void Dump_official_workbook_structure_for_audit()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "specifications", "employer-interface", "006", "Employer interface V 6.xlsx");
        Assert.True(File.Exists(path), path);

        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        var rels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open());
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        var relMap = rels.Root!.Elements(pkg + "Relationship")
            .ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!);

        var lines = new List<string>();
        foreach (var sheet in workbook.Descendants(main + "sheet"))
        {
            var name = (string)sheet.Attribute("name")!;
            var id = (string)sheet.Attribute(rel + "id")!;
            var target = relMap[id].Replace("../", "");
            if (!target.StartsWith("xl/")) target = "xl/" + target.TrimStart('/');
            var entry = zip.GetEntry(target);
            if (entry is null) continue;
            var rows = ReadRows(entry, shared);
            lines.Add($"=== {name} ({rows.Count} rows) ===");
            foreach (var row in rows.Take(12))
                lines.Add(string.Join(" | ", row));
            foreach (var row in rows.Where(r => r.Any(v => Keywords.Any(k => v.Contains(k, StringComparison.OrdinalIgnoreCase)))).Take(30))
                lines.Add("MATCH: " + string.Join(" | ", row));
        }

        Assert.Fail(string.Join(Environment.NewLine, lines.Take(500)));
    }

    private static readonly string[] Keywords =
    [
        "SUG-PEULA", "KOD-EMTZAI-TASHLUM", "MISPAR-ZIHUI-KODEM", "MISPAR-MISLAKA-KODEM",
        "MISPAR-MEZAHE-RESHUMA-KODEM", "HAFKADA-ACHRONA", "SEIF-ARBA-ESRE-LAOVED",
        "SUG-MEZAHE-OVED", "SUG-HAFRASHA", "SUG-MISMACH", "SUG-KUPA",
        "SCHUM-HAFKADA-KOLEL", "SACH-HAFKADA-KUPA-H-P", "MISPAR-MAASIKIM",
        "MISPAR-KUPOT-YATZRANIM-BAKOVETZ", "MISPAR-RESHUMOT", "MISPAR-AMITIM",
        "SACH-HAFRASHOT-BAKOVETZ", "SACH-HAFKADOT-BAKOVETZ", "SUG-MAFKID",
        "MISPAR-TIK-NIKUIM-MAASIK", "SUG-CHESHBON-MAASIK", "SUG-CHESHBON-KOLET-TASHLUM",
        "MISPAR-BANK-MAASIK", "MISPAR-SNIF-MAASIK", "MISPAR-CHESHBON-MAASIK",
        "MISPAR-BANK-KOLET", "MISPAR-SNIF-KOLET", "MISPAR-CHESHBON-KOLET"
    ];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

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
