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
        AssertValid(result.Document!, "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_report_matches_official_006_xsd()
    {
        var fixture = CreateFixture(true);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.Document);
        AssertValid(result.Document!, "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml");
    }

    [Fact]
    public void Negative_report_rejects_current_operation_code()
    {
        var fixture = CreateFixture(true, operationCode: 1);
        var result = EmployerInterface006XmlBuilder.BuildNegative(fixture.Context);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, x => x.Contains("OperationCode 5 or 6", StringComparison.Ordinal));
    }

    private static (EmployerInterface006XmlBuilder.BuildContext Context, ManualReportProduct Product) CreateFixture(
        bool negative, int? operationCode = null)
    {
        var organizationId = Guid.NewGuid();
        var employer = new Employer(organizationId, "Test Employer", "123456789", "987654321",
            "Guy", "Mishan", "031234567", "employer@example.com", "0501234567");
        var person = new Person(organizationId, "123456789", "Test", "Employee",
            new DateOnly(1990, 1, 1), PersonGender.Male, "employee@example.com", "0507654321");
        var employment = new Employment(organizationId, employer.Id, person.Id, new DateOnly(2020, 1, 1), "E1", 1000m);
        var reportId = Guid.NewGuid();
        var reportEmployee = new ManualReportEmployee(reportId, organizationId, employer.Id, employment.Id, person.Id,
            person.NationalId, person.FirstName, person.LastName, employment.EmployeeNumber, employment.MonthlySalary);
        var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund, "123",
            new DateOnly(2026, 9, 1), 1000m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "Test Fund");
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee, ContributionComponent.Benefits,
            100m, 10m, 0m);
        var payment = new ManualReportPayment(product.Id);
        payment.Update("Test Fund", "", "", new DateOnly(2026, 9, 16), "REF-1", "Test Bank", "10", "123",
            "12345678901234567890", "");
        var metadata = new EmployerInterfaceReportProductData(product.Id);
        metadata.Update(operationCode ?? (negative ? 5 : 1), 1, 1, new DateOnly(2026, 9, 1), null, null, 2,
            negative ? 1 : null, 1, 1, 1);
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
