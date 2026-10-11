using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

/// <summary>Version 006 numbered fields, keyed by interface type and field number.
/// Populated from the committed official workbook; never use an error amount as a
/// field number without the error-code-specific workbook rule (e.g. code 18).</summary>
public static class EmployerInterface006FieldMapInitializer
{
    private sealed record FieldRow(int InterfaceType, int FieldNumber, string FieldName,
        string XmlTag, string XmlBlock, string DataType, int SourceRow);

    public static async Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS reference_data.employer_interface_006_fields (
                interface_type integer NOT NULL,
                field_number integer NOT NULL,
                field_name text NOT NULL,
                xml_tag text NOT NULL,
                xml_block text NOT NULL,
                data_type text NOT NULL,
                source_row integer NOT NULL,
                specification text NOT NULL DEFAULT 'Employer interface V 6.xlsx',
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (interface_type, field_number)
            );
            """;
        await db.Database.ExecuteSqlRawAsync(ddl, ct);

        var workbookPath = Path.Combine(AppContext.BaseDirectory, "Specifications", "EmployerInterface", "006",
            "Employer interface V 6.xlsx");
        if (!File.Exists(workbookPath))
            throw new FileNotFoundException("The official Employer Interface V6 workbook is required for field mapping.", workbookPath);

        var fields = ReadFields(workbookPath);
        if (fields.Count != 321 || fields.GroupBy(row => row.InterfaceType).Any(group =>
            group.Count() != (group.Key is 12 or 13 ? 95 : group.Key == 14 ? 72 : 59)))
            throw new InvalidDataException("Unexpected official Employer Interface V6 field catalog; refuse partial seeding.");

        var json = JsonSerializer.Serialize(fields.Select(field => new {
            interface_type = field.InterfaceType,
            field_number = field.FieldNumber,
            field_name = field.FieldName,
            xml_tag = field.XmlTag,
            xml_block = field.XmlBlock,
            data_type = field.DataType,
            source_row = field.SourceRow
        }));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reference_data.employer_interface_006_fields
                (interface_type, field_number, field_name, xml_tag, xml_block, data_type, source_row)
            SELECT interface_type, field_number, field_name, xml_tag, xml_block, data_type, source_row
            FROM jsonb_to_recordset(CAST({json} AS jsonb))
                AS input(interface_type integer, field_number integer, field_name text,
                    xml_tag text, xml_block text, data_type text, source_row integer)
            ON CONFLICT (interface_type, field_number) DO UPDATE
                SET field_name = EXCLUDED.field_name, xml_tag = EXCLUDED.xml_tag,
                    xml_block = EXCLUDED.xml_block, data_type = EXCLUDED.data_type,
                    source_row = EXCLUDED.source_row, updated_at = now();
            """, ct);
    }

    private static List<FieldRow> ReadFields(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        var strings = sharedEntry is null ? Array.Empty<string>() :
            XDocument.Load(sharedEntry.Open()).Descendants(m + "si")
                .Select(si => string.Concat(si.Descendants(m + "t").Select(t => t.Value))).ToArray();
        var sheets = new (int Index, int Type)[] { (2, 12), (3, 13), (5, 14), (6, 16) };
        var result = new List<FieldRow>();
        foreach (var (index, type) in sheets)
        {
            var entry = zip.GetEntry($"xl/worksheets/sheet{index}.xml")
                ?? throw new InvalidDataException($"Missing official worksheet {index}");
            var block = "";
            foreach (var row in XDocument.Load(entry.Open()).Descendants(m + "sheetData").Elements(m + "row"))
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var cell in row.Elements(m + "c"))
                {
                    var cellRef = (string?)cell.Attribute("r") ?? "";
                    var column = new string(cellRef.TakeWhile(char.IsLetter).ToArray());
                    var raw = (string?)cell.Element(m + "v") ?? "";
                    if ((string?)cell.Attribute("t") == "s" && int.TryParse(raw, out var stringIndex))
                        raw = strings[stringIndex];
                    else if ((string?)cell.Attribute("t") == "inlineStr")
                        raw = string.Concat(cell.Descendants(m + "t").Select(t => t.Value));
                    values[column] = raw.Trim();
                }
                values.TryGetValue("A", out var numberText);
                values.TryGetValue("B", out var element);
                if (string.IsNullOrWhiteSpace(numberText) && !string.IsNullOrWhiteSpace(element)
                    && char.IsLetter(element[0])) block = element;
                if (!int.TryParse(numberText, out var number) || !values.TryGetValue("I", out var xmlTag)
                    || string.IsNullOrWhiteSpace(xmlTag)) continue;
                result.Add(new FieldRow(type, number, values.GetValueOrDefault("D", ""),
                    xmlTag, block, values.GetValueOrDefault("J", ""),
                    int.Parse((string?)row.Attribute("r") ?? "0")));
            }
        }
        return result;
    }
}
