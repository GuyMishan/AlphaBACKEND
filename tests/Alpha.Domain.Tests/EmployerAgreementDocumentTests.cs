using Alpha.Domain.Employers;
using Xunit;

namespace Alpha.Domain.Tests;

public class EmployerAgreementDocumentTests
{
    [Fact]
    public void Employer_agreement_is_employer_wide_and_requires_no_employee_product()
    {
        var document = new EmployerDocument(Guid.NewGuid(), Guid.NewGuid(), null, null, 7,
            "agreement.pdf", new byte[] { 1, 2, 3 }, 3, new string('a', 64));
        Assert.Equal(7, document.DocumentTypeCode);
        Assert.Null(document.EmploymentId);
        Assert.Null(document.EmployeePensionProductId);
    }

    [Fact]
    public void Employer_agreement_cannot_be_assigned_to_an_employee()
    {
        Assert.Throws<ArgumentException>(() => new EmployerDocument(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            "agreement.pdf", new byte[] { 1 }, 1, new string('a', 64)));
    }
}
