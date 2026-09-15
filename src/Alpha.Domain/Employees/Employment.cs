using Alpha.Domain.Common;

namespace Alpha.Domain.Employees;

public enum EmploymentStatus { Active = 1, UnpaidLeave = 2, Ended = 3 }

public sealed class Employment : Entity
{
    private Employment() { }

    public Employment(Guid organizationId, Guid employerId, Guid personId, DateOnly startDate, string employeeNumber,
        decimal monthlySalary = 0)
    {
        OrganizationId = organizationId;
        EmployerId = employerId;
        PersonId = personId;
        StartDate = startDate;
        EmployeeNumber = string.IsNullOrWhiteSpace(employeeNumber)
            ? throw new ArgumentException("Employee number is required.", nameof(employeeNumber))
            : employeeNumber.Trim();
        SetMonthlySalary(monthlySalary);
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public Guid PersonId { get; private set; }
    public string EmployeeNumber { get; private set; } = string.Empty;
    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public EmploymentStatus Status { get; private set; } = EmploymentStatus.Active;
    public decimal MonthlySalary { get; private set; }

    public void Update(string employeeNumber, DateOnly startDate, decimal monthlySalary)
    {
        EmployeeNumber = string.IsNullOrWhiteSpace(employeeNumber)
            ? throw new ArgumentException("Employee number is required.", nameof(employeeNumber))
            : employeeNumber.Trim();
        StartDate = startDate;
        SetMonthlySalary(monthlySalary);
        Touch();
    }

    public void UpdateMonthlySalary(decimal monthlySalary)
    {
        SetMonthlySalary(monthlySalary);
        Touch();
    }

    public void End(DateOnly endDate)
    {
        if (endDate < StartDate) throw new ArgumentOutOfRangeException(nameof(endDate));
        EndDate = endDate;
        Status = EmploymentStatus.Ended;
        Touch();
    }

    private void SetMonthlySalary(decimal monthlySalary)
    {
        if (monthlySalary < 0) throw new ArgumentOutOfRangeException(nameof(monthlySalary));
        MonthlySalary = monthlySalary;
    }
}
