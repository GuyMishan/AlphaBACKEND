using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006FileNamingTests
{
    [Fact]
    public void Current_production_file_uses_official_006_dat_name()
    {
        var preparedAt = new DateTimeOffset(2026, 9, 25, 8, 9, 10, TimeSpan.FromHours(3));

        var result = EmployerInterface006FileNaming.Build("123456789", 3, negative: false,
            preparedAt, sequence: 12, testFile: false);

        Assert.Equal("003000123456789EMPONG000006202609250809100012.DAT", result.PayloadFileName);
        Assert.Equal("003000123456789EMPONG000006202609250809100012", result.BaseName);
        Assert.True(EmployerInterface006FileNaming.IsOfficialOutboundEmployerInterfaceName(result.PayloadFileName));
    
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(103)]
    [InlineData(205)]
    public void Employer_interface_clearinghouse_outbound_rejects_other_directions(int direction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmployerInterface006FileNaming.Build("123456789", direction, negative: false,
                DateTimeOffset.UtcNow, sequence: 1, testFile: true));
    
    [Fact]
    public void Default_options_use_service_bureau_direction()
    {
        var options = new EmployerInterface006Options();

        Assert.Equal(6, options.SenderCode);
        Assert.Equal(EmployerInterface006FileNaming.ServiceBureauToClearinghouseDirection, options.FileDirectionCode);
    }
}

    [Theory]
    [InlineData("003000123456789EMPONG000006202609250809100001.TST", true)]
    [InlineData("006000123456789EMPNEG000006202609250809100001.DAT", true)]
    [InlineData("205000123456789EMPONG000006202609250809100001.DAT", false)]
    [InlineData("003000123456789EMPFED000006202609250809100001.DAT", false)]
    [InlineData("003000123456789EMPONGPNN006202609250809100001.DAT", false)]
    public void Official_outbound_name_validator_matches_annex_vi(string fileName, bool expected)
    {
        Assert.Equal(expected, EmployerInterface006FileNaming.IsOfficialOutboundEmployerInterfaceName(fileName));
    }
}

    [Fact]
    public void Negative_test_file_and_attachment_use_official_package_base()
    {
        var preparedAt = new DateTimeOffset(2026, 9, 25, 8, 9, 10, TimeSpan.FromHours(3));

        var result = EmployerInterface006FileNaming.Build("123456789", 6, negative: true,
            preparedAt, sequence: 3, testFile: true);
        var attachment = EmployerInterface006FileNaming.BuildAttachmentFileName(result.BaseName, 2, ".pdf");

        Assert.Equal("006000123456789EMPNEG000006202609250809100003.TST", result.PayloadFileName);
        Assert.Equal("006000123456789EMPNEG000006202609250809100003_002.PDF", attachment);
        Assert.True(EmployerInterface006FileNaming.IsOfficialOutboundEmployerInterfaceName(result.PayloadFileName));
    }
}
