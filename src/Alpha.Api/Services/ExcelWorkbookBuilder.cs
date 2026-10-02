using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Alpha.Api.Services;

public static class ExcelWorkbookBuilder
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static byte[] Build(string sheetName, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        ArgumentNullException.ThrowIfNull(rows);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteContentTypes(archive);
            WriteRootRelationships(archive);
            WriteWorkbook(archive, SanitizeSheetName(sheetName));
            WriteWorkbookRelationships(archive);
            WriteStyles(archive);
            WriteWorksheet(archive, rows);
        }

        return output.ToArray();
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "[Content_Types].xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", ContentTypesNamespace);
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", "rels");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", "xml");
        writer.WriteAttributeString("ContentType", "application/xml");
        writer.WriteEndElement();
        WriteOverride(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteOverride(writer, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        WriteOverride(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteOverride(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override", ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "_rels/.rels");
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbook(ZipArchive archive, string sheetName)
    {
        using var writer = CreateXmlWriter(archive, "xl/workbook.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);
        writer.WriteStartElement("bookViews", SpreadsheetNamespace);
        writer.WriteStartElement("workbookView", SpreadsheetNamespace);
        writer.WriteAttributeString("rightToLeft", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", sheetName);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", RelationshipsNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/_rels/workbook.xml.rels");
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml");
        WriteRelationship(writer, "rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteStyles(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive, "xl/styles.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("styleSheet", SpreadsheetNamespace);

        writer.WriteStartElement("fonts", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("font", SpreadsheetNamespace);
        writer.WriteStartElement("sz", SpreadsheetNamespace); writer.WriteAttributeString("val", "11"); writer.WriteEndElement();
        writer.WriteStartElement("name", SpreadsheetNamespace); writer.WriteAttributeString("val", "Arial"); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("font", SpreadsheetNamespace);
        writer.WriteStartElement("b", SpreadsheetNamespace); writer.WriteEndElement();
        writer.WriteStartElement("sz", SpreadsheetNamespace); writer.WriteAttributeString("val", "11"); writer.WriteEndElement();
        writer.WriteStartElement("name", SpreadsheetNamespace); writer.WriteAttributeString("val", "Arial"); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("fills", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace); writer.WriteAttributeString("patternType", "none"); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("fill", SpreadsheetNamespace);
        writer.WriteStartElement("patternFill", SpreadsheetNamespace); writer.WriteAttributeString("patternType", "gray125"); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("borders", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border", SpreadsheetNamespace);
        writer.WriteStartElement("left", SpreadsheetNamespace); writer.WriteEndElement();
        writer.WriteStartElement("right", SpreadsheetNamespace); writer.WriteEndElement();
        writer.WriteStartElement("top", SpreadsheetNamespace); writer.WriteEndElement();
        writer.WriteStartElement("bottom", SpreadsheetNamespace); writer.WriteEndElement();
        writer.WriteStartElement("diagonal", SpreadsheetNamespace); writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "2");
        WriteCellFormat(writer, "0");
        WriteCellFormat(writer, "1");
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles", SpreadsheetNamespace);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle", SpreadsheetNamespace);
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteCellFormat(XmlWriter writer, string fontId)
    {
        writer.WriteStartElement("xf", SpreadsheetNamespace);
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", fontId);
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("applyFont", "1");
        writer.WriteStartElement("alignment", SpreadsheetNamespace);
        writer.WriteAttributeString("horizontal", "right");
        writer.WriteAttributeString("vertical", "center");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorksheet(ZipArchive archive, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        using var writer = CreateXmlWriter(archive, "xl/worksheets/sheet1.xml");
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);

        writer.WriteStartElement("sheetViews", SpreadsheetNamespace);
        writer.WriteStartElement("sheetView", SpreadsheetNamespace);
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteAttributeString("rightToLeft", "1");
        if (rows.Count > 1)
        {
            writer.WriteStartElement("pane", SpreadsheetNamespace);
            writer.WriteAttributeString("ySplit", "1");
            writer.WriteAttributeString("topLeftCell", "A2");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();

        var maxColumns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        if (maxColumns > 0)
        {
            writer.WriteStartElement("cols", SpreadsheetNamespace);
            for (var column = 0; column < maxColumns; column++)
            {
                var maxLength = rows.Select(row => column < row.Count ? DisplayValue(row[column]).Length : 0).DefaultIfEmpty(0).Max();
                writer.WriteStartElement("col", SpreadsheetNamespace);
                writer.WriteAttributeString("min", (column + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("max", (column + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("width", Math.Clamp(maxLength + 2, 10, 45).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        writer.WriteStartElement("sheetData", SpreadsheetNamespace);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
                WriteCell(writer, row[columnIndex], columnIndex, rowNumber, rowIndex == 0);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        if (rows.Count > 1 && maxColumns > 0)
        {
            writer.WriteStartElement("autoFilter", SpreadsheetNamespace);
            writer.WriteAttributeString("ref", $"A1:{ColumnName(maxColumns - 1)}{rows.Count}");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteCell(XmlWriter writer, object? value, int columnIndex, int rowNumber, bool header)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", $"{ColumnName(columnIndex)}{rowNumber}");
        if (header) writer.WriteAttributeString("s", "1");

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            writer.WriteAttributeString("t", "n");
            writer.WriteStartElement("v", SpreadsheetNamespace); writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); writer.WriteEndElement();
        }
        else if (value is bool boolean)
        {
            writer.WriteAttributeString("t", "b");
            writer.WriteStartElement("v", SpreadsheetNamespace); writer.WriteString(boolean ? "1" : "0"); writer.WriteEndElement();
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SpreadsheetNamespace);
            writer.WriteStartElement("t", SpreadsheetNamespace);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(DisplayValue(value));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string DisplayValue(object? value) => value switch
    {
        null => string.Empty,
        DateOnly date => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string ColumnName(int zeroBasedIndex)
    {
        var value = zeroBasedIndex + 1;
        var name = string.Empty;
        while (value > 0)
        {
            value--;
            name = (char)('A' + value % 26) + name;
            value /= 26;
        }
        return name;
    }

    private static string SanitizeSheetName(string value)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (sanitized.Length == 0) sanitized = "דוח";
        return sanitized.Length <= 31 ? sanitized : sanitized[..31];
    }

    private static XmlWriter CreateXmlWriter(ZipArchive archive, string path)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        return XmlWriter.Create(entry.Open(), new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = true
        });
    }
}
