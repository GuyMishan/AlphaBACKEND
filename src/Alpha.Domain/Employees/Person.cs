using Alpha.Domain.Common;

namespace Alpha.Domain.Employees;

public enum PersonGender
{
    Male = 1,
    Female = 2
}

public sealed class Person : Entity
{
    private Person() { }

    public Person(Guid organizationId, string nationalId, string firstName, string lastName,
        DateOnly? birthDate = null, PersonGender? gender = null, string? email = null, string? mobile = null,
        string? city = null, string? street = null, string? houseNumber = null, string? apartment = null,
        string? postalCode = null, string? postOfficeBox = null)
    {
        OrganizationId = organizationId;
        NationalId = Require(nationalId, nameof(nationalId));
        FirstName = Require(firstName, nameof(firstName));
        LastName = Require(lastName, nameof(lastName));
        SetInterfaceDetails(birthDate, gender, email, mobile, city, street, houseNumber, apartment, postalCode, postOfficeBox);
    }

    public Guid OrganizationId { get; private set; }
    public string NationalId { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public DateOnly? BirthDate { get; private set; }
    public PersonGender? Gender { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Mobile { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty;
    public string HouseNumber { get; private set; } = string.Empty;
    public string Apartment { get; private set; } = string.Empty;
    public string PostalCode { get; private set; } = string.Empty;
    public string PostOfficeBox { get; private set; } = string.Empty;

    public void Update(string nationalId, string firstName, string lastName)
    {
        NationalId = Require(nationalId, nameof(nationalId));
        FirstName = Require(firstName, nameof(firstName));
        LastName = Require(lastName, nameof(lastName));
        Touch();
    }

    public void UpdateInterfaceDetails(DateOnly? birthDate, PersonGender? gender, string? email, string? mobile,
        string? city = null, string? street = null, string? houseNumber = null, string? apartment = null,
        string? postalCode = null, string? postOfficeBox = null)
    {
        SetInterfaceDetails(birthDate, gender, email, mobile, city, street, houseNumber, apartment, postalCode, postOfficeBox);
        Touch();
    }

    private void SetInterfaceDetails(DateOnly? birthDate, PersonGender? gender, string? email, string? mobile,
        string? city, string? street, string? houseNumber, string? apartment, string? postalCode, string? postOfficeBox)
    {
        BirthDate = birthDate;
        Gender = gender;
        Email = email?.Trim() ?? string.Empty;
        Mobile = Digits(mobile);
        City = city?.Trim() ?? string.Empty;
        Street = street?.Trim() ?? string.Empty;
        HouseNumber = houseNumber?.Trim() ?? string.Empty;
        Apartment = apartment?.Trim() ?? string.Empty;
        PostalCode = Digits(postalCode);
        PostOfficeBox = postOfficeBox?.Trim() ?? string.Empty;
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
