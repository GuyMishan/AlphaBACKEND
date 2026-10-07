using Alpha.Domain.Auditing;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Abstractions;

public interface IAlphaDbContext
{
    DbSet<BillingAccount> BillingAccounts { get; }
    DbSet<BillingAccountPricingComponent> BillingAccountPricingComponents { get; }
    DbSet<BillingAccountPricingTier> BillingAccountPricingTiers { get; }
    DbSet<BillingPeriod> BillingPeriods { get; }
    DbSet<BillingUsage> BillingUsages { get; }
    DbSet<PaymentMethod> PaymentMethods { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentAttempt> PaymentAttempts { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<ProviderWebhookEvent> ProviderWebhookEvents { get; }
    DbSet<User> Users { get; }
    DbSet<UserInvitation> UserInvitations { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<ReferentOrganizationAssignment> ReferentOrganizationAssignments { get; }
    DbSet<ReferentEmployerAssignment> ReferentEmployerAssignments { get; }
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
    DbSet<ManualReportAttachment> ManualReportAttachments { get; }
    DbSet<PaymentConfirmation> PaymentConfirmations { get; }
    DbSet<EmployerInterfaceReportProductData> EmployerInterfaceReportProductData { get; }
    DbSet<ReportTransmission> ReportTransmissions { get; }
    DbSet<EmployerInterfaceFeedback> EmployerInterfaceFeedback { get; }
    DbSet<EmployerInterfaceTransferFeedback> EmployerInterfaceTransferFeedback { get; }
    DbSet<EmployerInterfaceContributionFeedback> EmployerInterfaceContributionFeedback { get; }
    DbSet<ReportProductTreatment> ReportProductTreatments { get; }
    DbSet<ReportProductTreatmentHistory> ReportProductTreatmentHistory { get; }
    DbSet<FeedbackProblemResolution> FeedbackProblemResolutions { get; }
    DbSet<FeedbackResolutionDocument> FeedbackResolutionDocuments { get; }
    DbSet<FeedbackProblemDecision> FeedbackProblemDecisions { get; }
    DbSet<ContributionPercentageLimit> ContributionPercentageLimits { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
