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
}
