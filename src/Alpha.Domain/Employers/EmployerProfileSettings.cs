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

public enum EmployerPensionPaymentMode
{
    EmployerDirect = 1,
    InheritOrganization = 2
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
    public bool BillingModeOverridden { get; private set; }
    public EmployerBillingStatus BillingStatus { get; private set; } = EmployerBillingStatus.NotConfigured;
    public EmployerPensionPaymentMode PensionPaymentMode { get; private set; } = EmployerPensionPaymentMode.InheritOrganization;
    public bool PensionPaymentModeOverridden { get; private set; }

    public int? DefaultSalaryPaymentDay { get; private set; }
    public int DefaultDepositorTypeCode { get; private set; } = 1;
    public int DefaultEmployerIdentifierTypeCode { get; private set; } = 1;
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
        BillingModeOverridden = true;
        if (status.HasValue) BillingStatus = status.Value;
        Touch();
    }

    public void ApplyDefaultBillingMode(EmployerBillingMode mode)
    {
        if (BillingModeOverridden) return;
        BillingMode = mode;
        Touch();
    }

    public void UpdatePensionPaymentMode(EmployerPensionPaymentMode mode)
    {
        PensionPaymentMode = mode;
        PensionPaymentModeOverridden = true;
        Touch();
    }

    public void ApplyDefaultPensionPaymentMode(EmployerPensionPaymentMode mode)
    {
        if (PensionPaymentModeOverridden) return;
        PensionPaymentMode = mode;
        Touch();
    }

    public void UpdateReporting(int? defaultSalaryPaymentDay, int? defaultPaymentMethodCode,
        int? defaultEmployerAccountType, int? defaultReceiverAccountType, string? notes) =>
        UpdateReporting(defaultSalaryPaymentDay, DefaultDepositorTypeCode, DefaultEmployerIdentifierTypeCode,
            defaultPaymentMethodCode, defaultEmployerAccountType, defaultReceiverAccountType, notes);

    public void UpdateReporting(int? defaultSalaryPaymentDay, int defaultDepositorTypeCode, int defaultEmployerIdentifierTypeCode,
        int? defaultPaymentMethodCode, int? defaultEmployerAccountType, int? defaultReceiverAccountType, string? notes)
    {
        if (defaultSalaryPaymentDay is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(defaultSalaryPaymentDay), "Salary payment day must be between 1 and 31.");
        if (defaultDepositorTypeCode is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(defaultDepositorTypeCode), "Depositor type must be 1, 2 or 3.");
        if (defaultEmployerIdentifierTypeCode is not (1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 10 or 11 or 12 or 13))
            throw new ArgumentOutOfRangeException(nameof(defaultEmployerIdentifierTypeCode), "Employer identifier type is not valid for Employer Interface 006.");
        if (defaultPaymentMethodCode < 0 || defaultEmployerAccountType < 0 || defaultReceiverAccountType < 0)
            throw new ArgumentOutOfRangeException(nameof(defaultPaymentMethodCode), "Reporting codes cannot be negative.");

        DefaultSalaryPaymentDay = defaultSalaryPaymentDay;
        DefaultDepositorTypeCode = defaultDepositorTypeCode;
        DefaultEmployerIdentifierTypeCode = defaultEmployerIdentifierTypeCode;
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
