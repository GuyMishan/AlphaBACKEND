using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public enum EmployerStatus { Onboarding = 1, Active = 2, Suspended = 3, Closed = 4 }

public sealed class Employer : Entity
{
    private Employer() { }

    public Employer(Guid organizationId, string legalName, string registrationNumber, string withholdingFileNumber,
        string? contactFirstName = null, string? contactLastName = null, string? contactPhone = null,
        string? contactEmail = null, string? contactMobile = null)
    {
        OrganizationId = organizationId;
        LegalName = Require(legalName, nameof(legalName));
        RegistrationNumber = Require(registrationNumber, nameof(registrationNumber));
        WithholdingFileNumber = Require(withholdingFileNumber, nameof(withholdingFileNumber));
        SetInterfaceContact(contactFirstName, contactLastName, contactPhone, contactEmail, contactMobile);
    }

    public Guid OrganizationId { get; private set; }
    public string LegalName { get; private set; } = string.Empty;
    public string RegistrationNumber { get; private set; } = string.Empty;
    public string WithholdingFileNumber { get; private set; } = string.Empty;
    public EmployerStatus Status { get; private set; } = EmployerStatus.Onboarding;
    public string ContactFirstName { get; private set; } = string.Empty;
    public string ContactLastName { get; private set; } = string.Empty;
    public string ContactPhone { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public string ContactMobile { get; private set; } = string.Empty;

    public void Update(string legalName, string registrationNumber, string withholdingFileNumber,
        string? contactFirstName = null, string? contactLastName = null, string? contactPhone = null,
        string? contactEmail = null, string? contactMobile = null)
    {
        LegalName = Require(legalName, nameof(legalName));
        RegistrationNumber = Require(registrationNumber, nameof(registrationNumber));
        WithholdingFileNumber = Require(withholdingFileNumber, nameof(withholdingFileNumber));
        SetInterfaceContact(contactFirstName, contactLastName, contactPhone, contactEmail, contactMobile);
        Touch();
    }

    private void SetInterfaceContact(string? firstName, string? lastName, string? phone, string? email, string? mobile)
    {
        ContactFirstName = firstName?.Trim() ?? string.Empty;
        ContactLastName = lastName?.Trim() ?? string.Empty;
        ContactPhone = Digits(phone);
        ContactEmail = email?.Trim() ?? string.Empty;
        ContactMobile = Digits(mobile);
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
