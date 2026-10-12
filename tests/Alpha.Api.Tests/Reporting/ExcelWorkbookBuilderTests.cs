using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

public sealed class ExcelWorkbookBuilderTests
{
    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Build_creates_valid_xlsx_package_with_rtl_sheet_and_data()
    {
        var bytes = ExcelWorkbookBuilder.Build("דוח בדיקה",
        [
            new object?[] { "עובד", "סכום" },
            new object?[] { "ישראל ישראלי", 123.45m }
        ]);

        Assert.True(bytes.Length > 4);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);

        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/styles.xml"));
        var worksheetEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("xl/worksheets/sheet1.xml"));

        using var worksheetStream = worksheetEntry.Open();
        using var worksheetBytes = new MemoryStream();
        worksheetStream.CopyTo(worksheetBytes);
        Assert.Equal("8a819d8059efa01e8af2bea3daae75e0bcc149de19b03a7928920183803134ee",
            Convert.ToHexString(SHA256.HashData(worksheetBytes.ToArray())).ToLowerInvariant());
        worksheetBytes.Position = 0;
        var worksheet = XDocument.Load(worksheetBytes);
        var sheetView = Assert.Single(worksheet.Descendants(Spreadsheet + "sheetView"));
        Assert.Equal("1", sheetView.Attribute("rightToLeft")?.Value);

        var cells = worksheet.Descendants(Spreadsheet + "c").ToArray();
        Assert.Contains(cells, cell => cell.Descendants(Spreadsheet + "t").Any(x => x.Value == "ישראל ישראלי"));
        Assert.Contains(cells, cell => cell.Descendants(Spreadsheet + "v").Any(x => x.Value == "123.45"));
    }
}
