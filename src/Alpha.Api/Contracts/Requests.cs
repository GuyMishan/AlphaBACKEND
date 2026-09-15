using Alpha.Domain.Organizations;

namespace Alpha.Api.Contracts;

public sealed record CreateOrganizationRequest(string Name, OrganizationType Type);
public sealed record AddMembershipRequest(Guid UserId, OrganizationRole Role, EmployerAccessMode EmployerAccessMode);
public sealed record CreateEmployerRequest(string LegalName, string RegistrationNumber, string WithholdingFileNumber);
public sealed record UpdateEmployerRequest(string LegalName, string RegistrationNumber, string WithholdingFileNumber);
public sealed record CreateEmployeeRequest(string NationalId, string FirstName, string LastName, string EmployeeNumber, DateOnly StartDate, decimal MonthlySalary = 0);
public sealed record UpdateEmployeeRequest(string NationalId, string FirstName, string LastName, string EmployeeNumber, DateOnly StartDate, decimal MonthlySalary = 0);
