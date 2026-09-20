using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public enum EmployerBillingMode
{
    EmployerDirect = 1,
    InheritOrganization = 2
}

public enum EmployerBillingStatus
{
    NotConfigured = 1,
    Active = 2,
    Suspended = 3
}

public sealed class EmployerProfileSettings : Entity
{
    private EmployerProfileSettings() { }

    public EmployerProfileSettings(Guid employerId)
    {
        if (employerId == Guid.Empty) throw new ArgumentException("Employer is required.", nameof(employerId));
        EmployerId = employerId;
    }

    public Guid EmployerId { get; private set; }

    public string City { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty;
    public string HouseNumber { get; private set; } = string.Empty;
    public string Apartment { get; private set; } = string.Empty;
    public string PostalCode { get; private set; } = string.Empty;
    public string PostOfficeBox { get; private set; } = string.Empty;

    public EmployerBillingMode BillingMode { get; private set; } = EmployerBillingMode.EmployerDirect;
    public EmployerBillingStatus BillingStatus { get; private set; } = EmployerBillingStatus.NotConfigured;

    public int? DefaultSalaryPaymentDay { get; private set; }
    public int? DefaultPaymentMethodCode { get; private set; }
    public int? DefaultEmployerAccountType { get; private set; }
    public int? DefaultReceiverAccountType { get; private set; }
    public string ReportingNotes { get; private set; } = string.Empty;

    public void UpdateAddress(string? city, string? street, string? houseNumber, string? apartment,
        string? postalCode, string? postOfficeBox)
    {
        City = Clean(city, 100);
        Street = Clean(street, 100);
        HouseNumber = Clean(houseNumber, 20);
        Apartment = Clean(apartment, 20);
        PostalCode = Digits(postalCode, 10);
        PostOfficeBox = Clean(postOfficeBox, 20);
        Touch();
    }

    public void UpdateBilling(EmployerBillingMode mode, EmployerBillingStatus? status = null)
    {
        BillingMode = mode;
        if (status.HasValue) BillingStatus = status.Value;
        Touch();
    }

    public void UpdateReporting(int? defaultSalaryPaymentDay, int? defaultPaymentMethodCode,
        int? defaultEmployerAccountType, int? defaultReceiverAccountType, string? notes)
    {
        if (defaultSalaryPaymentDay is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(defaultSalaryPaymentDay), "Salary payment day must be between 1 and 31.");
        if (defaultPaymentMethodCode < 0 || defaultEmployerAccountType < 0 || defaultReceiverAccountType < 0)
            throw new ArgumentOutOfRangeException(nameof(defaultPaymentMethodCode), "Reporting codes cannot be negative.");

        DefaultSalaryPaymentDay = defaultSalaryPaymentDay;
        DefaultPaymentMethodCode = defaultPaymentMethodCode;
        DefaultEmployerAccountType = defaultEmployerAccountType;
        DefaultReceiverAccountType = defaultReceiverAccountType;
        ReportingNotes = Clean(notes, 500);
        Touch();
    }

    private static string Clean(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private static string Digits(string? value, int max)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits[..Math.Min(digits.Length, max)];
    }
}
