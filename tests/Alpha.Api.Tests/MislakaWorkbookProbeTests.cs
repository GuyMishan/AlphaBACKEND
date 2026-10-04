using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Alpha.Api.Tests;

// Temporary discovery guard: remove after the clearing-house technical feedback catalog is committed.
public sealed class MislakaWorkbookProbeTests
{
    [Fact]
    public void Probe_official_mislaka_workbook()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "specifications", "mislaka",
            "מעסיקים-גרסה-6.0-טבלאות-קודי-שגיאה-סופי.xlsx");
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
            var name = ((string)sheet.Attribute("name")!).Trim();
            var target = relMap[(string)sheet.Attribute(rel + "id")!].Replace("../", "");
            target = target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target.TrimStart('/');
            var entry = zip.GetEntry(target)!;
            var rows = ReadRows(entry, shared);
            lines.Add($"SHEET: {name}");
            foreach (var row in rows)
            {
                var joined = string.Join(" | ", row);
                if (joined.Contains("שגיא", StringComparison.OrdinalIgnoreCase)
                    || joined.Contains("FEDBK", StringComparison.OrdinalIgnoreCase)
                    || joined.Contains("טכני", StringComparison.OrdinalIgnoreCase)
                    || joined.Contains("תקין", StringComparison.OrdinalIgnoreCase)
                    || joined.Contains("קוד", StringComparison.OrdinalIgnoreCase))
                    lines.Add(joined);
            }
        }

        throw new Xunit.Sdk.XunitException(string.Join("\n", lines));
    }

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
