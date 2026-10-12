using Alpha.Domain.Employers;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

public sealed class EmployerPensionDocumentOwnershipTests
{
    [Fact]
    public void Employer_affidavit_is_employer_wide()
    {
        var orgId = Guid.NewGuid();
        var employerId = Guid.NewGuid();
        var document = new EmployerDocument(orgId, employerId, null, null, 3,
            "affidavit.pdf", new byte[] { 1 }, 1, "abc");

        Assert.Equal(orgId, document.OrganizationId);
        Assert.Equal(employerId, document.EmployerId);
        Assert.Null(document.EmploymentId);
        Assert.Null(document.EmployeePensionProductId);
        Assert.Equal(3, document.DocumentTypeCode);

        Assert.Throws<ArgumentException>(() => new EmployerDocument(orgId, employerId,
            Guid.NewGuid(), Guid.NewGuid(), 3, "bad.pdf", new byte[] { 1 }, 1, "abc"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Employee_document_requires_the_employee_and_a_specific_pension_product(int type)
    {
        var orgId = Guid.NewGuid();
        var employerId = Guid.NewGuid();
        var employmentId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new EmployerDocument(orgId, employerId,
            employmentId, null, type, "unassigned.pdf", new byte[] { 1 }, 1, "abc"));

        var document = new EmployerDocument(orgId, employerId, employmentId, productId,
            type, "document.pdf", new byte[] { 1, 2 }, 2, "abc");
        Assert.Equal(employmentId, document.EmploymentId);
        Assert.Equal(productId, document.EmployeePensionProductId);
        Assert.Throws<InvalidOperationException>(() => document.AssignProduct(Guid.NewGuid()));
    }

    [Fact]
    public void Existing_pension_product_id_is_preserved_when_the_mix_is_rebuilt()
    {
        var originalId = Guid.NewGuid();
        var product = new EmployeePensionProduct(Guid.NewGuid(), PensionProductType.PensionFund,
            "123456", 1000m, "1", "1", false, null, true, new DateOnly(2026, 1, 1),
            null, "", "", "fund-external", "fund-code", "fund-name", "company",
            SalaryAllocationType.Fixed, 1000m, 0, 3, null, originalId);

        Assert.Equal(originalId, product.Id);
        Assert.Throws<ArgumentException>(() => new EmployeePensionProduct(Guid.NewGuid(),
            PensionProductType.PensionFund, "123456", 1000m, "1", "1", false, null, true,
            new DateOnly(2026, 1, 1), null, "", "", "fund-external", "fund-code",
            "fund-name", "company", SalaryAllocationType.Fixed, 1000m, 0, 3, null, Guid.Empty));
    }
}
