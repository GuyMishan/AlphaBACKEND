using System.Globalization;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Reporting.Application;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Features.Corrections;
using Alpha.Modules.Reporting.Features.EmployerInterface006;
using Alpha.Modules.Reporting.Features.FeedbackIngestion;
using Alpha.Modules.Reporting.Features.Validation;
using Alpha.Modules.Reporting.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Alpha.Modules.Reporting.Features.Feedback;

public static partial class ReportFeedbackEndpoints
{
    private static async Task<IResult> ExportAsync(
        Guid organizationId, Guid employerId, Guid reportId, string exportType,
        AlphaDbContext db, IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)
                && (x.Amount != 0m || x.Percentage != 0m || x.ExemptPayments != 0m))
            .ToListAsync(ct);
        var employeeById = employees.ToDictionary(x => x.Id);
        var productById = products.ToDictionary(x => x.Id);

        static string ContributionName(ManualContribution c) => (c.Party, c.Component) switch
        {
            (ContributionParty.Employee, ContributionComponent.Benefits) => "תגמולי עובד",
            (ContributionParty.Employer, ContributionComponent.Benefits) => "תגמולי מעביד",
            (ContributionParty.Employer, ContributionComponent.Severance) => "פיצויים",
            (ContributionParty.Employee, ContributionComponent.Severance) => "תגמולים 47",
            (ContributionParty.Employee, ContributionComponent.Disability) => "אכ״ע עובד",
            (ContributionParty.Employer, ContributionComponent.Disability) => "אכ״ע מעסיק",
            (ContributionParty.Employee, ContributionComponent.Other) => "רכיב עובד נוסף",
            (ContributionParty.Employer, ContributionComponent.Other) => "רכיב מעסיק נוסף",
            _ => "רכיב הפרשה"
        };

        var rows = new List<IReadOnlyList<object?>>();
        var normalized = exportType.Trim().ToLowerInvariant();
        var sheetName = "דוח";

        if (normalized == "contributions")
        {
            sheetName = "פירוט הפרשות";
            rows.Add(new object?[] { "חודש דיווח", "עובד", "מזהה עובד", "יצרן", "מוצר", "פוליסה/חשבון", "רכיב", "שכר מבוטח", "שיעור", "סכום" });
            foreach (var contribution in contributions.OrderBy(x => x.ReportProductId).ThenBy(x => x.Party).ThenBy(x => x.Component))
            {
                var product = productById[contribution.ReportProductId];
                var employee = employeeById[product.ReportEmployeeId];
                rows.Add(new object?[]
                {
                    report.ReportingMonth.ToString("yyyy-MM"),
                    employee.FirstName + " " + employee.LastName,
                    protector.Unprotect(employee.NationalId, $"report-employee-national-id:{employee.Id}"),
                    product.FundCompanyName,
                    product.FundName,
                    product.PolicyNumber,
                    ContributionName(contribution),
                    product.Salary,
                    contribution.Percentage,
                    contribution.Amount
                });
            }
        }
        else if (normalized == "deposits")
        {
            sheetName = "סיכום הפקדות";
            var payments = await db.ManualReportPayments.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId)).ToDictionaryAsync(x => x.ReportProductId, ct);
            var totals = contributions.GroupBy(x => x.ReportProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            rows.Add(new object?[] { "חודש דיווח", "עובד", "יצרן", "מוצר", "פוליסה/חשבון", "סכום כולל", "אמצעי תשלום", "חשבון יצרן", "אסמכתא", "תאריך ערך" });
            foreach (var product in products.OrderBy(x => employeeById[x.ReportEmployeeId].LastName).ThenBy(x => x.FundCompanyName))
            {
                var employee = employeeById[product.ReportEmployeeId];
                payments.TryGetValue(product.Id, out var payment);
                rows.Add(new object?[]
                {
                    report.ReportingMonth.ToString("yyyy-MM"),
                    employee.FirstName + " " + employee.LastName,
                    product.FundCompanyName,
                    product.FundName,
                    product.PolicyNumber,
                    totals.GetValueOrDefault(product.Id),
                    payment?.PaymentMethod ?? string.Empty,
                    payment?.ProviderAccount ?? string.Empty,
                    payment?.ReferenceNumber ?? string.Empty,
                    payment?.ValueDate?.ToString("dd/MM/yyyy") ?? string.Empty
                });
            }
        }
        else if (normalized == "feedback")
        {
            sheetName = "משוב יצרן";
            var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);
            var feedbackHistory = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
                .Where(x => x.ReportId == reportId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
                .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.CreatedAt)
                .ThenBy(x => x.RecordIdentifier).ThenBy(x => x.Sequence)
                .ToListAsync(ct);
            var feedback = feedbackHistory.GroupBy(x => x.ContributionId).SelectMany(group =>
            {
                var latest = group.First();
                return group.Where(x => x.FeedbackId == latest.FeedbackId).OrderBy(x => x.Sequence);
            }).ToArray();
            var contributionById = contributions.ToDictionary(x => x.Id);
            rows.Add(new object?[] { "עובד", "יצרן", "מוצר", "רכיב", "סכום מעסיק", "שיעור מעסיק", "סכום יצרן", "שיעור יצרן", "שכר מחושב יצרן", "סטטוס קליטה", "קוד שגיאה", "פירוט", "תאריך משוב", "קובץ מקור" });
            foreach (var item in feedback)
            {
                if (!contributionById.TryGetValue(item.ContributionId, out var contribution)) continue;
                var product = productById[item.ReportProductId];
                var employee = employeeById[product.ReportEmployeeId];
                rows.Add(new object?[]
                {
                    employee.FirstName + " " + employee.LastName,
                    product.FundCompanyName,
                    product.FundName,
                    ContributionName(contribution),
                    contribution.Amount,
                    contribution.Percentage,
                    item.ContributionAmount,
                    item.ContributionRate,
                    item.CalculatedSalary,
                    item.IntakeStatus,
                    item.ErrorCode,
                    item.ErrorDescription,
                    item.ReceivedAt.ToString("dd/MM/yyyy HH:mm:ss"),
                    item.SourceFileName
                });
            }
        }
        else
        {
            return Results.BadRequest(new { error = "export_type_invalid" });
        }

        var bytes = ExcelWorkbookBuilder.Build(sheetName, rows);
        var fileLabel = normalized switch
        {
            "contributions" => "פירוט עובדים והפרשות",
            "deposits" => "סיכום הפקדות",
            _ => "משוב קופות"
        };
        var fileName = $"{fileLabel} - {report.ReportingMonth:MM-yyyy}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
