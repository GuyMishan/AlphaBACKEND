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
        DateOnly? birthDate = null, PersonGender? gender = null, string? email = null, string? mobile = null)
    {
        OrganizationId = organizationId;
        NationalId = Require(nationalId, nameof(nationalId));
        FirstName = Require(firstName, nameof(firstName));
        LastName = Require(lastName, nameof(lastName));
        SetInterfaceDetails(birthDate, gender, email, mobile);
    }

    public Guid OrganizationId { get; private set; }
    public string NationalId { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public DateOnly? BirthDate { get; private set; }
    public PersonGender? Gender { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Mobile { get; private set; } = string.Empty;

    public void Update(string nationalId, string firstName, string lastName)
    {
        NationalId = Require(nationalId, nameof(nationalId));
        FirstName = Require(firstName, nameof(firstName));
        LastName = Require(lastName, nameof(lastName));
        Touch();
    }

    public void UpdateInterfaceDetails(DateOnly? birthDate, PersonGender? gender, string? email, string? mobile)
    {
        SetInterfaceDetails(birthDate, gender, email, mobile);
        Touch();
    }

    private void SetInterfaceDetails(DateOnly? birthDate, PersonGender? gender, string? email, string? mobile)
    {
        BirthDate = birthDate;
        Gender = gender;
        Email = email?.Trim() ?? string.Empty;
        Mobile = Digits(mobile);
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
