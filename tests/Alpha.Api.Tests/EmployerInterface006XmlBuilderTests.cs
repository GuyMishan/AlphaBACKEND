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
    public void Negative_operation_6_uses_payment_method_1_and_matches_official_006_xsd()
    {
        var fixture = CreateFixture(true, operationCode: 6, paymentMethodCode: 1);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, true);
        Assert.Empty(workbookIssues);
        Assert.Contains(result.Document!.Descendants("KOD-EMTZAI-TASHLUM"), x => x.Value == "1");
        AssertValid(result.Document!, "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_operation_6_rejects_non_matrix_payment_method()
    {
        var fixture = CreateFixture(true, operationCode: 6, paymentMethodCode: 3);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.NotNull(result.Document);
        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(result.Document!, fixture.Context, true);
        Assert.Contains(workbookIssues, x => x.Contains("payment method 3 is not allowed for operation 6", StringComparison.Ordinal));
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
        string employerMobile = "0501234567", int paymentMethodCode = 1)
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
        var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund, "123",
            new DateOnly(2026, 9, 1), 1000m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "Test Fund", section14Code: section14Code);
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee, ContributionComponent.Benefits,
            100m, 10m, 0m);
        var payment = new ManualReportPayment(product.Id);
        payment.Update("Test Fund", "10 - 123 - 987654", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "123",
            "123456", "");
        var metadata = new EmployerInterfaceReportProductData(product.Id);
        metadata.Update(operationCode ?? (negative ? 5 : 1), 1, 1, new DateOnly(2026, 9, 1), null, null, 2,
            negative ? 1 : null, paymentMethodCode, 1, 1,
            previousReferenceExceptionCode: (negative || (operationCode is 2 or 3 or 7)) ? previousExceptionCode : null);
        var options = new EmployerInterface006Options
        {
            EnvironmentCode = 2,
            SenderCode = 3,
            SenderIdentifierType = 1,
            RecipientCode = 6,
            RecipientIdentifierType = 1,
            RecipientIdentifier = "123456789"
        };

        var context = new EmployerInterface006XmlBuilder.BuildContext(
            employer,
            [reportEmployee],
            new Dictionary<Guid, Person> { [person.Id] = person },
            new Dictionary<Guid, Employment> { [employment.Id] = employment },
            [product], [contribution], [payment], [metadata], options);
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
