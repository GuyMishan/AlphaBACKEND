using Alpha.Domain.Common;

namespace Alpha.Domain.Employees;

public sealed class Person : Entity
{
    private Person() { }

    public Person(Guid organizationId, string nationalId, string firstName, string lastName)
    {
        OrganizationId = organizationId;
        NationalId = Require(nationalId, nameof(nationalId));
        FirstName = Require(firstName, nameof(firstName));
        LastName = Require(lastName, nameof(lastName));
    }

    public Guid OrganizationId { get; private set; }
    public string NationalId { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
