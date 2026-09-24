using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Alpha.Api.Services;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006XmlBuilderTests
{
    [Fact]
    public void Current_report_matches_official_006_xsd()
    {
        var fixture = CreateFixture(false);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_report_matches_official_006_xsd()
    {
        var fixture = CreateFixture(true);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, true);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document!, "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_emits_small_employer_depositor_type_3()
    {
        var fixture = CreateFixture(false);
        var context = fixture.Context with { DepositorTypeCode = 3 };

        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.All(result.Document!.Descendants("SUG-MAFKID"), x => Assert.Equal("3", x.Value));
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_accepts_section14_code_5()
    {
        var fixture = CreateFixture(false, section14Code: 5);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_emits_configured_depositor_type()
    {
        var fixture = CreateFixture(false);
        var context = fixture.Context with { DepositorTypeCode = 3 };
        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.All(result.Document!.Descendants("SUG-MAFKID"), x => Assert.Equal("3", x.Value));
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_uses_official_mobile_fallback_when_employer_has_no_mobile()
    {
        var fixture = CreateFixture(false, employerMobile: "");
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.Contains(result.Document!.Descendants("MISPAR-CELLULARI-ISH-KESHER-MAASIK"), x => x.Value == "0500000000");
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_rejects_invalid_employer_mobile_format()
    {
        var fixture = CreateFixture(false, employerMobile: "031234567");
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("contact mobile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Current_bank_transfer_normalizes_employer_and_receiver_accounts_for_xsd()
    {
        var fixture = CreateFixture(false);
        var payment = fixture.Context.Payments[0];
        payment.Update("Test Fund", "10 - 8 - 999", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "7", "12345", "");

        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);

        var transfer = Assert.Single(result.Document!.Descendants("PirteiHaavaratKsafim"));
        Assert.Equal("007", transfer.Element("MISPAR-SNIF-MAASIK")?.Value);
        Assert.Equal("00000000000000012345", transfer.Element("MISPAR-CHESHBON-MAASIK")?.Value);
        Assert.Equal("10", transfer.Element("MISPAR-BANK-KOLET")?.Value);
        Assert.Equal("008", transfer.Element("MISPAR-SNIF-KOLET")?.Value);
        Assert.Equal("00000000000000000999", transfer.Element("MISPAR-CHESHBON-KOLET")?.Value);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_non_bank_payment_zeroes_employer_branch_and_account()
    {
        var fixture = CreateFixture(false, paymentMethodCode: 3);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);

        var transfer = Assert.Single(result.Document!.Descendants("PirteiHaavaratKsafim"));
        Assert.Equal("000", transfer.Element("MISPAR-SNIF-MAASIK")?.Value);
        Assert.Equal(new string('0', 20), transfer.Element("MISPAR-CHESHBON-MAASIK")?.Value);
        Assert.Equal("true", transfer.Element("MISPAR-BANK-KOLET")?.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))?.Value);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_rejects_operation_payment_combination_not_allowed_by_clearinghouse_matrix()
    {
        var fixture = CreateFixture(false, operationCode: 1, paymentMethodCode: 4);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Contains(workbookIssues, x => x.Contains("payment method 4 is not allowed for operation 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Current_report_emits_one_employee_block_for_multiple_products_in_same_fund()
    {
        var fixture = CreateFixture(false);
        var secondProduct = new ManualReportProduct(fixture.Context.Employees[0].Id, PensionProductType.PensionFund, "456",
            new DateOnly(2026, 9, 1), 500m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "Test Fund");
        var secondContribution = new ManualContribution(secondProduct.Id, ContributionParty.Employee, ContributionComponent.Benefits,
            50m, 10m, 0m);
        var secondPayment = new ManualReportPayment(secondProduct.Id);
        secondPayment.Update("Test Fund", "10 - 123 - 987654", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "123",
            "123456", "");
        var secondMetadata = new EmployerInterfaceReportProductData(secondProduct.Id);
        secondMetadata.Update(1, 1, 1, new DateOnly(2026, 9, 1), null, null, 2, null, 1, 1, 1);

        var context = fixture.Context with
        {
            Products = [.. fixture.Context.Products, secondProduct],
            Contributions = [.. fixture.Context.Contributions, secondContribution],
            Payments = [.. fixture.Context.Payments, secondPayment],
            ProductMetadata = [.. fixture.Context.ProductMetadata, secondMetadata]
        };

        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.Single(result.Document!.Descendants("PirteiOved"));
        Assert.Equal(2, result.Document.Descendants("ChodeshMaskoretVestatusOved").Count());
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_rejects_inconsistent_payment_details_inside_one_transfer()
    {
        var fixture = CreateFixture(false);
        var secondProduct = new ManualReportProduct(fixture.Context.Employees[0].Id, PensionProductType.PensionFund, "456",
            new DateOnly(2026, 9, 1), 500m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "Test Fund");
        var secondContribution = new ManualContribution(secondProduct.Id, ContributionParty.Employee, ContributionComponent.Benefits,
            50m, 10m, 0m);
        var secondPayment = new ManualReportPayment(secondProduct.Id);
        secondPayment.Update("Test Fund", "10 - 123 - 987654", "", new DateOnly(2026, 9, 17), "DIFFERENT-REF", "Test Bank", "10", "123",
            "123456", "");
        var secondMetadata = new EmployerInterfaceReportProductData(secondProduct.Id);
        secondMetadata.Update(1, 1, 1, new DateOnly(2026, 9, 1), null, null, 2, null, 1, 1, 1);

        var context = fixture.Context with
        {
            Products = [.. fixture.Context.Products, secondProduct],
            Contributions = [.. fixture.Context.Contributions, secondContribution],
            Payments = [.. fixture.Context.Payments, secondPayment],
            ProductMetadata = [.. fixture.Context.ProductMetadata, secondMetadata]
        };

        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, context, false);
        Assert.Contains(workbookIssues, x => x.Contains("identical transfer/payment details", StringComparison.Ordinal));
    }

    [Fact]
    public void Negative_operation_6_emits_nil_payment_method_and_matches_official_006_xsd()
    {
        var fixture = CreateFixture(true, operationCode: 6, paymentMethodCode: null);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, true);
        Assert.Empty(workbookIssues);
        var method = Assert.Single(result.Document!.Descendants("KOD-EMTZAI-TASHLUM"));
        Assert.Equal("true", method.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))?.Value);
        AssertValid(result.Document!, "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_operation_6_rejects_payment_method_value()
    {
        var fixture = CreateFixture(true, operationCode: 6, paymentMethodCode: 3);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("must not carry PaymentMethodCode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(1, 5)]
    [InlineData(1, 6)]
    [InlineData(1, 7)]
    [InlineData(1, 9)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    [InlineData(3, 5)]
    [InlineData(3, 6)]
    [InlineData(3, 7)]
    [InlineData(3, 9)]
    [InlineData(7, 1)]
    public void Current_report_accepts_every_official_operation_payment_combination(int operationCode, int paymentMethodCode)
    {
        var fixture = CreateFixture(false, operationCode: operationCode, paymentMethodCode: paymentMethodCode);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(5, 3)]
    [InlineData(5, 6)]
    [InlineData(5, 7)]
    [InlineData(5, 9)]
    public void Negative_report_accepts_every_official_operation_payment_combination(int operationCode, int paymentMethodCode)
    {
        var fixture = CreateFixture(true, operationCode: operationCode, paymentMethodCode: paymentMethodCode);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, true);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document!, "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void Current_non_deposit_corrections_report_zero_deposit_totals(int operationCode)
    {
        var fixture = CreateFixture(false, operationCode: operationCode, paymentMethodCode: 1);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var transfer = Assert.Single(result.Document!.Descendants("PirteiHaavaratKsafim"));
        Assert.Equal("0.00", transfer.Element("SCHUM-HAFKADA-KOLEL")?.Value);
        Assert.Equal("0.00", transfer.Element("SACH-HAFKADA-KUPA-H-P")?.Value);
        Assert.Equal("0.00", result.Document.Root?.Element("ReshumatSgira")?.Element("SACH-HAFKADOT-BAKOVETZ")?.Value);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, fixture.Context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_bank_transfer_with_zero_amount_does_not_require_receiver_account()
    {
        var fixture = CreateFixture(false, paymentMethodCode: 1);
        fixture.Context.Contributions[0].Update(0m, 0m, 0m);
        fixture.Context.Payments[0].Update("Test Fund", "", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "", "", "");

        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Empty(workbookIssues);
        var transfer = Assert.Single(result.Document!.Descendants("PirteiHaavaratKsafim"));
        Assert.Equal("true", transfer.Element("MISPAR-BANK-KOLET")?.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))?.Value);
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_masav_requires_receiver_account_even_when_amount_is_zero()
    {
        var fixture = CreateFixture(false, paymentMethodCode: 7);
        fixture.Context.Contributions[0].Update(0m, 0m, 0m);
        fixture.Context.Payments[0].Update("Test Fund", "", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "", "", "");

        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("requires receiving bank, branch and account details", StringComparison.Ordinal));
    }

    [Fact]
    public void Current_report_rejects_unmapped_other_product_type()
    {
        var fixture = CreateFixture(false);
        var product = new ManualReportProduct(fixture.Context.Employees[0].Id, PensionProductType.Other, "123",
            new DateOnly(2026, 9, 1), 1000m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "Other");
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee, ContributionComponent.Benefits, 100m, 10m, 0m);
        var payment = new ManualReportPayment(product.Id);
        payment.Update("Other", "10 - 123 - 987654", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "123", "123456", "");
        var metadata = new EmployerInterfaceReportProductData(product.Id);
        metadata.Update(1, 1, 1, new DateOnly(2026, 9, 1), null, null, 2, null, 1, 1, 1);

        var context = fixture.Context with { Products = [product], Contributions = [contribution], Payments = [payment], ProductMetadata = [metadata] };
        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);

        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("SUG-KUPA only allows codes 1-4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ContributionParty.Employer, ContributionComponent.Severance, "1")]
    [InlineData(ContributionParty.Employee, ContributionComponent.Severance, "2")]
    [InlineData(ContributionParty.Employer, ContributionComponent.Benefits, "3")]
    [InlineData(ContributionParty.Employee, ContributionComponent.Benefits, "4")]
    [InlineData(ContributionParty.Employee, ContributionComponent.Disability, "5")]
    [InlineData(ContributionParty.Employer, ContributionComponent.Disability, "6")]
    [InlineData(ContributionParty.Employee, ContributionComponent.Other, "7")]
    [InlineData(ContributionParty.Employer, ContributionComponent.Other, "8")]
    public void Current_report_maps_all_official_contribution_types(ContributionParty party, ContributionComponent component, string expectedCode)
    {
        var fixture = CreateFixture(false, productType: int.Parse(expectedCode) >= 5
            ? PensionProductType.ManagersInsurance
            : PensionProductType.PensionFund);
        var contribution = new ManualContribution(fixture.Product.Id, party, component, 100m, 10m, 0m);
        var context = fixture.Context with { Contributions = [contribution] };

        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.Contains(result.Document!.Descendants("SUG-HAFRASHA"), x => x.Value == expectedCode);
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_rejects_transfer_reference_longer_than_50_characters()
    {
        var fixture = CreateFixture(false);
        fixture.Context.Payments[0].Update("Test Fund", "10 - 123 - 987654", "", new DateOnly(2026, 9, 16), new string('A', 51), "Test Bank", "10", "123", "123456", "");

        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);

        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("cannot exceed 50", StringComparison.Ordinal));
    }

    [Fact]
    public void Current_report_allows_missing_policy_number_and_emits_nil()
    {
        var fixture = CreateFixture(false, policyNumber: "");
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var policy = Assert.Single(result.Document!.Descendants("MISPAR-POLISA-O-HESHBON"));
        Assert.Equal("true", policy.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))?.Value);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, fixture.Context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Current_report_emits_official_default_fund_attachment_type_5()
    {
        var fixture = CreateFixture(false, operationCode: 1);
        var request = new ManualReportAttachment(Guid.NewGuid(), fixture.Product.Id, 5, "default-fund-request.pdf",
            "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nrequest"));
        var context = fixture.Context with { Attachments = [request] };

        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.Equal("5", Assert.Single(result.Document!.Descendants("SUG-MISMACH")).Value);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, context, false);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_operation_5_emits_attachment_references_and_employee_flags()
    {
        var fixture = CreateFixture(true, operationCode: 5);
        var employeeApproval = new ManualReportAttachment(Guid.NewGuid(), fixture.Product.Id, 4, "employee-approval.pdf",
            "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nemployee"));
        var collectiveAgreement = new ManualReportAttachment(Guid.NewGuid(), fixture.Product.Id, 6, "collective.pdf",
            "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\ncollective"));
        var context = fixture.Context with
        {
            Attachments = [.. fixture.Context.Attachments, employeeApproval, collectiveAgreement],
            AnnualEmployerAffidavitSatisfied = true
        };

        var result = EmployerInterface006XmlBuilder.BuildNegative(context);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var documentTypes = result.Document!.Descendants("SUG-MISMACH").Select(x => x.Value).OrderBy(x => x).ToArray();
        Assert.Equal(["3", "4", "6"], documentTypes);
        Assert.Contains(result.Document.Descendants("HASHAVA-KIBUTZI"), x => x.Value == "1");
        Assert.Contains(result.Document.Descendants("HATZHARAT-OVED"), x => x.Value == "1");
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document, context, true);
        Assert.Empty(workbookIssues);
        AssertValid(result.Document, "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_operation_5_requires_annual_employer_affidavit()
    {
        var fixture = CreateFixture(true, operationCode: 5);
        var employeeApproval = new ManualReportAttachment(Guid.NewGuid(), fixture.Product.Id, 4, "employee-approval.pdf",
            "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nemployee"));
        var context = fixture.Context with
        {
            Attachments = [employeeApproval],
            AnnualEmployerAffidavitSatisfied = false
        };

        var result = EmployerInterface006XmlBuilder.BuildNegative(context);

        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("annual employer affidavit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Current_operation_3_uses_actual_additional_cash_amount_not_contribution_total()
    {
        var fixture = CreateFixture(false, operationCode: 3, paymentMethodCode: 1);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        Assert.Equal("50.00", Assert.Single(result.Document!.Descendants("SACH-HAFKADA-KUPA-H-P")).Value);
        Assert.Equal("50.00", Assert.Single(result.Document.Descendants("SCHUM-HAFKADA-KOLEL")).Value);
        Assert.Equal("50.00", result.Document.Root?.Element("ReshumatSgira")?.Element("SACH-HAFKADOT-BAKOVETZ")?.Value);
    }

    [Fact]
    public void Current_masav_payment_emits_configured_masav_sender_code()
    {
        var fixture = CreateFixture(false, operationCode: 1, paymentMethodCode: 7);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.Equal("12345678", Assert.Single(result.Document!.Descendants("KOD-MASAV")).Value);
    }

    [Fact]
    public void Current_no_money_correction_uses_file_date_and_reference_000()
    {
        var fixture = CreateFixture(false, operationCode: 2, paymentMethodCode: 1);
        var preparedAt = new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.FromHours(3));
        var context = fixture.Context with { PreparedAt = preparedAt };
        var result = EmployerInterface006XmlBuilder.BuildCurrent(context);
        Assert.Empty(result.Issues);
        Assert.Equal("2026-09-24", Assert.Single(result.Document!.Descendants("TAARICH-ERECH-HAFKADA-LEKUPA")).Value);
        Assert.Equal("000", Assert.Single(result.Document.Descendants("MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM")).Value);
    }

    [Fact]
    public void Negative_report_does_not_emit_non_relevant_birth_date_or_contribution_percentage()
    {
        var fixture = CreateFixture(true, operationCode: 5);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Document!.Descendants("TAARICH-LEIDA"));
        Assert.Empty(result.Document.Descendants("SHIUR-HAFRASHA"));
    }

    [Fact]
    public void Negative_report_rejects_current_operation_code()
    {
        var fixture = CreateFixture(true, operationCode: 1);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("OperationCode 5 or 6", StringComparison.Ordinal));
    }

    [Fact]
    public void Corrective_report_requires_previous_reference_or_official_exception()
    {
        var fixture = CreateFixture(false, operationCode: 2, previousExceptionCode: null);
        var result = EmployerInterface006XmlBuilder.BuildCurrent(fixture.Context);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, false);
        Assert.Contains(workbookIssues, x => x.Contains("must reference the original report", StringComparison.Ordinal));
    }

    private static (EmployerInterface006XmlBuilder.BuildContext Context, ManualReportProduct Product) CreateFixture(
        bool negative, int? operationCode = null, int? previousExceptionCode = 1, int? section14Code = null,
        string employerMobile = "0501234567", int? paymentMethodCode = 1, string policyNumber = "123",
        PensionProductType productType = PensionProductType.PensionFund)
    {
        var organizationId = Guid.NewGuid();
        var employer = new Employer(organizationId, "Test Employer", "123456789", "987654321",
            "Guy", "Mishan", "031234567", "employer@example.com", employerMobile);
        var person = new Person(organizationId, "123456789", "Test", "Employee",
            new DateOnly(1990, 1, 1), PersonGender.Male, "employee@example.com", "0507654321",
            "Tel Aviv", "Herzl", "10", "4", "6100001", "123");
        var employment = new Employment(organizationId, employer.Id, person.Id, new DateOnly(2020, 1, 1), "E1", 1000m);
        var reportId = Guid.NewGuid();
        var reportEmployee = new ManualReportEmployee(reportId, organizationId, employer.Id, employment.Id, person.Id,
            person.NationalId, person.FirstName, person.LastName, employment.EmployeeNumber, employment.MonthlySalary);
        var product = new ManualReportProduct(reportEmployee.Id, productType, policyNumber,
            new DateOnly(2026, 9, 1), 1000m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "Test Fund", section14Code: section14Code);
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee, ContributionComponent.Benefits,
            100m, 10m, 0m);
        var payment = new ManualReportPayment(product.Id);
        var resolvedOperation = operationCode ?? (negative ? 5 : 1);
        decimal? actualDepositAmount = !negative && resolvedOperation == 3 ? 50m : null;
        var masavSenderCode = paymentMethodCode == 7 ? "12345678" : null;
        payment.Update("Test Fund", "10 - 123 - 987654", "", new DateOnly(2026, 9, 16), null, "REF-1",
            "Test Bank", "10", "123", "123456", "", actualDepositAmount, masavSenderCode);
        var metadata = new EmployerInterfaceReportProductData(product.Id);
        metadata.Update(operationCode ?? (negative ? 5 : 1), 1, 1, new DateOnly(2026, 9, 1), null, null, 2,
            negative ? 1 : null, paymentMethodCode, 1, 1,
            previousReferenceExceptionCode: (negative || (operationCode is 2 or 3 or 7)) ? previousExceptionCode : null);
        var options = new EmployerInterface006Options
        {
            EnvironmentCode = 2,
            SenderCode = 3,
            SenderIdentifierType = 1,
            SenderIdentifier = "123456789",
            SenderName = "Test Sender",
            SenderContactFirstName = "Sender",
            SenderContactLastName = "Contact",
            SenderContactPhone = "031234567",
            SenderContactEmail = "sender@example.com",
            SenderContactMobile = "0501234567",
            RecipientCode = 6,
            RecipientIdentifierType = 1,
            RecipientIdentifier = "123456789"
        };

        var attachments = new List<ManualReportAttachment>();
        var annualAffidavitSatisfied = false;
        if (negative && (operationCode ?? 5) == 5)
        {
            attachments.Add(new ManualReportAttachment(reportId, null, 3, "employer-affidavit.pdf", "application/pdf",
                Encoding.ASCII.GetBytes("%PDF-1.4\nfixture")));
            annualAffidavitSatisfied = true;
        }

        var context = new EmployerInterface006XmlBuilder.BuildContext(
            employer,
            [reportEmployee],
            new Dictionary<Guid, Person> { [person.Id] = person },
            new Dictionary<Guid, Employment> { [employment.Id] = employment },
            [product], [contribution], [payment], [metadata], options, 1, 1, attachments, annualAffidavitSatisfied);
        return (context, product);
    }

    private static void AssertValid(XDocument document, string xsdFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Specifications", "EmployerInterface", "006", xsdFileName);
        Assert.True(File.Exists(path), $"Missing official XSD test fixture: {path}");
        var schemas = new XmlSchemaSet { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            schemas.Add(null, reader);
        schemas.Compile();
        var issues = new List<string>();
        document.Validate(schemas, (_, e) => issues.Add(e.Message), true);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues));
    }
}
