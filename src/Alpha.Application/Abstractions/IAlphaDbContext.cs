using Alpha.Domain.Auditing;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Abstractions;

public interface IAlphaDbContext
{
    DbSet<BillingAccount> BillingAccounts { get; }
    DbSet<User> Users { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationMembership> OrganizationMemberships { get; }
    DbSet<OrganizationProfileSettings> OrganizationProfileSettings { get; }
    DbSet<Employer> Employers { get; }
    DbSet<EmployerUserAccess> EmployerUserAccesses { get; }
    DbSet<EmployerProfileSettings> EmployerProfileSettings { get; }
    DbSet<EmployerPaymentAccount> EmployerPaymentAccounts { get; }
    DbSet<BankDebitMandate> BankDebitMandates { get; }
    DbSet<Person> People { get; }
    DbSet<Employment> Employments { get; }
    DbSet<EmployeePensionProduct> EmployeePensionProducts { get; }
    DbSet<EmployeePensionContribution> EmployeePensionContributions { get; }
    DbSet<ManualReport> ManualReports { get; }
    DbSet<ManualReportEmployee> ManualReportEmployees { get; }
    DbSet<ManualReportProduct> ManualReportProducts { get; }
    DbSet<ManualContribution> ManualContributions { get; }
    DbSet<ManualReportPayment> ManualReportPayments { get; }
    DbSet<EmployerInterfaceReportProductData> EmployerInterfaceReportProductData { get; }
    DbSet<ReportTransmission> ReportTransmissions { get; }
    DbSet<EmployerInterfaceFeedback> EmployerInterfaceFeedback { get; }
    DbSet<ContributionPercentageLimit> ContributionPercentageLimits { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<Plan> Plans { get; }
    DbSet<Subscription> Subscriptions { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
