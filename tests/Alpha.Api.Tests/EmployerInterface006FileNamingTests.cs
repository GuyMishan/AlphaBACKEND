using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006FileNamingTests
{
    [Fact]
    public void Current_production_file_uses_official_006_dat_name()
    {
        var preparedAt = new DateTimeOffset(2026, 9, 25, 8, 9, 10, TimeSpan.FromHours(3));

        var result = EmployerInterface006FileNaming.Build("123456789", 17, negative: false,
            preparedAt, sequence: 12, testFile: false);

        Assert.Equal("017000123456789EMPONG000006202609250809100012.DAT", result.PayloadFileName);
        Assert.Equal("017000123456789EMPONG000006202609250809100012", result.BaseName);
    }

    [Fact]
    public void Negative_test_file_and_attachment_use_official_package_base()
    {
        var preparedAt = new DateTimeOffset(2026, 9, 25, 8, 9, 10, TimeSpan.FromHours(3));

        var result = EmployerInterface006FileNaming.Build("123456789", 17, negative: true,
            preparedAt, sequence: 3, testFile: true);
        var attachment = EmployerInterface006FileNaming.BuildAttachmentFileName(result.BaseName, 2, ".pdf");

        Assert.Equal("017000123456789EMPNEG000006202609250809100003.TST", result.PayloadFileName);
        Assert.Equal("017000123456789EMPNEG000006202609250809100003_002.PDF", attachment);
    }
}
