using Alpha.Domain.Employees;
using Alpha.Domain.Employers;

namespace Alpha.Modules.Workforce.Features.EmployerManagement;

public sealed record CreateEmployerRequest(string LegalName, string RegistrationNumber, string WithholdingFileNumber,
    string? ContactFirstName = null, string? ContactLastName = null, string? ContactPhone = null,
    string? ContactEmail = null, string? ContactMobile = null);
public sealed record UpdateEmployerRequest(string LegalName, string RegistrationNumber, string WithholdingFileNumber,
    string? ContactFirstName = null, string? ContactLastName = null, string? ContactPhone = null,
    string? ContactEmail = null, string? ContactMobile = null);
public sealed record UpdateEmployerStatusRequest(EmployerStatus Status);
public sealed record CreateEmployeeRequest(string NationalId, string FirstName, string LastName, string EmployeeNumber,
    DateOnly StartDate, decimal MonthlySalary, DateOnly BirthDate, PersonGender Gender,
    string Email, string Mobile, string City, string Street, string HouseNumber, string Apartment,
    string PostalCode, string PostOfficeBox, PersonIdentifierType IdentifierType = PersonIdentifierType.IsraeliId, string? Identifier = null);
public sealed record UpdateEmployeeRequest(string NationalId, string FirstName, string LastName, string EmployeeNumber,
    DateOnly StartDate, decimal MonthlySalary, DateOnly BirthDate, PersonGender Gender,
    string Email, string Mobile, string City, string Street, string HouseNumber, string Apartment,
    string PostalCode, string PostOfficeBox, PersonIdentifierType IdentifierType = PersonIdentifierType.IsraeliId, string? Identifier = null);
