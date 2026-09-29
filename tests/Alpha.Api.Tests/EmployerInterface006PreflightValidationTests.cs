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
}
