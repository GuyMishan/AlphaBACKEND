using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public enum EmployerStatus { Onboarding = 1, Active = 2, Suspended = 3, Closed = 4 }

public sealed class Employer : Entity
{
    private Employer() { }

    public Employer(Guid organizationId, string legalName, string registrationNumber, string withholdingFileNumber)
    {
        OrganizationId = organizationId;
        LegalName = Require(legalName, nameof(legalName));
        RegistrationNumber = Require(registrationNumber, nameof(registrationNumber));
        WithholdingFileNumber = Require(withholdingFileNumber, nameof(withholdingFileNumber));
    }

    public Guid OrganizationId { get; private set; }
    public string LegalName { get; private set; } = string.Empty;
    public string RegistrationNumber { get; private set; } = string.Empty;
    public string WithholdingFileNumber { get; private set; } = string.Empty;
    public EmployerStatus Status { get; private set; } = EmployerStatus.Onboarding;

    public void Update(string legalName, string registrationNumber, string withholdingFileNumber)
    {
        LegalName = Require(legalName, nameof(legalName));
        RegistrationNumber = Require(registrationNumber, nameof(registrationNumber));
        WithholdingFileNumber = Require(withholdingFileNumber, nameof(withholdingFileNumber));
        Touch();
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
