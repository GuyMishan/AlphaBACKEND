using Alpha.Api.Services;
using Alpha.Api.Endpoints;
using Alpha.Api.Validation;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006PreflightValidationTests
{
    [Fact]
    public void Non_bank_payment_does_not_require_receiver_account()
    {
        var request = new SaveManualReportPaymentRequest(
            "Test Fund",
            "",
            "3",
            new DateOnly(2026, 9, 16),
            "REF-1",
            "Test Bank",
            "10",
            "123",
            "123456",
            "");

        var errors = ApiInputValidation.Payment(request);

        Assert.DoesNotContain(errors, x => x.Contains("חשבון יצרן לזיכוי", StringComparison.Ordinal));
    }

    [Fact]
    public void Bank_transfer_requires_receiver_account()
    {
        var request = new SaveManualReportPaymentRequest(
            "Test Fund",
            "",
            "1",
            new DateOnly(2026, 9, 16),
            "REF-1",
            "Test Bank",
            "10",
            "123",
            "123456",
            "");

        var errors = ApiInputValidation.Payment(request);

        Assert.Contains(errors, x => x.Contains("חשבון יצרן לזיכוי", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 3, true)]
    [InlineData(1, 4, false)]
    [InlineData(2, 1, true)]
    [InlineData(2, 3, false)]
    [InlineData(3, 7, true)]
    [InlineData(5, 1, true)]
    [InlineData(5, 3, true)]
    [InlineData(5, 4, false)]
    [InlineData(6, null, true)]
    [InlineData(6, 1, false)]
    [InlineData(7, 1, true)]
    [InlineData(7, 9, false)]
    public void Operation_payment_matrix_matches_official_v6_rules(int operationCode, int? paymentMethodCode, bool expected)
    {
        Assert.Equal(expected, EmployerInterface006WorkbookRules.IsPaymentMethodAllowed(operationCode, paymentMethodCode));
    }

    [Fact]
    public void Clearinghouse_preflight_accepts_valid_generated_package()
    {
        var preparedAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.FromHours(3));
        var name = EmployerInterface006FileNaming.Build("123456789", 6, negative: false,
            preparedAt, sequence: 1, testFile: true).PayloadFileName;
        var xml = System.Text.Encoding.UTF8.GetBytes(
            "<MimshakMaasikim><KoteretKovetz><TAARICH-BITZUA>20261004103000</TAARICH-BITZUA></KoteretKovetz></MimshakMaasikim>");
        var generated = new EmployerInterfaceService.GeneratedDocument(
            xml,
            new EmployerInterfaceService.FileValidation(true, EmployerInterfaceDocumentType.CurrentReport, "006", "schema.xsd", []),
            name,
            []);

        var findings = EmployerInterface006ClearinghousePreflight.Validate(generated, preparedAt);

        Assert.Empty(findings);
    }

    [Fact]
    public void Clearinghouse_preflight_maps_bad_filename_to_fedbka_1()
    {
        var preparedAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.FromHours(3));
        var xml = System.Text.Encoding.UTF8.GetBytes(
            "<MimshakMaasikim><KoteretKovetz><TAARICH-BITZUA>20261004103000</TAARICH-BITZUA></KoteretKovetz></MimshakMaasikim>");
        var generated = new EmployerInterfaceService.GeneratedDocument(
            xml,
            new EmployerInterfaceService.FileValidation(true, EmployerInterfaceDocumentType.CurrentReport, "006", "schema.xsd", []),
            "BAD.TST",
            []);

        var findings = EmployerInterface006ClearinghousePreflight.Validate(generated, preparedAt);

        Assert.Contains(findings, x => x.FedbkaCode == 1);
    }

    [Fact]
    public void Clearinghouse_preflight_maps_wrong_root_to_fedbka_4()
    {
        var preparedAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.FromHours(3));
        var name = EmployerInterface006FileNaming.Build("123456789", 6, negative: false,
            preparedAt, sequence: 1, testFile: true).PayloadFileName;
        var xml = System.Text.Encoding.UTF8.GetBytes(
            "<WrongRoot><TAARICH-BITZUA>20261004103000</TAARICH-BITZUA></WrongRoot>");
        var generated = new EmployerInterfaceService.GeneratedDocument(
            xml,
            new EmployerInterfaceService.FileValidation(true, EmployerInterfaceDocumentType.CurrentReport, "006", "schema.xsd", []),
            name,
            []);

        var findings = EmployerInterface006ClearinghousePreflight.Validate(generated, preparedAt);

        Assert.Contains(findings, x => x.FedbkaCode == 4);
    }

    [Fact]
    public void Clearinghouse_preflight_maps_future_file_date_to_fedbka_11()
    {
        var preparedAt = new DateTimeOffset(2026, 10, 4, 10, 30, 0, TimeSpan.FromHours(3));
        var future = preparedAt.AddHours(2);
        var name = EmployerInterface006FileNaming.Build("123456789", 6, negative: false,
            future, sequence: 1, testFile: true).PayloadFileName;
        var xml = System.Text.Encoding.UTF8.GetBytes(
            "<MimshakMaasikim><KoteretKovetz><TAARICH-BITZUA>20261004123000</TAARICH-BITZUA></KoteretKovetz></MimshakMaasikim>");
        var generated = new EmployerInterfaceService.GeneratedDocument(
            xml,
            new EmployerInterfaceService.FileValidation(true, EmployerInterfaceDocumentType.CurrentReport, "006", "schema.xsd", []),
            name,
            []);

        var findings = EmployerInterface006ClearinghousePreflight.Validate(generated, preparedAt);

        Assert.Contains(findings, x => x.FedbkaCode == 11);
    }

    [Fact]
    public void Clearinghouse_preflight_maps_empty_payload_to_fedbka_2()
    {
        var generated = new EmployerInterfaceService.GeneratedDocument(
            [],
            new EmployerInterfaceService.FileValidation(false, EmployerInterfaceDocumentType.CurrentReport, "006", "schema.xsd", ["empty"]),
            "006000123456789EMPONG000006202610041030000001.TST",
            []);

        var findings = EmployerInterface006ClearinghousePreflight.Validate(generated);

        Assert.Contains(findings, x => x.FedbkaCode == 2);
    }
}
