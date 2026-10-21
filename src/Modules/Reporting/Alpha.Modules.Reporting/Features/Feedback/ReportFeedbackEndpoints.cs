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
    private sealed record TreatmentStatusOption(string Code, string Label);
    private static readonly TreatmentStatusOption[] TreatmentStatuses =
    [
        new("manual-refund-completed", "בוצע החזר ידני על ידי הקופה"),
        new("mobility-completed", "בוצע ניוד/מעבר לקופה אחרת"),
        new("file-retransmitted-completed", "בוצע שידור חוזר לקובץ"),
        new("negative-correction-completed", "בוצע שלילי-תיקון תנועות"),
        new("refund-request-excess", "בקשת החזר (שלילי)-כסף עודף"),
        new("refund-request-wrong-fund", "בקשת החזר (שלילי)-קופה שגויה"),
        new("employer-negative-cancelled", "הועבר קובץ שלילי ממעסיק-בוטל ביצרן"),
        new("agent-ownership-pending", "יש לפנות לסוכן ממתין לביצוע קבלת בעלות"),
        new("agent-policy-pending", "יש לפנות לסוכן ממתין להפקת פוליסה"),
        new("agent-changes-pending", "יש לפנות לסוכן ממתין לביצוע שינויים"),
        new("refund-not-approved", "לא אושר החזר (כסף לא הוחזר)"),
        new("reopened-after-received", "לטיפול חוזר לאחר שהוגדר נקלט ושונה"),
        new("refund-pending", "ממתין לביצוע החזר"),
        new("company-inquiry-pending", "ממתין לברור בחברה"),
        new("split-report-pending", "ממתין לדוח פיצול"),
        new("payment-report-pending", "ממתין לדוח תשלומים"),
        new("employer-declaration-pending", "ממתין להצהרת מעסיק (צרופה)"),
        new("treatment-pending", "ממתין לטיפול"),
        new("company-treatment-pending", "ממתין לטיפול בחברה"),
        new("fault-treatment-pending", "ממתין לטיפול בתקלה"),
        new("internal-treatment-pending", "ממתין לטיפול פנימי"),
        new("employer-power-of-attorney-pending", "ממתין ליפוי כח מהמעסיק"),
        new("employer-documents-pending", "ממתין למסמכים ממעסיק"),
        new("fund-power-of-attorney-update-pending", "ממתין לעידכון יפוי כח בקופה"),
        new("manual-settlement-pending", "ממתין לפרעון ידני"),
        new("reference-pending", "ממתין לקבלת אסמכתא"),
        new("negative-file-pending", "ממתין לקובץ שלילי"),
        new("negative-and-payroll-change-pending", "ממתין לקובץ שלילי ושינוי בתוכנת שכר"),
        new("employer-file-transmission-pending", "ממתין לשידור קובץ מעסיק"),
        new("payroll-and-corrective-file-pending", "ממתין לתיקון בת.השכר וק.מתקן"),
        new("employer-response-pending", "ממתין לתשובת מעסיק"),
        new("payroll-change-required", "נדרש שינוי בתוכנת השכר"),
        new("received", "נקלט"),
        new("received-with-excess", "נקלט ונשאר עודף בפיקדון-לברר מול סוכן"),
        new("partially-received-fault-pending", "נקלט חלקי- ממתין לטיפול בתקלה"),
        new("manufacturer-received-changed", "נקלט שונה ביצרן"),
        new("duplicate-file-cancelled", "קובץ כפול ממעסיק-בוטל ביצרן"),
        new("invalid-file-cancelled", "קובץ שגוי ממעסיק-בוטל ביצרן"),
        new("file-retransmission", "שידור חוזר לקובץ"),
    ];

    public static IEndpointRouteBuilder MapReportFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/report-feedback")
            .RequireAuthorization().WithTags("Report feedback");
        group.MapGet("/", ListAsync);
        group.MapGet("/employer-context", EmployerContextAsync);
        group.MapGet("/resolution-context", EmployerResolutionContextAsync);
        group.MapGet("/treatment-statuses", TreatmentStatusOptionsAsync);
        group.MapGet("/{reportId:guid}", DetailsAsync);
        group.MapGet("/{reportId:guid}/context", ReportContextAsync);
        group.MapGet("/{reportId:guid}/resolution-context", ReportResolutionContextAsync);
        group.MapGet("/{reportId:guid}/deposits", DepositListAsync);
        group.MapGet("/{reportId:guid}/deposits/{reportProductId:guid}", DepositDetailsAsync);
        group.MapGet("/{reportId:guid}/deposits/{reportProductId:guid}/resolution-context", DepositResolutionContextAsync);
        group.MapPost("/{reportId:guid}/deposits/{reportProductId:guid}/resolution-actions/employee/validate", ValidateEmployeeResolutionActionAsync);
        group.MapPost("/{reportId:guid}/resolution-actions/internal/prepare", PrepareInternalResolutionActionAsync);
        group.MapPost("/{reportId:guid}/resolution-actions/problems/resolve", ResolveProblemsAsync);
        group.MapGet("/correction-workspaces/{workspaceReportId:guid}/resolution-links", CorrectionWorkspaceResolutionLinksAsync);
        group.MapPost("/{reportId:guid}/resolution-actions/decision", DecideProblemAsync);
        group.MapGet("/{reportId:guid}/resolution-actions/{problemId}/original-movement-candidates", OriginalMovementCandidatesAsync);
        group.MapPost("/{reportId:guid}/resolution-actions/{problemId}/link-original", LinkOriginalMovementAsync);
        group.MapPost("/{reportId:guid}/resolution-actions/external-case/open", OpenExternalCaseAsync);
        group.MapManufacturerInquiryEndpoints();
        group.MapGet("/external-cases/{caseId:guid}", ExternalCaseDetailsAsync);
        group.MapPost("/external-cases/{caseId:guid}/events", AddExternalCaseEventAsync);
        group.MapPut("/external-cases/{caseId:guid}/assignment", AssignExternalCaseAsync);
        group.MapPut("/external-cases/{caseId:guid}/status", UpdateExternalCaseStatusAsync);
        group.MapPut("/external-cases/{caseId:guid}/template", UpdateExternalCaseTemplateAsync);
        group.MapPost("/external-cases/{caseId:guid}/attachments", UploadExternalCaseAttachmentAsync)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        group.MapGet("/external-cases/{caseId:guid}/attachments/{attachmentId:guid}", DownloadExternalCaseAttachmentAsync);
        group.MapPost("/{reportId:guid}/resolution-actions/documents/{problemId}", UploadResolutionDocumentAsync).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        group.MapPut("/{reportId:guid}/deposits/{reportProductId:guid}/treatment", UpdateTreatmentAsync);
        group.MapGet("/{reportId:guid}/exports/{exportType}", ExportAsync);
        return endpoints;
    }

    private static object FeedbackIssue(EmployerInterfaceContributionFeedback row, FeedbackTreatmentTarget? target = null, IReadOnlyCollection<Guid>? affectedReportIds = null) => new
    {
        code = row.ErrorCode ?? 0,
        description = string.IsNullOrWhiteSpace(row.ErrorDescription)
            ? EmployerInterfaceLineFeedbackParser.Description(row.ErrorCode)
            : row.ErrorDescription,
        scope = (target ?? FeedbackTreatmentTargetCatalog.Resolve(row.ErrorCode)).ToString().ToLowerInvariant(),
        row.ReportId,
        row.ReportProductId,
        row.ContributionId,
        row.ErrorAmount,
        row.ErrorDate,
        row.ReceivedAt
    };

    // A target choice is an append-only routing decision, not a resolved error.
    // Classify using the effective employer payment account, not user choices or code alone.
    // Only code 56 provides evidence of a bad account. A matching reported account
    // means the defect is in the shared account; a distinct or unknown account
    // stays with the individual deposit. Other financial feedback stays local.
    private static async Task<Dictionary<Guid, FeedbackTreatmentTarget>> TreatmentTargetsAsync(
        IReadOnlyCollection<EmployerInterfaceContributionFeedback> rows, Guid employerId,
        AlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        var result = new Dictionary<Guid, FeedbackTreatmentTarget>();
        var missingFieldRows = rows.Where(row => row.ErrorCode == 18).ToArray();
        if (missingFieldRows.Length > 0)
        {
            var affectedReportIds = missingFieldRows.Select(row => row.ReportId).Distinct().ToArray();
            var reportKinds = await db.ManualReports.AsNoTracking()
                .Where(report => affectedReportIds.Contains(report.Id) && report.EmployerId == employerId)
                .Select(report => new { report.Id, report.ReportKind })
                .ToDictionaryAsync(report => report.Id, ct);
            foreach (var row in missingFieldRows)
            {
                var number = EmployerInterface006FieldMapInitializer.Code18FieldNumber(row.ErrorAmount);
                if (!number.HasValue || !reportKinds.TryGetValue(row.ReportId, out var report)) continue;
                var type = report.ReportKind == ManualReportKind.Negative ? 13 : 12;
                var field = EmployerInterface006FieldMapInitializer.Find(type, number.Value);
                if (field is null) continue;
                result[row.Id] = Code18TargetForField(field);
            }
        }
        var bankRows = rows.Where(row => row.ErrorCode == 56).ToArray();
        if (bankRows.Length == 0) return result;
        var effective = await new ReportPaymentAccountService(db)
            .ResolveForReportAsync(employerId, null, ct);
        if (effective?.AccountNumberEncrypted is null) return result;
        var account = protector.Unprotect(effective.AccountNumberEncrypted, "bank-account-number");
        if (string.IsNullOrWhiteSpace(account)) return result;
        var ids = bankRows.Select(row => row.ReportProductId).Distinct().ToArray();
        var payments = await db.ManualReportPayments.AsNoTracking()
            .Where(payment => ids.Contains(payment.ReportProductId))
            .ToDictionaryAsync(payment => payment.ReportProductId, ct);
        static string Digits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());
        foreach (var row in bankRows)
        {
            if (!payments.TryGetValue(row.ReportProductId, out var payment)
                || string.IsNullOrWhiteSpace(payment.EmployerAccount)) continue;
            var reported = protector.Unprotect(payment.EmployerAccount,
                $"report-payment-account:{payment.ReportProductId}");
            if (Digits(reported) == Digits(account)
                && Digits(payment.EmployerBankCode).TrimStart('0') == effective.BankId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && Digits(payment.EmployerBranch).TrimStart('0') == effective.BranchId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                result[row.Id] = FeedbackTreatmentTarget.Employer;
        }
        return result;
    }

    // V006 blocks describe document ownership. The report/deposit distinction
    // refers to where the error is resolved, not where its XML element is nested.
    private static FeedbackTreatmentTarget Code18TargetForField(
        EmployerInterface006FieldMapInitializer.FieldRow field) => field.XmlBlock switch
    {
        // Sender/recipient/contact values are maintained outside individual deposits.
        "NetuneiGoremSholech" or "NetuneiGoremNimaan" or
        "YeshutGoremPoneLemislaka" => FeedbackTreatmentTarget.Employer,
        // Fund, employee and contribution values are scoped to the relevant
        // employee/product deposit; employee profile fixes remain in that flow.
        "PirteiKupa" or "PirteiOved" or "ChodeshMaskoretVestatusOved" or
        "PizulHafrashotOvedBeKupa" or "SachHafrashaLeKupaBechodeshMaskoretOved" or
        "SachHafrashaLeOvedBekupa" or "SachHafrashaLeKupaMaasik" =>
            FeedbackTreatmentTarget.Deposit,
        // File headers, transfer details, event-level details and closing totals
        // belong to the report, not to an individual employee.
        _ => FeedbackTreatmentTarget.Report
    };

    private static FeedbackTreatmentTarget TreatmentTargetFor(
        EmployerInterfaceContributionFeedback row,
        IReadOnlyDictionary<Guid, FeedbackTreatmentTarget> targets) =>
        targets.TryGetValue(row.Id, out var target)
            ? target : FeedbackTreatmentTargetCatalog.Resolve(row.ErrorCode);

    private static async Task<IReadOnlyList<EmployerInterfaceContributionFeedback>> ActiveActionableFeedbackAsync(
        IReadOnlyCollection<Guid> reportIds, AlphaDbContext db, CancellationToken ct)
    {
        if (reportIds.Count == 0) return Array.Empty<EmployerInterfaceContributionFeedback>();
        var activeFeedbackIds = new HashSet<Guid>();
        foreach (var reportId in reportIds)
            activeFeedbackIds.UnionWith(await ActiveFeedbackIdsAsync(reportId, db, ct));

        if (activeFeedbackIds.Count == 0) return Array.Empty<EmployerInterfaceContributionFeedback>();

        var rows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => reportIds.Contains(x.ReportId) && activeFeedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var latestRows = rows
            .GroupBy(x => x.ContributionId)
            .SelectMany(group =>
            {
                var latest = group.First();
                return group.Where(x => x.FeedbackId == latest.FeedbackId).OrderBy(x => x.Sequence);
            })
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .ToArray();

        var problemIds = latestRows
            .Where(x => x.ErrorCode.HasValue)
            .Select(x => FeedbackResolutionWireProjection.BuildProblemId(
                x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode!.Value))
            .ToArray();
        if (problemIds.Length == 0) return latestRows;

        var resolved = await EffectiveResolvedProblemIdsAsync(problemIds, db, ct);

        return latestRows
            .Where(x => !x.ErrorCode.HasValue
                || !resolved.Contains(FeedbackResolutionWireProjection.BuildProblemId(
                    x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode.Value)))
            .ToArray();
    }

    private static async Task<HashSet<string>> EffectiveResolvedProblemIdsAsync(
        IReadOnlyCollection<string> problemIds, AlphaDbContext db, CancellationToken ct)
    {
        if (problemIds.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var resolutions = await db.FeedbackProblemResolutions.AsNoTracking()
            .Where(item => problemIds.Contains(item.ProblemId))
            .Select(item => new { item.ProblemId, item.ErrorCode, item.ResolutionSource })
            .ToListAsync(ct);
        if (resolutions.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        static bool RequiresCompletedCorrection(string source, int errorCode)
        {
            if (source is "employee-save" or "workspace-validation" or "link-original")
                return true;
            if (source != "deposit-save")
                return false;

            // A payment issue such as code 50 is deliberately RevalidateOnly: once the
            // server has revalidated the corrected payment data it does not need a
            // transmitted correction revision. Every other deposit-save path fails
            // closed behind correction completion.
            return !FeedbackResolutionPlaybookCatalog.TryGet(errorCode, out var playbook)
                || playbook.CorrectionBehavior != FeedbackCorrectionBehavior.RevalidateOnly;
        }

        var immediate = resolutions
            .Where(item => !RequiresCompletedCorrection(item.ResolutionSource, item.ErrorCode))
            .Select(item => item.ProblemId)
            .ToHashSet(StringComparer.Ordinal);
        var correctionProblemIds = resolutions
            .Where(item => RequiresCompletedCorrection(item.ResolutionSource, item.ErrorCode))
            .Select(item => item.ProblemId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (correctionProblemIds.Length == 0) return immediate;

        var correctionLinks = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
            .Where(link => correctionProblemIds.Contains(link.ProblemId))
            .ToListAsync(ct);
        if (correctionLinks.Count == 0) return immediate;

        var workspaceIds = correctionLinks.Select(link => link.WorkspaceReportId).Distinct().ToArray();
        var sentWorkspaces = await db.ManualReports.AsNoTracking()
            .Where(report => workspaceIds.Contains(report.Id)
                && report.IsRevisionSnapshot && report.Status == ManualReportStatus.Completed)
            .Select(report => report.Id).ToHashSetAsync(ct);
        if (sentWorkspaces.Count == 0) return immediate;

        // Completing the 006 dispatch is a pending state, never a successful reply.
        // Confirm each affected technical product against fresh feedback for the
        // exact technical transmission; missing/partial/failed feedback stays open.
        var documents = await db.ManualReports.AsNoTracking()
            .Where(report => report.CorrectionWorkspaceId.HasValue
                && sentWorkspaces.Contains(report.CorrectionWorkspaceId.Value)
                && report.IsTechnicalCorrectionDocument)
            .Select(report => new { report.Id, WorkspaceId = report.CorrectionWorkspaceId!.Value })
            .ToListAsync(ct);
        var documentIds = documents.Select(document => document.Id).ToArray();
        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(employee => documentIds.Contains(employee.ReportId))
            .Select(employee => new { employee.Id, employee.ReportId }).ToListAsync(ct);
        var employeeIds = employees.Select(employee => employee.Id).ToArray();
        var documentProducts = await db.ManualReportProducts.AsNoTracking()
            .Where(product => employeeIds.Contains(product.ReportEmployeeId))
            .Select(product => new { product.Id, product.SourceReportProductId, product.ReportEmployeeId })
            .ToListAsync(ct);
        var productIds = documentProducts.Select(product => product.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(contribution => productIds.Contains(contribution.ReportProductId)
                && (contribution.Amount != 0m || contribution.Percentage != 0m
                    || contribution.ExemptPayments != 0m))
            .Select(contribution => new { contribution.Id, contribution.ReportProductId })
            .ToListAsync(ct);
        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(tx => documentIds.Contains(tx.ReportId))
            .OrderByDescending(tx => tx.AttemptNumber)
            .Select(tx => new { tx.Id, tx.ReportId, tx.RoutingKey })
            .ToListAsync(ct);
        var latestTx = transmissions.GroupBy(tx => tx.ReportId)
            .ToDictionary(group => group.Key, group =>
                group.GroupBy(tx => string.IsNullOrWhiteSpace(tx.RoutingKey)
                        ? "legacy" : tx.RoutingKey, StringComparer.OrdinalIgnoreCase)
                    .Select(route => route.First().Id).ToHashSet());
        var feedbackFiles = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(file => file.ReportId.HasValue && documentIds.Contains(file.ReportId.Value))
            .Select(file => new { file.Id, file.ReportId, file.TransmissionId })
            .ToListAsync(ct);
        var freshFeedbackIds = feedbackFiles
            .Where(file => file.ReportId.HasValue
                && latestTx.TryGetValue(file.ReportId.Value, out var txIds)
                && file.TransmissionId.HasValue && txIds.Contains(file.TransmissionId.Value))
            .Select(file => file.Id).ToArray();
        var feedbackRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(row => freshFeedbackIds.Contains(row.FeedbackId)
                && documentIds.Contains(row.ReportId))
            .OrderByDescending(row => row.ReceivedAt)
            .ThenByDescending(row => row.CreatedAt)
            .Select(row => new { row.ContributionId, row.ReportProductId, row.FeedbackId, row.ErrorCode, row.IntakeStatus })
            .ToListAsync(ct);
        var latestRows = feedbackRows.GroupBy(row => row.ContributionId)
            .ToDictionary(group => group.Key, group =>
                group.Where(row => row.FeedbackId == group.First().FeedbackId).ToArray());
        var confirmedProductIds = contributions.GroupBy(row => row.ReportProductId)
            .Where(group => group.All(contribution =>
                latestRows.TryGetValue(contribution.Id, out var rows)
                && rows.Any(row => row.ReportProductId == contribution.ReportProductId)
                && rows.All(row => ReportFeedbackStatusResolver.IsCorrectionFeedbackAccepted(row.IntakeStatus, row.ErrorCode))))
            .Select(group => group.Key).ToHashSet();

        var sourceProductIds = correctionLinks.Where(link => link.SourceReportProductId.HasValue)
            .Select(link => link.SourceReportProductId!.Value).Distinct().ToArray();
        var workspaceEmployeeReports = await db.ManualReportEmployees.AsNoTracking()
            .Where(employee => sentWorkspaces.Contains(employee.ReportId))
            .Select(employee => new { employee.Id, employee.ReportId })
            .ToDictionaryAsync(employee => employee.Id, employee => employee.ReportId, ct);
        var workspaceEmployees = workspaceEmployeeReports.Keys.ToArray();
        var workspaceProducts = await db.ManualReportProducts.AsNoTracking()
            .Where(product => workspaceEmployees.Contains(product.ReportEmployeeId)
                && product.SourceReportProductId.HasValue
                && sourceProductIds.Contains(product.SourceReportProductId.Value))
            .Select(product => new { product.Id, product.SourceReportProductId, product.ReportEmployeeId })
            .ToListAsync(ct);
        foreach (var link in correctionLinks)
        {
            if (!sentWorkspaces.Contains(link.WorkspaceReportId))
                continue;
            if (!link.SourceReportProductId.HasValue)
            {
                // Report-level correction: require positive acknowledgement of every
                // technical product, not only a successful file dispatch.
                var allWorkspaceProducts = workspaceProducts
                    .Where(product => workspaceEmployeeReports.GetValueOrDefault(product.ReportEmployeeId)
                        == link.WorkspaceReportId)
                    .Select(product => product.Id).ToHashSet();
                var allTechnicalProducts = documentProducts
                    .Where(product => product.SourceReportProductId.HasValue
                        && allWorkspaceProducts.Contains(product.SourceReportProductId.Value))
                    .ToArray();
                if (allTechnicalProducts.Length > 0
                    && allTechnicalProducts.All(product => confirmedProductIds.Contains(product.Id)))
                    immediate.Add(link.ProblemId);
                continue;
            }
            var mappedWorkspaceProductIds = workspaceProducts
                .Where(product => product.SourceReportProductId == link.SourceReportProductId
                    && workspaceEmployeeReports.GetValueOrDefault(product.ReportEmployeeId) == link.WorkspaceReportId)
                .Select(product => product.Id).ToHashSet();
            var matchedDocuments = documentProducts
                .Where(product => product.SourceReportProductId.HasValue
                    && mappedWorkspaceProductIds.Contains(product.SourceReportProductId.Value))
                .ToArray();
            if (matchedDocuments.Length > 0
                && matchedDocuments.All(product => confirmedProductIds.Contains(product.Id)))
                immediate.Add(link.ProblemId);
        }

        return immediate;
    }

    private static async Task<HashSet<Guid>> ActiveFeedbackIdsAsync(
        Guid reportId, AlphaDbContext db, CancellationToken ct)
    {
        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(tx => tx.ReportId == reportId)
            .OrderByDescending(tx => tx.AttemptNumber)
            .ToListAsync(ct);
        var activeTransmissionIds = ReportTransmissionFeedbackSelection.LatestAttemptIds(transmissions);
        var files = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(file => file.ReportId == reportId)
            .Select(file => new { file.Id, file.TransmissionId })
            .ToListAsync(ct);
        return files.Where(file => ReportTransmissionFeedbackSelection.IsActive(
                file.TransmissionId, activeTransmissionIds, transmissions.Count > 0))
            .Select(file => file.Id).ToHashSet();
    }

    public sealed record EmployeeResolutionActionRequest(string GroupKey, Guid EmploymentId);
    public sealed record InternalResolutionActionRequest(
        string GroupKey,
        string ResolverType,
        Guid? ReportProductId,
        IReadOnlyList<string>? ProblemIds);
    public sealed record ResolveProblemsRequest(
        IReadOnlyList<string> ProblemIds,
        string Source,
        Guid? ValidatedReportId);
    public sealed record DecideProblemRequest(
        string ProblemId,
        string Outcome,
        string? Note);
    public sealed record LinkOriginalMovementRequest(Guid ContributionId);
    public sealed record OpenExternalCaseRequest(
        string GroupKey,
        IReadOnlyList<string> ProblemIds,
        string? Note,
        string? Action = null);
    public sealed record ExternalCaseEventRequest(string Note);
    public sealed record ExternalCaseAssignmentRequest(bool AssignToMe);
    public sealed record ExternalCaseStatusRequest(string Status, string? Note);
    public sealed record ExternalCaseTemplateRequest(
        string Destination,
        string Subject,
        string MessageTemplate);
    public sealed record UpdateTreatmentRequest(string StatusCode, string? Note, DateTimeOffset? ExpectedUpdatedAt);
}
