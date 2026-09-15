using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Abstractions;

public interface IAlphaDbContext
{
    DbSet<User> Users { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationMembership> OrganizationMemberships { get; }
    DbSet<Employer> Employers { get; }
    DbSet<EmployerUserAccess> EmployerUserAccesses { get; }
    DbSet<Person> People { get; }
    DbSet<Employment> Employments { get; }
    DbSet<EmployeePensionProduct> EmployeePensionProducts { get; }
    DbSet<EmployeePensionContribution> EmployeePensionContributions { get; }
    DbSet<ManualReport> ManualReports { get; }
    DbSet<ManualReportEmployee> ManualReportEmployees { get; }
    DbSet<ManualReportProduct> ManualReportProducts { get; }
    DbSet<ManualContribution> ManualContributions { get; }
    DbSet<ManualReportPayment> ManualReportPayments { get; }
    DbSet<ContributionPercentageLimit> ContributionPercentageLimits { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
