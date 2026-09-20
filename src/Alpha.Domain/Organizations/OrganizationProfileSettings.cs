using Alpha.Domain.Common;

namespace Alpha.Domain.Organizations;

public enum OrganizationBillingStatus
{
    NotConfigured = 1,
    Active = 2,
    PastDue = 3,
    Suspended = 4
}

public sealed class OrganizationProfileSettings : Entity
{
    private OrganizationProfileSettings() { }

    public OrganizationProfileSettings(Guid organizationId)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("Organization is required.", nameof(organizationId));
        OrganizationId = organizationId;
    }

    public Guid OrganizationId { get; private set; }
    public string RegistrationNumber { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty;
    public string HouseNumber { get; private set; } = string.Empty;
    public string Apartment { get; private set; } = string.Empty;
    public string PostalCode { get; private set; } = string.Empty;
    public string PostOfficeBox { get; private set; } = string.Empty;
    public string ContactName { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public string ContactPhone { get; private set; } = string.Empty;

    public OrganizationBillingStatus BillingStatus { get; private set; } = OrganizationBillingStatus.NotConfigured;
    public string InvoiceName { get; private set; } = string.Empty;
    public string InvoiceRegistrationNumber { get; private set; } = string.Empty;
    public string InvoiceEmail { get; private set; } = string.Empty;
    public string BillingContactName { get; private set; } = string.Empty;
    public string BillingContactPhone { get; private set; } = string.Empty;

    public void UpdateGeneral(string? registrationNumber, string? city, string? street, string? houseNumber,
        string? apartment, string? postalCode, string? postOfficeBox, string? contactName, string? contactEmail, string? contactPhone)
    {
        RegistrationNumber = Clean(registrationNumber, 30);
        City = Clean(city, 100);
        Street = Clean(street, 100);
        HouseNumber = Clean(houseNumber, 20);
        Apartment = Clean(apartment, 20);
        PostalCode = Digits(postalCode, 10);
        PostOfficeBox = Clean(postOfficeBox, 20);
        ContactName = Clean(contactName, 150);
        ContactEmail = Clean(contactEmail, 320);
        ContactPhone = Digits(contactPhone, 20);
        Touch();
    }

    public void UpdateBilling(string? invoiceName, string? invoiceRegistrationNumber, string? invoiceEmail,
        string? billingContactName, string? billingContactPhone, OrganizationBillingStatus? status = null)
    {
        InvoiceName = Clean(invoiceName, 200);
        InvoiceRegistrationNumber = Clean(invoiceRegistrationNumber, 30);
        InvoiceEmail = Clean(invoiceEmail, 320);
        BillingContactName = Clean(billingContactName, 150);
        BillingContactPhone = Digits(billingContactPhone, 20);
        if (status.HasValue) BillingStatus = status.Value;
        Touch();
    }

    private static string Clean(string? value, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed[..Math.Min(trimmed.Length, max)];
    }

    private static string Digits(string? value, int max)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits[..Math.Min(digits.Length, max)];
    }
}
