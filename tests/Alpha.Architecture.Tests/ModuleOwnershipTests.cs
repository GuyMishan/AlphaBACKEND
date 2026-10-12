using System.Text.RegularExpressions;
using Xunit;
using static Alpha.Architecture.Tests.Repository;

namespace Alpha.Architecture.Tests;

/// <summary>
/// Feature ownership and data ownership inside the shared AlphaDbContext. The compiler cannot stop one module
/// from querying another module's tables, so every cross-module DbSet usage is listed and reviewed here.
/// </summary>
public sealed class ModuleOwnershipTests
{
    /// <summary>Owning module of every DbSet. The owner defines the invariants and is the primary writer.</summary>
    private static readonly Dictionary<string, string> Owners = new()
    {
        ["AuditEvents"] = "Auditing",
        ["Users"] = "IdentityAccess", ["UserSessions"] = "IdentityAccess", ["OtpChallenges"] = "IdentityAccess",
        ["RegistrationOtpChallenges"] = "IdentityAccess", ["UserInvitations"] = "IdentityAccess",
        ["OrganizationMemberships"] = "IdentityAccess", ["EmployerUserAccesses"] = "IdentityAccess",
        ["ReferentOrganizationAssignments"] = "IdentityAccess", ["ReferentEmployerAssignments"] = "IdentityAccess",
        ["Organizations"] = "Organizations", ["OrganizationProfileSettings"] = "Organizations",
        ["Employers"] = "Workforce", ["EmployerProfileSettings"] = "Workforce", ["People"] = "Workforce",
        ["Employments"] = "Workforce", ["EmployeePensionProducts"] = "Workforce", ["EmployeePensionContributions"] = "Workforce",
        ["BillingAccounts"] = "Billing", ["BillingAccountPricingComponents"] = "Billing", ["BillingAccountPricingTiers"] = "Billing",
        ["BillingPeriods"] = "Billing", ["BillingUsages"] = "Billing", ["PaymentMethods"] = "Billing", ["Payments"] = "Billing",
        ["PaymentAttempts"] = "Billing", ["Refunds"] = "Billing", ["ProviderWebhookEvents"] = "Billing",
        ["EmployerPaymentAccounts"] = "Reporting", ["EmployerDocuments"] = "Reporting", ["BankDebitMandates"] = "Reporting", ["ContributionPercentageLimits"] = "Reporting",
        ["ManualReports"] = "Reporting", ["ManualReportEmployees"] = "Reporting", ["ManualReportProducts"] = "Reporting",
        ["ManualContributions"] = "Reporting", ["ManualReportPayments"] = "Reporting", ["ManualReportAttachments"] = "Reporting",
        ["PaymentConfirmations"] = "Reporting", ["ReportTransmissions"] = "Reporting",
        ["EmployerInterfaceFeedback"] = "Reporting", ["EmployerInterfaceContributionFeedback"] = "Reporting",
        ["EmployerInterfaceTransferFeedback"] = "Reporting", ["EmployerInterfaceReportProductData"] = "Reporting",
        ["ReportProductTreatments"] = "Reporting", ["ReportProductTreatmentHistory"] = "Reporting",
        ["FeedbackProblemResolutions"] = "Reporting", ["FeedbackProblemDecisions"] = "Reporting",
        ["FeedbackCorrectionResolutionLinks"] = "Reporting", ["FeedbackResolutionDocuments"] = "Reporting",
        ["FeedbackExternalCases"] = "Reporting", ["FeedbackExternalCaseProblems"] = "Reporting",
        ["FeedbackExternalCaseEvents"] = "Reporting", ["FeedbackExternalCaseAttachments"] = "Reporting"
    };

    /// <summary>
    /// Reviewed cross-module usages (reads unless noted). Rationale per module:
    /// IdentityAccess resolves tenant/employer scope for authorization; Organizations runs the atomic Employer
    /// Transfer (moves workforce, reporting, access and billing rows) and onboarding; Workforce reads tenant
    /// state and access scope; Billing reads usage/scope for pricing and entitlements; Reporting snapshots
    /// workforce data into reports; Workforce blocks deleting a pension product that still has Reporting documents.
    /// Every module appends audit rows. See docs/architecture.md.
    /// </summary>
    private static readonly Dictionary<string, string[]> ApprovedForeignAccess = new()
    {
        ["IdentityAccess"] = ["AuditEvents", "Employers", "Organizations"],
        ["Organizations"] =
        [
            "AuditEvents", "EmployerInterfaceFeedback", "EmployerPaymentAccounts", "EmployerProfileSettings",
            "EmployerUserAccesses", "Employers", "Employments", "ManualReportEmployees", "ManualReports",
            "OrganizationMemberships", "People", "ReferentEmployerAssignments", "ReferentOrganizationAssignments",
            "ReportTransmissions", "UserInvitations", "Users"
        ],
        ["Workforce"] =
        [
            "AuditEvents", "EmployerDocuments", "EmployerPaymentAccounts", "EmployerUserAccesses", "Organizations",
            "ReferentEmployerAssignments", "Users"
        ],
        ["Billing"] =
        [
            "AuditEvents", "EmployerPaymentAccounts", "EmployerProfileSettings", "EmployerUserAccesses", "Employers",
            "Employments", "OrganizationMemberships", "Organizations", "UserInvitations"
        ],
        ["Reporting"] =
        [
            "AuditEvents", "EmployeePensionContributions", "EmployeePensionProducts", "EmployerProfileSettings",
            "Employers", "Employments", "Organizations", "People", "Users"
        ],
        ["ReferenceData"] = [],
        ["Auditing"] = []
    };

    [Fact]
    public void Every_dbset_has_exactly_one_owning_module()
    {
        var dbSets = Regex.Matches(Read("src/Alpha.Infrastructure/Persistence/AlphaDbContext.cs"),
                @"public DbSet<[A-Za-z]+> (?<name>[A-Za-z]+)\b").Select(m => m.Groups["name"].Value)
            .Order(StringComparer.Ordinal);
        Assert.Equal(dbSets, Owners.Keys.Order(StringComparer.Ordinal));
        Assert.All(Owners.Values, owner => Assert.Contains(owner, BusinessModules));
    }

    [Fact]
    public void Cross_module_data_access_is_limited_to_the_reviewed_allow_list()
    {
        foreach (var module in BusinessModules)
        {
            var sources = SourceFiles($"src/Modules/{module}").Select(File.ReadAllText).ToArray();
            var foreign = Owners
                .Where(set => set.Value != module && sources.Any(s => Regex.IsMatch(s, $@"\bdb\.{set.Key}\b")))
                .Select(set => set.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();

            // Equality (not subset) keeps the list honest: a removed dependency must be removed here too.
            Assert.True(ApprovedForeignAccess[module].Order(StringComparer.Ordinal).SequenceEqual(foreign),
                $"{module} foreign DbSet usage changed. Actual: [{string.Join(", ", foreign)}]. " +
                "Review the ownership impact, then update ApprovedForeignAccess and docs/architecture.md.");
        }
    }

    [Fact]
    public void Http_endpoints_live_under_module_features()
    {
        var misplaced = Directory.EnumerateFiles(Path("src/Modules"), "*Endpoints*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .Select(Relative)
            .Where(p => !p.Contains("/Features/", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(misplaced);
    }

    [Fact]
    public void Infrastructure_contains_persistence_only()
    {
        var files = SourceFiles("src/Alpha.Infrastructure").Select(Relative).ToArray();
        Assert.All(files, file => Assert.True(
            file.StartsWith("src/Alpha.Infrastructure/Persistence/", StringComparison.Ordinal) ||
            file == "src/Alpha.Infrastructure/DependencyInjection.cs", $"Unexpected infrastructure file {file}"));
        Assert.DoesNotContain(SourceFiles("src/Alpha.Infrastructure").Select(File.ReadAllText), source =>
            source.Contains("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
