using Alpha.Domain.Employees;
using Alpha.Domain.Organizations;

namespace Alpha.Api.Contracts;

public sealed record CreateOrganizationRequest(string Name, OrganizationType Type);
public sealed record AddMembershipRequest(Guid UserId, OrganizationRole Role, EmployerAccessMode EmployerAccessMode);
public sealed record CreateEmployerRequest(string LegalName, string RegistrationNumber, string WithholdingFileNumber,
    string? ContactFirstName = null, string? ContactLastName = null, string? ContactPhone = null,
    string? ContactEmail = null, string? ContactMobile = null);
public sealed record UpdateEmployerRequest(string LegalName, string RegistrationNumber, string WithholdingFileNumber,
    string? ContactFirstName = null, string? ContactLastName = null, string? ContactPhone = null,
    string? ContactEmail = null, string? ContactMobile = null);
public sealed record CreateEmployeeRequest(string NationalId, string FirstName, string LastName, string EmployeeNumber,
    DateOnly StartDate, decimal MonthlySalary = 0, DateOnly? BirthDate = null, PersonGender? Gender = null,
    string? Email = null, string? Mobile = null);
public sealed record UpdateEmployeeRequest(string NationalId, string FirstName, string LastName, string EmployeeNumber,
    DateOnly StartDate, decimal MonthlySalary = 0, DateOnly? BirthDate = null, PersonGender? Gender = null,
    string? Email = null, string? Mobile = null);
