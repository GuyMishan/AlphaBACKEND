using Alpha.Domain.Common;

namespace Alpha.Domain.Employees;

public enum EmploymentStatus { Active = 1, UnpaidLeave = 2, Ended = 3 }

public sealed class Employment : Entity
{
    private Employment() { }

    public Employment(Guid organizationId, Guid employerId, Guid personId, DateOnly startDate, string employeeNumber)
    {
        OrganizationId = organizationId;
        EmployerId = employerId;
        PersonId = personId;
        StartDate = startDate;
        EmployeeNumber = string.IsNullOrWhiteSpace(employeeNumber)
            ? throw new ArgumentException("Employee number is required.", nameof(employeeNumber))
            : employeeNumber.Trim();
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public Guid PersonId { get; private set; }
    public string EmployeeNumber { get; private set; } = string.Empty;
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public EmploymentStatus Status { get; private set; } = EmploymentStatus.Active;

    public void Update(string employeeNumber, DateOnly startDate)
    {
        EmployeeNumber = string.IsNullOrWhiteSpace(employeeNumber)
            ? throw new ArgumentException("Employee number is required.", nameof(employeeNumber))
            : employeeNumber.Trim();
        StartDate = startDate;
        Touch();
    }

    public void End(DateOnly endDate)
    {
        if (endDate < StartDate) throw new ArgumentOutOfRangeException(nameof(endDate));
        EndDate = endDate;
        Status = EmploymentStatus.Ended;
        Touch();
    }
}
