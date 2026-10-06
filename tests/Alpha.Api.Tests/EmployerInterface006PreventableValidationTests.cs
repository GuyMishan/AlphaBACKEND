using Alpha.Api.Endpoints;
using Alpha.Api.Validation;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006PreventableValidationTests
{
    private static readonly ContributionPercentageLimit[] Limits =
    [
        new(2026, PensionProductType.PensionFund, ContributionParty.Employer, ContributionComponent.Severance, 8.33m),
        new(2026, PensionProductType.PensionFund, ContributionParty.Employer, ContributionComponent.Benefits, 7.5m),
        new(2026, PensionProductType.PensionFund, ContributionParty.Employee, ContributionComponent.Benefits, 7m)
    ];

    [Fact]
    public void Rejects_duplicate_regular_employee_benefits_rows()
    {
        var product = Product(1000m,
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)],
            [
                new(ContributionComponent.Benefits, 30m, 3m, 0m),
                new(ContributionComponent.Benefits, 30m, 3m, 0m)
            ]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.Contains(errors, x => x.Contains("כל רכיב הפקדה יכול להופיע פעם אחת"));
    }

    [Fact]
    public void Allows_multiple_employee_code_4_rows_without_confusing_them_with_regular_benefits()
    {
        var product = Product(1000m,
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)],
            [
                new(ContributionComponent.Benefits, 60m, 6m, 0m),
                new(ContributionComponent.Severance, 10m, 1m, 0m),
                new(ContributionComponent.Severance, 20m, 2m, 0m)
            ]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.DoesNotContain(errors, x => x.Contains("כל רכיב הפקדה יכול להופיע פעם אחת"));
    }

    [Fact]
    public void Rejects_employee_benefits_without_employer_benefits_error_16()
    {
        var errors = Validate(
            employer: [],
            employee: [new(ContributionComponent.Benefits, 60m, 6m, 0m)]);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 16"));
    }

    [Fact]
    public void Rejects_employer_benefits_without_employee_benefits_error_17()
    {
        var errors = Validate(
            employer: [new(ContributionComponent.Benefits, 65m, 6.5m, 0m)],
            employee: []);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 17"));
    }

    [Fact]
    public void Rejects_severance_without_both_benefits_error_23()
    {
        var errors = Validate(
            employer: [new(ContributionComponent.Severance, 83.3m, 8.33m, 0m)],
            employee: []);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 23"));
    }

    [Fact]
    public void Rejects_future_salary_month_error_27()
    {
        var product = new ManualProductInput(PensionProductType.PensionFund, "P1", new DateOnly(2200, 1, 1), 1000m,
            "1", "1", false, null, 3, "fund", "111", "Fund", "Company", "",
            SalaryAllocationType.Fixed, 1000m, 0,
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)],
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 27"));
    }

    [Fact]
    public void Rejects_invalid_israeli_id_checksum_error_62_rule()
    {
        Assert.False(ApiInputValidation.IsIsraeliId("123456789"));
        Assert.True(ApiInputValidation.IsIsraeliId("123456782"));
    }

    [Fact]
    public void Rejects_salary_amount_rate_mismatch_error_53()
    {
        var errors = Validate(
            employer: [new(ContributionComponent.Benefits, 50m, 6.5m, 0m)],
            employee: [new(ContributionComponent.Benefits, 60m, 6m, 0m)]);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 53"));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(60, 0)]
    public void Rejects_partially_populated_salary_rate_amount_as_error_53(decimal amount, decimal percentage)
    {
        var errors = Validate(
            employer: [new(ContributionComponent.Benefits, 60m, 6m, 0m)],
            employee: [new(ContributionComponent.Benefits, amount, percentage, 0m)]);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 53"));
    }

    [Fact]
    public void Wire_rounding_half_cent_matches_error_53_validation()
    {
        var product = Product(100.50m,
            [new(ContributionComponent.Benefits, 1.01m, 1m, 0m)],
            [new(ContributionComponent.Benefits, 1.01m, 1m, 0m)]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.DoesNotContain(errors, x => x.Contains("קוד שגיאה 53"));
    }

    [Theory]
    [InlineData("6.123")]
    [InlineData("6.999")]
    public void Rejects_percentage_precision_beyond_official_xsd(string raw)
    {
        var percentage = decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        var product = Product(1000m,
            [new(ContributionComponent.Benefits, 60m, percentage, 0m)],
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.Contains(errors, x => x.Contains("עד 2 ספרות אחרי הנקודה"));
    }

    [Fact]
    public void Rejects_money_precision_beyond_official_xsd()
    {
        var product = Product(1000m,
            [new(ContributionComponent.Benefits, 60.001m, 6m, 0m)],
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.Contains(errors, x => x.Contains("עד 2 ספרות אחרי הנקודה"));
    }

    [Fact]
    public void Rejects_unequal_employee_and_employer_rates_up_to_five_error_71()
    {
        var errors = Validate(
            employer: [new(ContributionComponent.Benefits, 40m, 4m, 0m)],
            employee: [new(ContributionComponent.Benefits, 50m, 5m, 0m)]);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 71"));
    }

    [Fact]
    public void Rejects_combined_employer_benefits_and_disability_above_7_5_error_72()
    {
        var errors = Validate(
            employer:
            [
                new(ContributionComponent.Benefits, 70m, 7m, 0m),
                new(ContributionComponent.Disability, 10m, 1m, 0m)
            ],
            employee: [new(ContributionComponent.Benefits, 70m, 7m, 0m)]);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 72"));
    }

    [Fact]
    public void Rejects_missing_salary_for_routine_salaried_deposit_error_75()
    {
        var product = Product(0m,
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)],
            [new(ContributionComponent.Benefits, 60m, 6m, 0m)]);
        var errors = ApiInputValidation.Products([product], Limits, false);
        Assert.Contains(errors, x => x.Contains("קוד שגיאה 75"));
    }

    private static IReadOnlyList<string> Validate(
        IReadOnlyCollection<ManualContributionInput> employer,
        IReadOnlyCollection<ManualContributionInput> employee)
        => ApiInputValidation.Products([Product(1000m, employer, employee)], Limits, false);

    private static ManualProductInput Product(decimal salary,
        IReadOnlyCollection<ManualContributionInput> employer,
        IReadOnlyCollection<ManualContributionInput> employee)
        => new(PensionProductType.PensionFund, "P1", new DateOnly(2026, 9, 1), salary,
            "1", "1", false, null, 3, "fund", "111", "Fund", "Company", "",
            SalaryAllocationType.Fixed, salary, 0, employer, employee);
}
