using System.Globalization;
using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Reporting;
using Alpha.Api.Security;
using Alpha.Api.Services;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportFeedbackEndpoints
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

    private static async Task<IResult> ListAsync(
        Guid organizationId, Guid employerId, string? feedbackStatus, string? month, string? reportKind,
        string? search, string? product, string? treatmentStatus, bool? requiresAttention, int skip, int take,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employerName = await db.Employers.AsNoTracking()
            .Where(x => x.Id == employerId && x.OrganizationId == organizationId)
            .Select(x => x.LegalName)
            .SingleOrDefaultAsync(ct) ?? string.Empty;
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        skip = Math.Max(skip, 0); take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = db.ManualReports.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument);

        if (!string.IsNullOrWhiteSpace(month))
        {
            var raw = month.Trim().Length == 7 ? month.Trim() + "-01" : month.Trim();
            if (DateOnly.TryParse(raw, out var parsed))
            {
                var target = new DateOnly(parsed.Year, parsed.Month, 1);
                query = query.Where(x => x.ReportingMonth == target);
            }
        }
        if (int.TryParse(reportKind, out var kind) && Enum.IsDefined(typeof(ManualReportKind), kind))
            query = query.Where(x => (int)x.ReportKind == kind);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var idHash = protector.LookupHash(search.Trim(), "report-employee-national-id-lookup");
            var byEmployee = db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                    && (x.FirstName.ToLower().Contains(term) || x.LastName.ToLower().Contains(term) || x.NationalIdLookupHash == idHash))
                .Select(x => x.ReportId);
            var byProduct = from p in db.ManualReportProducts.AsNoTracking()
                            join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                            where e.OrganizationId == organizationId && e.EmployerId == employerId
                                && (p.FundName.ToLower().Contains(term) || p.FundCompanyName.ToLower().Contains(term) || p.PolicyNumber.ToLower().Contains(term))
                            select e.ReportId;
            query = query.Where(x => byEmployee.Contains(x.Id) || byProduct.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(product))
        {
            var term = product.Trim().ToLower();
            var matches = from p in db.ManualReportProducts.AsNoTracking()
                          join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                          where e.OrganizationId == organizationId && e.EmployerId == employerId
                              && (p.FundName.ToLower().Contains(term) || p.FundCompanyName.ToLower().Contains(term) || p.FundCode.ToLower().Contains(term))
                          select e.ReportId;
            query = query.Where(x => matches.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(treatmentStatus))
        {
            var matches = from t in db.ReportProductTreatments.AsNoTracking()
                          join p in db.ManualReportProducts.AsNoTracking() on t.ReportProductId equals p.Id
                          join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                          where e.OrganizationId == organizationId && e.EmployerId == employerId && t.StatusCode == treatmentStatus
                          select e.ReportId;
            query = query.Where(x => matches.Contains(x.Id));
        }

        var candidateIds = await query.Select(x => x.Id).ToListAsync(ct);
        if (candidateIds.Count == 0) return Results.Ok(new { items = Array.Empty<object>(), hasMore = false, summary = new { total = 0, completed = 0, attention = 0, pending = 0 } });

        var transmissions = await db.ReportTransmissions.AsNoTracking().Where(x => candidateIds.Contains(x.ReportId))
            .OrderByDescending(x => x.AttemptNumber).ToListAsync(ct);
        var latestTransmission = transmissions.GroupBy(x => x.ReportId).ToDictionary(g => g.Key, g => g.First());
        var feedbackFiles = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.ReportId.HasValue && candidateIds.Contains(x.ReportId.Value))
            .Select(x => new { x.Id, ReportId = x.ReportId!.Value, x.TransmissionId })
            .ToListAsync(ct);
        var activeFeedbackFiles = feedbackFiles.Where(x =>
        {
            latestTransmission.TryGetValue(x.ReportId, out var tx);
            return tx is null ? x.TransmissionId is null : x.TransmissionId == tx.Id;
        }).ToArray();
        var activeFeedbackIds = activeFeedbackFiles.Select(x => x.Id).ToHashSet();
        var officialCounts = activeFeedbackFiles
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Count());
        var expectedContributionCounts = await (
                from contribution in db.ManualContributions.AsNoTracking()
                join reportProduct in db.ManualReportProducts.AsNoTracking() on contribution.ReportProductId equals reportProduct.Id
                join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                where candidateIds.Contains(employee.ReportId)
                    && (contribution.Amount != 0m || contribution.Percentage != 0m || contribution.ExemptPayments != 0m)
                group contribution by employee.ReportId into g
                select new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var contributionFeedbackRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => candidateIds.Contains(x.ReportId) && activeFeedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new { x.ReportId, x.ReportProductId, x.ContributionId, x.FeedbackId, x.Sequence, x.ErrorCode, x.ReceivedAt, x.CreatedAt })
            .ToListAsync(ct);
        var latestContributionFeedback = contributionFeedbackRows
            .GroupBy(x => x.ContributionId)
            .SelectMany(g =>
            {
                var latest = g.First();
                return g.Where(x => x.FeedbackId == latest.FeedbackId);
            })
            .ToArray();
        var feedbackContributionCounts = latestContributionFeedback
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ContributionId).Distinct().Count());
        var latestProblemIds = latestContributionFeedback
            .Where(x => x.ErrorCode.HasValue && ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .Select(x => FeedbackResolutionWireProjection.BuildProblemId(
                x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode!.Value))
            .ToArray();
        var resolvedProblemIds = await EffectiveResolvedProblemIdsAsync(latestProblemIds, db, ct);
        var unresolvedLatestFeedback = latestContributionFeedback
            .Where(x => !x.ErrorCode.HasValue
                || !resolvedProblemIds.Contains(FeedbackResolutionWireProjection.BuildProblemId(
                    x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode.Value)))
            .ToArray();
        var errorCounts = unresolvedLatestFeedback
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ContributionId).Distinct().Count());
        var attentionProductCounts = unresolvedLatestFeedback
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ReportProductId).Distinct().Count());
        var reportIssueCounts = unresolvedLatestFeedback
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .Where(x => EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report)
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorCode).Distinct().Count());

        var completedRevisionIds = await db.ManualReports.AsNoTracking()
            .Where(x => candidateIds.Contains(x.Id) && x.IsRevisionSnapshot
                && x.Status == ManualReportStatus.Completed)
            .Select(x => x.Id).ToHashSetAsync(ct);

        string State(Guid id)
        {
            if (completedRevisionIds.Contains(id)) return "completed";
            latestTransmission.TryGetValue(id, out var tx);
            return ReportFeedbackStatusResolver.ResolveReportState(
                tx?.Status,
                officialCounts.GetValueOrDefault(id),
                expectedContributionCounts.GetValueOrDefault(id),
                feedbackContributionCounts.GetValueOrDefault(id),
                errorCounts.GetValueOrDefault(id));
        }

        var normalizedStatus = feedbackStatus?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedStatus) && normalizedStatus != "all")
            candidateIds = candidateIds.Where(id => State(id) == normalizedStatus).ToList();
        if (requiresAttention == true)
            candidateIds = candidateIds.Where(id => State(id) == "attention").ToList();
        if (candidateIds.Count == 0) return Results.Ok(new { items = Array.Empty<object>(), hasMore = false, summary = new { total = 0, completed = 0, attention = 0, pending = 0 } });

        var summary = new
        {
            total = candidateIds.Count,
            completed = candidateIds.Count(id => State(id) == "completed"),
            attention = candidateIds.Sum(id => attentionProductCounts.GetValueOrDefault(id)),
            pending = candidateIds.Count(id => State(id) is "pending" or "partial")
        };

        var page = await db.ManualReports.AsNoTracking().Where(x => candidateIds.Contains(x.Id))
            .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt)
            .Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take; if (hasMore) page.RemoveAt(page.Count - 1);
        var pageIds = page.Select(x => x.Id).ToArray();

        var correctionWorkspaces = await db.ManualReports.AsNoTracking()
            .Where(x => x.IsCorrectionWorkspace && x.SourceReportId.HasValue
                && pageIds.Contains(x.SourceReportId.Value)
                && (x.Status == ManualReportStatus.Draft
                    || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Error))
            .Select(x => new { x.Id, SourceReportId = x.SourceReportId!.Value, x.HasCorrectionChanges })
            .ToListAsync(ct);
        var workspaceBySource = correctionWorkspaces.ToDictionary(x => x.SourceReportId);
        var workspaceChangedCounts = new Dictionary<Guid, int>();
        foreach (var correctionWorkspace in correctionWorkspaces)
            workspaceChangedCounts[correctionWorkspace.Id] =
                await CorrectionWorkflowService.PendingChangeCountAsync(correctionWorkspace.Id, db, protector, ct);

        var employeeCounts = await db.ManualReportEmployees.AsNoTracking().Where(x => pageIds.Contains(x.ReportId))
            .GroupBy(x => x.ReportId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var productCounts = await (
                from reportProduct in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                where pageIds.Contains(employee.ReportId)
                group reportProduct by employee.ReportId into g
                select new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var pageOperations = await (
                from metadata in db.EmployerInterfaceReportProductData.AsNoTracking()
                join reportProduct in db.ManualReportProducts.AsNoTracking() on metadata.ReportProductId equals reportProduct.Id
                join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                where pageIds.Contains(employee.ReportId)
                select new { employee.ReportId, metadata.OperationCode })
            .ToListAsync(ct);
        var metadataCounts = pageOperations
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Count());
        var operation6Reports = pageOperations
            .GroupBy(x => x.ReportId)
            .Where(g => g.Count() == productCounts.GetValueOrDefault(g.Key) && g.All(x => x.OperationCode == 6))
            .Select(g => g.Key)
            .ToHashSet();
        var totals = await (from c in db.ManualContributions.AsNoTracking()
                            join p in db.ManualReportProducts.AsNoTracking() on c.ReportProductId equals p.Id
                            join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                            where pageIds.Contains(e.ReportId)
                            group c by e.ReportId into g select new { Id = g.Key, Total = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.Id, x => x.Total, ct);
        var transferRows = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => pageIds.Contains(x.ReportId) && activeFeedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct);
        var money = transferRows.GroupBy(x => new { x.ReportId, x.TransferIdentifier }).Select(g => g.First())
            .GroupBy(x => x.ReportId).ToDictionary(g => g.Key, g => new
            {
                Reported = g.Sum(x => x.ReportedDepositAmount),
                Allocated = g.Sum(x => x.AllocatedAmount),
                Received = g.Sum(x => x.ActualReceivedAmount),
                InTransit = g.Sum(x => x.InTransitAmount)
            });

        var revisionRoots = page.Select(x => x.RevisionRootReportId ?? x.Id).Distinct().ToArray();
        var latestRevisionByRoot = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && x.IsRevisionSnapshot && x.RevisionRootReportId.HasValue
                && revisionRoots.Contains(x.RevisionRootReportId.Value)
                && x.Status == ManualReportStatus.Completed)
            .GroupBy(x => x.RevisionRootReportId!.Value)
            .Select(g => new { RootId = g.Key, RevisionNumber = g.Max(x => x.RevisionNumber) })
            .ToDictionaryAsync(x => x.RootId, x => x.RevisionNumber, ct);

        var items = page.Select(report =>
        {
            latestTransmission.TryGetValue(report.Id, out var tx); money.TryGetValue(report.Id, out var cash);
            var rootId = report.RevisionRootReportId ?? report.Id;
            var hasNewerRevision = latestRevisionByRoot.TryGetValue(rootId, out var latestRevisionNumber)
                && (!report.IsRevisionSnapshot || report.RevisionNumber < latestRevisionNumber);
            var total = totals.GetValueOrDefault(report.Id);
            var payoffRate = cash is null
                ? null
                : ReportFeedbackStatusResolver.ResolvePayoffRate(cash.Reported, cash.Allocated);
            var issues = errorCounts.GetValueOrDefault(report.Id)
                + (string.IsNullOrWhiteSpace(report.ValidationError) ? 0 : 1)
                + (tx is null || string.IsNullOrWhiteSpace(tx.ErrorMessage) ? 0 : 1);
            return new
            {
                report.Id, employerName, report.ReportingMonth, report.SalaryPaymentDate, report.ReportKind, report.Status,
                revisionRootReportId = rootId, report.RevisionNumber, report.IsRevisionSnapshot,
                feedbackStatus = State(report.Id), hasFeedback = officialCounts.GetValueOrDefault(report.Id) > 0,
                issueCount = issues, requiresAttentionCount = attentionProductCounts.GetValueOrDefault(report.Id),
                reportIssueCount = reportIssueCounts.GetValueOrDefault(report.Id)
                    + (string.IsNullOrWhiteSpace(report.ValidationError) ? 0 : 1)
                    + (tx is null || string.IsNullOrWhiteSpace(tx.ErrorMessage) ? 0 : 1),
                employeeCount = employeeCounts.GetValueOrDefault(report.Id), totalAmount = total, payoffRate,
                allocatedAmount = cash?.Allocated, actualReceivedAmount = cash?.Received, inTransitAmount = cash?.InTransit,
                canEdit = canCreateReport && report.IsEditable,
                canDelete = canCreateReport && report.IsEditable
                    && tx is null && officialCounts.GetValueOrDefault(report.Id) == 0,
                canStartCorrectionWorkspace = canCreateReport
                    && report.ReportKind == ManualReportKind.Current
                    && report.Status is ManualReportStatus.Sent or ManualReportStatus.Completed
                    && !hasNewerRevision,
                correctionWorkspaceId = workspaceBySource.TryGetValue(report.Id, out var workspace)
                    ? workspace.Id : (Guid?)null,
                pendingCorrectionCount = workspaceBySource.TryGetValue(report.Id, out var pendingWorkspace)
                    ? workspaceChangedCounts.GetValueOrDefault(pendingWorkspace.Id)
                    : 0,
                canCreateCorrection = ReportFeedbackStatusResolver.CanCreateCorrection(
                    canCreateReport, report.IsEditable, report.Status, report.ReportKind,
                    productCounts.GetValueOrDefault(report.Id) > 0
                        && metadataCounts.GetValueOrDefault(report.Id) == productCounts.GetValueOrDefault(report.Id),
                    operation6Reports.Contains(report.Id)),
                report.CreatedAt, report.UpdatedAt,
                lastTransmission = tx is null ? null : new { tx.Id, tx.AttemptNumber, tx.Status, tx.Provider, tx.ExternalId, tx.ErrorMessage, tx.StartedAt, tx.SentAt, tx.CompletedAt }
            };
        }).ToList();
        return Results.Ok(new { items, hasMore, summary });
    }

    private static async Task<IResult> TreatmentStatusOptionsAsync(Guid organizationId, Guid employerId, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        return Results.Ok(TreatmentStatuses.Select(x => new { x.Code, x.Label }));
    }

    private static async Task<IResult> DepositListAsync(
        Guid organizationId, Guid employerId, Guid reportId, string? search, string? manufacturer, int skip, int take,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        skip = Math.Max(0, skip); take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = from p in db.ManualReportProducts.AsNoTracking()
                    join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                    where e.ReportId == reportId select new { Product = p, Employee = e };

        var manufacturers = await query
            .Select(x => string.IsNullOrWhiteSpace(x.Product.FundCompanyName) ? x.Product.FundName : x.Product.FundCompanyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var idHash = protector.LookupHash(search.Trim(), "report-employee-national-id-lookup");
            query = query.Where(x => x.Employee.FirstName.ToLower().Contains(term) || x.Employee.LastName.ToLower().Contains(term)
                || x.Employee.NationalIdLookupHash == idHash || x.Product.FundName.ToLower().Contains(term)
                || x.Product.FundCompanyName.ToLower().Contains(term) || x.Product.PolicyNumber.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            var manufacturerTerm = manufacturer.Trim().ToLower();
            query = query.Where(x =>
                (!string.IsNullOrWhiteSpace(x.Product.FundCompanyName)
                    ? x.Product.FundCompanyName.ToLower()
                    : x.Product.FundName.ToLower()) == manufacturerTerm);
        }
        var page = await query.OrderBy(x => x.Employee.LastName).ThenBy(x => x.Employee.FirstName)
            .ThenBy(x => x.Product.AllocationOrder).ThenBy(x => x.Product.CreatedAt).Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take; if (hasMore) page.RemoveAt(page.Count - 1);
        var productIds = page.Select(x => x.Product.Id).ToArray();
        var correctionWorkspace = await db.ManualReports.AsNoTracking()
            .Where(x => x.IsCorrectionWorkspace && x.SourceReportId == reportId
                && (x.Status == ManualReportStatus.Draft
                    || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Error))
            .Select(x => new { x.Id, x.HasCorrectionChanges })
            .SingleOrDefaultAsync(ct);

        var pendingBySourceProduct = new Dictionary<Guid, (Guid ProductId, bool Changed)>();
        if (correctionWorkspace is not null)
        {
            var workspaceEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.ReportId == correctionWorkspace.Id)
                .Select(x => x.Id)
                .ToArrayAsync(ct);
            var workspaceProducts = await db.ManualReportProducts.AsNoTracking()
                .Where(x => workspaceEmployeeIds.Contains(x.ReportEmployeeId)
                    && x.SourceReportProductId.HasValue
                    && productIds.Contains(x.SourceReportProductId.Value))
                .Select(x => new
                {
                    SourceId = x.SourceReportProductId!.Value,
                    ProductId = x.Id,
                    x.IsCorrectionChanged
                })
                .ToListAsync(ct);
            pendingBySourceProduct = workspaceProducts.ToDictionary(
                x => x.SourceId,
                x => (x.ProductId, x.IsCorrectionChanged));
        }

        var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);

        var totals = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)
                && (x.Amount != 0m || x.Percentage != 0m || x.ExemptPayments != 0m))
            .GroupBy(x => x.ReportProductId).Select(g => new { Id = g.Key, Total = g.Sum(x => x.Amount), Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, ct);
        var feedback = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId) && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct);
        var latestFeedbackRows = feedback
            .GroupBy(x => x.ContributionId)
            .SelectMany(group =>
            {
                var latest = group.First();
                return group.Where(x => x.FeedbackId == latest.FeedbackId);
            })
            .ToArray();
        var depositProblemIds = latestFeedbackRows
            .Where(x => x.ErrorCode.HasValue && ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .Select(x => FeedbackResolutionWireProjection.BuildProblemId(
                x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode!.Value))
            .ToArray();
        var resolvedDepositProblemIds = await EffectiveResolvedProblemIdsAsync(depositProblemIds, db, ct);
        var latestFeedback = latestFeedbackRows
            .GroupBy(x => x.ReportProductId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var treatments = await db.ReportProductTreatments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);
        var transfers = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => x.ReportId == reportId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct);
        var latestTransfers = transfers.GroupBy(x => x.TransferIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var labels = TreatmentStatuses.ToDictionary(x => x.Code, x => x.Label, StringComparer.Ordinal);

        var items = page.Select(x =>
        {
            latestFeedback.TryGetValue(x.Product.Id, out var rows); treatments.TryGetValue(x.Product.Id, out var treatment);
            metadata.TryGetValue(x.Product.Id, out var productMetadata); totals.TryGetValue(x.Product.Id, out var total);
            var expected = total?.Count ?? 0; var received = rows?.Length ?? 0;
            var actionableFeedback = (rows ?? [])
                .Where(item => ReportFeedbackStatusResolver.IsActionableFeedbackError(item.ErrorCode))
                .Where(item => !item.ErrorCode.HasValue
                    || !resolvedDepositProblemIds.Contains(FeedbackResolutionWireProjection.BuildProblemId(
                        item.FeedbackId, item.ContributionId, item.Sequence, item.ErrorCode.Value)))
                .ToArray();
            var actionableErrors = actionableFeedback
                .Select(item => string.IsNullOrWhiteSpace(item.ErrorDescription)
                    ? $"קוד שגיאה {item.ErrorCode}"
                    : item.ErrorDescription.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var errorSummary = actionableFeedback
                .Where(item => item.ErrorCode.HasValue)
                .Select(item => new
                {
                    Code = item.ErrorCode!.Value,
                    Scope = EmployerInterfaceLineFeedbackParser.ErrorScope(item.ErrorCode)
                })
                .Distinct()
                .GroupBy(item => item.Scope)
                .ToDictionary(
                    group => group.Key.ToString().ToLowerInvariant(),
                    group => group.Count(),
                    StringComparer.Ordinal);
            var hasError = actionableErrors.Length > 0;
            var feedbackState = received == 0 ? "pending" : hasError ? "attention" : received < expected ? "partial" : "completed";
            var transferKey = !string.IsNullOrWhiteSpace(productMetadata?.InterfaceTransferIdentifier)
                ? productMetadata.InterfaceTransferIdentifier : x.Product.Id.ToString("D").ToUpperInvariant();
            latestTransfers.TryGetValue(transferKey, out var transfer);
            var totalAmount = total?.Total ?? 0m;
            var moneyState = transfer is null ? "pending" : ReportFeedbackStatusResolver.ResolveMoneyState(
                transfer.ReportedDepositAmount, transfer.ActualReceivedAmount, transfer.AllocatedAmount, transfer.InTransitAmount);
            var timestamps = new List<DateTimeOffset>();
            if (rows is { Length: > 0 }) timestamps.Add(rows.Max(item => item.ReceivedAt));
            if (treatment is not null) timestamps.Add(treatment.UpdatedAt);
            if (transfer is not null) timestamps.Add(transfer.ReceivedAt);
            return new
            {
                id = x.Product.Id, reportEmployeeId = x.Employee.Id, x.Employee.EmploymentId,
                employeeName = x.Employee.FirstName + " " + x.Employee.LastName,
                x.Product.ProductType, x.Product.FundName, x.Product.FundCompanyName, x.Product.PolicyNumber, x.Product.SalaryMonth,
                totalAmount, hasFeedback = received > 0 || transfer is not null, feedbackStatus = feedbackState,
                feedbackLabel = feedbackState switch { "completed" => "נקלט", "attention" => "דורש טיפול", "partial" => "משוב חלקי", _ => "ממתין למשוב" },
                feedbackErrors = actionableErrors,
                feedbackErrorSummary = new
                {
                    deposit = errorSummary.GetValueOrDefault("deposit"),
                    employee = errorSummary.GetValueOrDefault("employee"),
                    money = errorSummary.GetValueOrDefault("money"),
                    report = errorSummary.GetValueOrDefault("report"),
                    contribution = errorSummary.GetValueOrDefault("contribution")
                },
                moneyStatus = moneyState,
                moneyStatusLabel = moneyState switch { "allocated" => "שויך במלואו", "in-transit" => "כספים במעבר", "received-partial" => "נקלט חלקית", "unresolved" => "טרם שויך", _ => "אין משוב כספי" },
                treatmentStatus = treatment?.StatusCode ?? "",
                treatmentStatusLabel = treatment is null ? "" : labels.GetValueOrDefault(treatment.StatusCode) ?? treatment.StatusCode,
                updatedAt = timestamps.Count == 0 ? (DateTimeOffset?)null : timestamps.Max(),
                requiresAttention = hasError,
                pendingCorrectionReportId = correctionWorkspace?.Id,
                pendingCorrectionProductId = pendingBySourceProduct.TryGetValue(x.Product.Id, out var pending)
                    ? pending.ProductId : (Guid?)null,
                hasPendingCorrection = correctionWorkspace is not null
                    && (!pendingBySourceProduct.TryGetValue(x.Product.Id, out var correctionProduct)
                        || correctionProduct.Changed)
            };
        });
        return Results.Ok(new { items, hasMore, manufacturers });
    }

    private static async Task<IResult> EmployerResolutionContextAsync(
        Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();

        var reportIds = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var rows = (await ActiveActionableFeedbackAsync(reportIds, db, ct))
            .Where(x => EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money)
            .ToArray();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);

        return Results.Ok(await BuildResolutionContextAsync(
            "employer", organizationId, employerId, null, null, canCreateReport, canEditEmployee, rows, db, protector, ct));
    }

    private static async Task<IResult> ReportResolutionContextAsync(
        Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (!exists) return Results.NotFound();

        var rows = (await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct))
            .Where(x => EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report)
            .ToArray();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);

        return Results.Ok(await BuildResolutionContextAsync(
            "report", organizationId, employerId, reportId, null, canCreateReport, canEditEmployee, rows, db, protector, ct));
    }

    private static async Task<IResult> DepositResolutionContextAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var belongs = await (from product in db.ManualReportProducts.AsNoTracking()
                             join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                             where product.Id == reportProductId && employee.ReportId == reportId
                                 && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                             select product.Id).AnyAsync(ct);
        if (!belongs) return Results.NotFound();

        var rows = (await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct))
            .Where(x => x.ReportProductId == reportProductId)
            .Where(x =>
            {
                var scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode);
                return scope is EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Employee
                    or EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Deposit
                    or EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Contribution;
            })
            .ToArray();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);

        return Results.Ok(await BuildResolutionContextAsync(
            "deposit", organizationId, employerId, reportId, reportProductId, canCreateReport, canEditEmployee, rows, db, protector, ct));
    }

    private static async Task<IResult> ValidateEmployeeResolutionActionAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        Guid reportProductId,
        EmployeeResolutionActionRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        IDataProtectionService protector,
        CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);

        var belongs = await (from product in db.ManualReportProducts.AsNoTracking()
                             join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                             where product.Id == reportProductId && employee.ReportId == reportId
                                 && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                             select product.Id).AnyAsync(ct);
        if (!belongs) return Results.NotFound();

        var rows = (await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct))
            .Where(x => x.ReportProductId == reportProductId)
            .Where(x =>
            {
                var scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode);
                return scope is EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Employee
                    or EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Deposit
                    or EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Contribution;
            })
            .ToArray();

        var context = await BuildResolutionContextAsync(
            "deposit", organizationId, employerId, reportId, reportProductId,
            canCreateReport, canEditEmployee: true, rows, db, protector, ct);

        if (context.UnsupportedCodes.Count > 0)
            return Results.Conflict(new { error = "resolution_context_unsupported" });

        var resolutionGroup = context.Groups.SingleOrDefault(group =>
            string.Equals(group.GroupKey, request.GroupKey, StringComparison.Ordinal));
        if (resolutionGroup is null)
            return Results.Conflict(new { error = "resolution_group_stale" });

        if (!FeedbackResolutionWireProjection.CanExecuteEmployeeEdit(resolutionGroup, request.EmploymentId))
            return Results.BadRequest(new { error = "resolution_action_not_allowed" });

        var requiresCorrection = resolutionGroup.Problems.Any(problem =>
            string.Equals(problem.ResolutionType, "edit", StringComparison.Ordinal)
            && string.Equals(problem.CorrectionBehavior, "correctionWorkspace", StringComparison.Ordinal)
            && problem.AvailableActions.Contains("prepareCorrection", StringComparer.Ordinal));
        if (requiresCorrection && !canCreateReport)
            return Results.Forbid();

        return Results.Ok(new { resolutionGroup.GroupKey, request.EmploymentId, requiresCorrection });
    }

    private static async Task<IResult> PrepareInternalResolutionActionAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        InternalResolutionActionRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        IDataProtectionService protector,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();

        var sourceExists = await db.ManualReports.AsNoTracking().AnyAsync(report =>
            report.Id == reportId
            && report.OrganizationId == organizationId
            && report.EmployerId == employerId
            && !report.IsCorrectionWorkspace
            && !report.IsTechnicalCorrectionDocument, ct);
        if (!sourceExists) return Results.NotFound();

        var rows = (await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct)).ToArray();
        var context = await BuildResolutionContextAsync(
            "report", organizationId, employerId, reportId, request.ReportProductId,
            canCreateReport: true, canEditEmployee: false, rows, db, protector, ct);

        if (context.UnsupportedCodes.Count > 0)
            return Results.Conflict(new { error = "resolution_context_unsupported" });

        var resolutionGroup = context.Groups.SingleOrDefault(group =>
            string.Equals(group.GroupKey, request.GroupKey, StringComparison.Ordinal));
        if (resolutionGroup is null)
            return Results.Conflict(new { error = "resolution_group_stale" });

        var selectedProblems = request.ProblemIds is { Count: > 0 }
            ? resolutionGroup.Problems
                .Where(problem => request.ProblemIds.Contains(problem.ProblemId, StringComparer.Ordinal))
                .ToArray()
            : resolutionGroup.Problems.ToArray();
        if (request.ProblemIds is { Count: > 0 }
            && selectedProblems.Length != request.ProblemIds.Distinct(StringComparer.Ordinal).Count())
            return Results.BadRequest(new { error = "resolution_problem_mismatch" });

        var selectedGroup = resolutionGroup with { Problems = selectedProblems };
        if (!FeedbackResolutionWireProjection.CanPrepareInternalCorrection(
                selectedGroup, request.ResolverType))
            return Results.BadRequest(new { error = "resolution_action_not_allowed" });

        var groupProductIds = selectedGroup.Problems
            .Where(problem => problem.ReportProductId.HasValue)
            .Select(problem => problem.ReportProductId!.Value)
            .Distinct()
            .ToArray();

        Guid? sourceProductId = null;
        if (request.ReportProductId.HasValue)
        {
            if (groupProductIds.Length > 0 && !groupProductIds.Contains(request.ReportProductId.Value))
                return Results.BadRequest(new { error = "resolution_target_mismatch" });
            sourceProductId = request.ReportProductId.Value;
        }
        else if (groupProductIds.Length == 1)
        {
            sourceProductId = groupProductIds[0];
        }
        else if (groupProductIds.Length > 1)
        {
            return Results.BadRequest(new { error = "resolution_target_ambiguous" });
        }

        CorrectionWorkflowService.WorkspaceResult? workspace;
        try
        {
            workspace = await CorrectionWorkflowService.EnsureWorkspaceAsync(
                organizationId, employerId, reportId, sourceProductId, db, protector, ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "correction_workspace_conflict" });
        }

        if (workspace is null)
            return Results.Conflict(new { error = "correction_workspace_source_invalid" });

        var existingCorrectionLinks = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
            .Where(link => selectedProblems.Select(problem => problem.ProblemId).Contains(link.ProblemId))
            .Select(link => link.ProblemId)
            .ToListAsync(ct);
        var existingCorrectionLinkSet = existingCorrectionLinks.ToHashSet(StringComparer.Ordinal);
        foreach (var selectedProblem in selectedProblems)
        {
            if (existingCorrectionLinkSet.Contains(selectedProblem.ProblemId)) continue;
            db.FeedbackCorrectionResolutionLinks.Add(new FeedbackCorrectionResolutionLink(
                selectedProblem.ProblemId,
                reportId,
                workspace.ReportId,
                selectedProblem.ReportProductId,
                selectedProblem.ResolverType,
                currentUser.UserId));
        }

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.correction-workspace-prepared",
            nameof(ManualReport),
            workspace.ReportId,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceReportId = reportId,
                sourceReportProductId = sourceProductId,
                request.GroupKey,
                request.ResolverType,
                workspace.Created,
                workspace.PendingChanges
            }),
            http.TraceIdentifier));
        await db.SaveChangesAsync(ct);

        Guid? workspaceReportEmployeeId = null;
        if (workspace.ReportProductId.HasValue)
        {
            workspaceReportEmployeeId = await db.ManualReportProducts.AsNoTracking()
                .Where(product => product.Id == workspace.ReportProductId.Value)
                .Select(product => (Guid?)product.ReportEmployeeId)
                .SingleOrDefaultAsync(ct);
        }

        return Results.Ok(new
        {
            workspaceReportId = workspace.ReportId,
            workspaceReportProductId = workspace.ReportProductId,
            workspaceReportEmployeeId,
            workspace.Created,
            workspace.PendingChanges,
            resolutionGroup.GroupKey,
            resolutionGroup.ResolverType
        });
    }

    private static async Task<IResult> DecideProblemAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        DecideProblemRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        IDataProtectionService protector,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProblemId))
            return Results.BadRequest(new { error = "problem_id_required" });
        if ((request.Note?.Length ?? 0) > 4000)
            return Results.BadRequest(new { error = "decision_note_too_long" });
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct))
            return Results.Forbid();

        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);
        if (!canCreateReport && !canEditEmployee)
            return Results.Forbid();

        var sourceExists = await db.ManualReports.AsNoTracking().AnyAsync(report =>
            report.Id == reportId
            && report.OrganizationId == organizationId
            && report.EmployerId == employerId
            && !report.IsCorrectionWorkspace
            && !report.IsTechnicalCorrectionDocument, ct);
        if (!sourceExists) return Results.NotFound();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var row = activeRows.FirstOrDefault(item =>
            item.ErrorCode.HasValue
            && string.Equals(
                FeedbackResolutionWireProjection.BuildProblemId(
                    item.FeedbackId, item.ContributionId, item.Sequence, item.ErrorCode.Value),
                request.ProblemId,
                StringComparison.Ordinal));
        if (row is null) return Results.Conflict(new { error = "resolution_problem_stale" });
        if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
            || playbook.ResolutionType != FeedbackResolutionType.Decision
            || !playbook.Actions.HasFlag(FeedbackResolutionAction.Review))
            return Results.BadRequest(new { error = "decision_not_allowed" });

        // A duplicate transfer identifier is not evidence of bad payment fields.
        // Require the operator to record why a correction is needed before
        // preparing a new 006 revision; blind resubmission can duplicate funds.
        if (row.ErrorCode == 50 && string.Equals(request.Outcome?.Trim(),
                "correction", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.Note))
            return Results.BadRequest(new { error = "duplicate_transfer_investigation_note_required" });

        var outcome = request.Outcome?.Trim().ToLowerInvariant() ?? string.Empty;
        if (outcome == "external")
            return Results.BadRequest(new { error = "external_case_required" });
        if (outcome == "reconcile")
            return Results.BadRequest(new { error = "reconciliation_case_required" });
        if (outcome == "link-original")
            return Results.BadRequest(new { error = "original_movement_link_required" });
        var needsCreateReport = outcome is "confirm" or "correction" or "reconcile" or "link-original";
        if (needsCreateReport && !canCreateReport)
            return Results.Forbid();

        var allowed = outcome switch
        {
            "confirm" => playbook.Actions.HasFlag(FeedbackResolutionAction.Confirm),
            "correction" => playbook.Actions.HasFlag(FeedbackResolutionAction.PrepareCorrection),
            "external" => playbook.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase),
            "reconcile" => playbook.Actions.HasFlag(FeedbackResolutionAction.Reconcile),
            "link-original" => playbook.Actions.HasFlag(FeedbackResolutionAction.LinkOriginalRecord),
            _ => false
        };
        if (!allowed) return Results.BadRequest(new { error = "decision_outcome_not_allowed" });
        if (outcome is "external" or "reconcile" or "link-original"
            && string.IsNullOrWhiteSpace(request.Note))
            return Results.BadRequest(new { error = "decision_note_required" });

        CorrectionWorkflowService.WorkspaceResult? workspace = null;
        if (outcome == "correction")
        {
            try
            {
                workspace = await CorrectionWorkflowService.EnsureWorkspaceAsync(
                    organizationId, employerId, reportId, row.ReportProductId, db, protector, ct);
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "correction_workspace_conflict" });
            }

            if (workspace is null)
                return Results.Conflict(new { error = "correction_workspace_source_invalid" });

            var correctionLinkExists = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
                .AnyAsync(link => link.ProblemId == request.ProblemId, ct);
            if (!correctionLinkExists)
            {
                db.FeedbackCorrectionResolutionLinks.Add(new FeedbackCorrectionResolutionLink(
                    request.ProblemId,
                    reportId,
                    workspace.ReportId,
                    row.ReportProductId,
                    FeedbackResolutionWireProjection.WireName(playbook.Resolver),
                    currentUser.UserId));
            }
        }

        var decision = new FeedbackProblemDecision(
            request.ProblemId,
            row.FeedbackId,
            row.ReportId,
            row.ReportProductId,
            row.ContributionId,
            row.ErrorCode.Value,
            outcome,
            request.Note,
            currentUser.UserId);
        db.FeedbackProblemDecisions.Add(decision);

        if (outcome == "confirm")
        {
            db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
                request.ProblemId,
                row.FeedbackId,
                row.ReportId,
                row.ReportProductId,
                row.ContributionId,
                row.ErrorCode.Value,
                "decision-confirm",
                currentUser.UserId));
        }

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.decision",
            nameof(EmployerInterfaceContributionFeedback),
            row.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                request.ProblemId,
                errorCode = row.ErrorCode.Value,
                outcome,
                hasNote = !string.IsNullOrWhiteSpace(request.Note),
                workspaceReportId = workspace?.ReportId,
                workspaceReportProductId = workspace?.ReportProductId
            }),
            http.TraceIdentifier));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "decision_conflict" });
        }

        Guid? workspaceReportEmployeeId = null;
        if (workspace?.ReportProductId is Guid workspaceProductId)
        {
            workspaceReportEmployeeId = await db.ManualReportProducts.AsNoTracking()
                .Where(product => product.Id == workspaceProductId)
                .Select(product => (Guid?)product.ReportEmployeeId)
                .SingleOrDefaultAsync(ct);
        }

        return Results.Ok(new
        {
            decisionId = decision.Id,
            problemId = request.ProblemId,
            outcome,
            resolved = outcome == "confirm",
            workspaceReportId = workspace?.ReportId,
            workspaceReportProductId = workspace?.ReportProductId,
            workspaceReportEmployeeId
        });
    }

    private static async Task<IResult> OriginalMovementCandidatesAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        string problemId,
        IAlphaDbContext db,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var row = activeRows.FirstOrDefault(item => item.ErrorCode.HasValue
            && string.Equals(
                FeedbackResolutionWireProjection.BuildProblemId(
                    item.FeedbackId, item.ContributionId, item.Sequence, item.ErrorCode.Value),
                problemId,
                StringComparison.Ordinal));
        if (row is null) return Results.Conflict(new { error = "resolution_problem_stale" });
        if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
            || !playbook.Actions.HasFlag(FeedbackResolutionAction.LinkOriginalRecord))
            return Results.BadRequest(new { error = "link_original_not_allowed" });

        var sourceContribution = await db.ManualContributions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.ContributionId, ct);
        var sourceProduct = await db.ManualReportProducts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.ReportProductId, ct);
        if (sourceContribution is null || sourceProduct is null) return Results.NotFound();

        var sourceEmployee = await db.ManualReportEmployees.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sourceProduct.ReportEmployeeId
                && item.ReportId == reportId
                && item.OrganizationId == organizationId
                && item.EmployerId == employerId, ct);
        if (sourceEmployee is null) return Results.NotFound();

        var immutableReportIds = await db.ManualReports.AsNoTracking()
            .Where(report => report.OrganizationId == organizationId
                && report.EmployerId == employerId
                && report.Id != reportId
                && !report.IsCorrectionWorkspace
                && !report.IsTechnicalCorrectionDocument
                && (report.Status == ManualReportStatus.Sent
                    || report.Status == ManualReportStatus.Completed))
            .Select(report => report.Id)
            .ToArrayAsync(ct);

        var employeeRows = await db.ManualReportEmployees.AsNoTracking()
            .Where(employee => immutableReportIds.Contains(employee.ReportId)
                && employee.PersonId == sourceEmployee.PersonId)
            .ToListAsync(ct);
        var employeeIds = employeeRows.Select(employee => employee.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(product => employeeIds.Contains(product.ReportEmployeeId)
                && product.ProductType == sourceProduct.ProductType)
            .ToListAsync(ct);
        products = products.Where(product =>
        {
            var policyMatches = string.IsNullOrWhiteSpace(sourceProduct.PolicyNumber)
                ? string.IsNullOrWhiteSpace(product.PolicyNumber)
                : string.Equals(product.PolicyNumber, sourceProduct.PolicyNumber, StringComparison.OrdinalIgnoreCase);
            var fundMatches = !string.IsNullOrWhiteSpace(sourceProduct.FundCode)
                ? string.Equals(product.FundCode, sourceProduct.FundCode, StringComparison.OrdinalIgnoreCase)
                : string.Equals(product.FundExternalKey, sourceProduct.FundExternalKey, StringComparison.OrdinalIgnoreCase);
            return policyMatches && fundMatches;
        }).ToList();

        var productIds = products.Select(product => product.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(item => productIds.Contains(item.ReportProductId)
                && item.Party == sourceContribution.Party
                && item.Component == sourceContribution.Component)
            .ToListAsync(ct);
        var reportByEmployeeId = employeeRows.ToDictionary(employee => employee.Id, employee => employee.ReportId);
        var reportIds = reportByEmployeeId.Values.Distinct().ToArray();
        var reports = await db.ManualReports.AsNoTracking()
            .Where(report => reportIds.Contains(report.Id))
            .ToDictionaryAsync(report => report.Id, ct);
        var productById = products.ToDictionary(product => product.Id);
        var employeeById = employeeRows.ToDictionary(employee => employee.Id);

        var candidates = contributions
            .Where(item => productById.ContainsKey(item.ReportProductId))
            .Select(item =>
            {
                var product = productById[item.ReportProductId];
                var employee = employeeById[product.ReportEmployeeId];
                var report = reports[employee.ReportId];
                var identifier = string.IsNullOrWhiteSpace(item.InterfaceRecordIdentifier)
                    ? item.Id.ToString("D").ToUpperInvariant()
                    : item.InterfaceRecordIdentifier;
                return new
                {
                    contributionId = item.Id,
                    recordIdentifier = identifier,
                    reportId = report.Id,
                    reportingMonth = report.ReportingMonth,
                    salaryMonth = product.SalaryMonth,
                    amount = item.Amount,
                    percentage = item.Percentage,
                    productName = product.FundName,
                    policyNumber = product.PolicyNumber
                };
            })
            .OrderByDescending(item => item.reportingMonth)
            .ThenByDescending(item => item.salaryMonth)
            .Take(100)
            .ToArray();

        return Results.Ok(new { items = candidates });
    }

    private static async Task<IResult> LinkOriginalMovementAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        string problemId,
        LinkOriginalMovementRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        EmployerInterface006ExportService employerInterfaceExporter,
        IDataProtectionService protector,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var row = activeRows.FirstOrDefault(item => item.ErrorCode.HasValue
            && string.Equals(
                FeedbackResolutionWireProjection.BuildProblemId(
                    item.FeedbackId, item.ContributionId, item.Sequence, item.ErrorCode.Value),
                problemId,
                StringComparison.Ordinal));
        if (row is null) return Results.Conflict(new { error = "resolution_problem_stale" });
        if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
            || playbook.ResolutionType != FeedbackResolutionType.Decision
            || !playbook.Actions.HasFlag(FeedbackResolutionAction.LinkOriginalRecord))
            return Results.BadRequest(new { error = "link_original_not_allowed" });

        var sourceContribution = await db.ManualContributions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.ContributionId, ct);
        var sourceProduct = await db.ManualReportProducts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == row.ReportProductId, ct);
        if (sourceContribution is null || sourceProduct is null) return Results.NotFound();
        var sourceEmployee = await db.ManualReportEmployees.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sourceProduct.ReportEmployeeId
                && item.ReportId == reportId
                && item.OrganizationId == organizationId
                && item.EmployerId == employerId, ct);
        if (sourceEmployee is null) return Results.NotFound();

        var candidate = await db.ManualContributions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.ContributionId, ct);
        if (candidate is null) return Results.BadRequest(new { error = "original_movement_not_found" });
        var candidateProduct = await db.ManualReportProducts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidate.ReportProductId, ct);
        if (candidateProduct is null) return Results.BadRequest(new { error = "original_movement_not_found" });
        var candidateEmployee = await db.ManualReportEmployees.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidateProduct.ReportEmployeeId
                && item.OrganizationId == organizationId
                && item.EmployerId == employerId
                && item.PersonId == sourceEmployee.PersonId, ct);
        if (candidateEmployee is null) return Results.BadRequest(new { error = "original_movement_owner_mismatch" });
        var candidateReport = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidateEmployee.ReportId
                && item.OrganizationId == organizationId
                && item.EmployerId == employerId
                && item.Id != reportId
                && !item.IsCorrectionWorkspace
                && !item.IsTechnicalCorrectionDocument
                && (item.Status == ManualReportStatus.Sent || item.Status == ManualReportStatus.Completed), ct);
        if (candidateReport is null) return Results.BadRequest(new { error = "original_movement_report_invalid" });

        var productMatches = candidateProduct.ProductType == sourceProduct.ProductType
            && (string.IsNullOrWhiteSpace(sourceProduct.PolicyNumber)
                ? string.IsNullOrWhiteSpace(candidateProduct.PolicyNumber)
                : string.Equals(candidateProduct.PolicyNumber, sourceProduct.PolicyNumber, StringComparison.OrdinalIgnoreCase))
            && (!string.IsNullOrWhiteSpace(sourceProduct.FundCode)
                ? string.Equals(candidateProduct.FundCode, sourceProduct.FundCode, StringComparison.OrdinalIgnoreCase)
                : string.Equals(candidateProduct.FundExternalKey, sourceProduct.FundExternalKey, StringComparison.OrdinalIgnoreCase));
        if (!productMatches
            || candidate.Party != sourceContribution.Party
            || candidate.Component != sourceContribution.Component)
            return Results.BadRequest(new { error = "original_movement_product_mismatch" });

        var recordIdentifier = string.IsNullOrWhiteSpace(candidate.InterfaceRecordIdentifier)
            ? candidate.Id.ToString("D").ToUpperInvariant()
            : candidate.InterfaceRecordIdentifier;

        CorrectionWorkflowService.WorkspaceResult? workspace;
        try
        {
            workspace = await CorrectionWorkflowService.EnsureWorkspaceAsync(
                organizationId, employerId, reportId, row.ReportProductId, db, protector, ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "correction_workspace_conflict" });
        }
        if (workspace?.ReportProductId is null)
            return Results.Conflict(new { error = "correction_workspace_source_invalid" });

        var workspaceContribution = await db.ManualContributions.SingleOrDefaultAsync(item =>
            item.ReportProductId == workspace.ReportProductId.Value
            && item.Party == sourceContribution.Party
            && item.Component == sourceContribution.Component, ct);
        var workspaceReport = await db.ManualReports.SingleOrDefaultAsync(item => item.Id == workspace.ReportId, ct);
        if (workspaceContribution is null || workspaceReport is null)
            return Results.Conflict(new { error = "correction_workspace_target_missing" });

        workspaceContribution.SetPreviousRecordIdentifier(recordIdentifier);
        workspaceReport.MarkDirty();

        var linkExists = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
            .AnyAsync(link => link.ProblemId == problemId, ct);
        if (!linkExists)
            db.FeedbackCorrectionResolutionLinks.Add(new FeedbackCorrectionResolutionLink(
                problemId, reportId, workspace.ReportId, row.ReportProductId,
                FeedbackResolutionWireProjection.WireName(playbook.Resolver), currentUser.UserId));

        db.FeedbackProblemDecisions.Add(new FeedbackProblemDecision(
            problemId, row.FeedbackId, row.ReportId, row.ReportProductId, row.ContributionId,
            row.ErrorCode.Value, "link-original", $"קושר לתנועה {recordIdentifier}", currentUser.UserId));
        await db.SaveChangesAsync(ct);

        var validation = await ReportValidationEndpoints.ValidateForFeedbackResolutionAsync(
            organizationId, employerId, workspace.ReportId, "deposits",
            db, employerInterfaceExporter, protector, ct);
        if (validation is null) return Results.NotFound();
        if (!validation.IsValid)
            return Results.Conflict(new
            {
                error = "resolution_revalidation_failed",
                validationCodes = validation.Codes,
                validationErrors = validation.Errors
            });

        if (await CorrectionWorkflowService.PendingChangeCountAsync(workspace.ReportId, db, protector, ct) <= 0)
            return Results.Conflict(new { error = "resolution_no_correction_change" });

        db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
            problemId, row.FeedbackId, row.ReportId, row.ReportProductId, row.ContributionId,
            row.ErrorCode.Value, "link-original", currentUser.UserId));
        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.original-movement-linked",
            nameof(EmployerInterfaceContributionFeedback),
            row.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                problemId,
                recordIdentifier,
                candidateReportId = candidateReport.Id,
                workspaceReportId = workspace.ReportId
            }),
            http.TraceIdentifier));
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            resolvedProblemId = problemId,
            recordIdentifier,
            workspaceReportId = workspace.ReportId,
            workspaceReportProductId = workspace.ReportProductId
        });
    }

    private static async Task<IResult> OpenExternalCaseAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        OpenExternalCaseRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        IDataProtectionService protector,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.ProblemIds is null || request.ProblemIds.Count == 0 || string.IsNullOrWhiteSpace(request.GroupKey))
            return Results.BadRequest(new { error = "external_case_targets_required" });
        var caseAction = string.Equals(request.Action, "reconcile", StringComparison.OrdinalIgnoreCase)
            ? "reconcile"
            : "external";

        var rows = (await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct)).ToArray();
        var context = await BuildResolutionContextAsync(
            "report", organizationId, employerId, reportId, null,
            canCreateReport: true, canEditEmployee: false, rows, db, protector, ct);
        var group = context.Groups.SingleOrDefault(item =>
            string.Equals(item.GroupKey, request.GroupKey, StringComparison.Ordinal));
        if (group is null) return Results.Conflict(new { error = "resolution_group_stale" });

        var selected = group.Problems
            .Where(problem => request.ProblemIds.Contains(problem.ProblemId, StringComparer.Ordinal))
            .ToArray();
        if (selected.Length != request.ProblemIds.Distinct(StringComparer.Ordinal).Count())
            return Results.BadRequest(new { error = "resolution_problem_mismatch" });
        var requiredAction = caseAction == "reconcile" ? "reconcile" : "openExternalCase";
        if (selected.Any(problem => !problem.AvailableActions.Contains(requiredAction, StringComparer.Ordinal)))
            return Results.BadRequest(new { error = "external_case_not_allowed" });

        var caseKey = request.GroupKey.Trim();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.EmployerId == employerId && item.CaseKey == caseKey, ct);

        var created = false;
        if (externalCase is null)
        {
            var first = selected[0];
            var destination = caseAction == "reconcile"
                ? "התאמת כספים / גבייה"
                : ExternalCaseDestination(first.Family);
            var subject = caseAction == "reconcile"
                ? $"ALPHA - התאמת כספים למשוב קוד {first.Code}"
                : $"ALPHA - בירור משוב מסלקה קוד {first.Code}";
            var template = BuildExternalCaseTemplate(first);
            externalCase = new FeedbackExternalCase(
                organizationId, employerId, caseKey, destination, subject, template, currentUser.UserId);
            db.FeedbackExternalCases.Add(externalCase);
            db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                externalCase.Id, "created", request.Note, currentUser.UserId));
            created = true;
        }
        else if (!string.IsNullOrWhiteSpace(request.Note))
        {
            db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                externalCase.Id, "note", request.Note, currentUser.UserId));
        }

        var existingProblemIds = await db.FeedbackExternalCaseProblems.AsNoTracking()
            .Where(item => item.CaseId == externalCase.Id)
            .Select(item => item.ProblemId)
            .ToListAsync(ct);
        var existingSet = existingProblemIds.ToHashSet(StringComparer.Ordinal);

        var activeById = rows
            .Where(row => row.ErrorCode.HasValue)
            .ToDictionary(
                row => FeedbackResolutionWireProjection.BuildProblemId(
                    row.FeedbackId, row.ContributionId, row.Sequence, row.ErrorCode!.Value),
                row => row,
                StringComparer.Ordinal);

        foreach (var problem in selected)
        {
            if (!existingSet.Contains(problem.ProblemId)
                && activeById.TryGetValue(problem.ProblemId, out var row))
            {
                db.FeedbackExternalCaseProblems.Add(new FeedbackExternalCaseProblem(
                    externalCase.Id, problem.ProblemId, row.FeedbackId, row.ReportId,
                    row.ReportProductId, row.ContributionId, row.ErrorCode!.Value));
                db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                    externalCase.Id, "problem-linked", $"קוד {problem.Code}", currentUser.UserId));
            }

            if (string.Equals(problem.ResolutionType, "decision", StringComparison.Ordinal)
                && !string.Equals(problem.LatestDecision, caseAction, StringComparison.Ordinal))
            {
                var decisionRow = activeById[problem.ProblemId];
                db.FeedbackProblemDecisions.Add(new FeedbackProblemDecision(
                    problem.ProblemId, decisionRow.FeedbackId, decisionRow.ReportId, decisionRow.ReportProductId,
                    decisionRow.ContributionId, decisionRow.ErrorCode!.Value, caseAction, request.Note, currentUser.UserId));
            }
        }

        if (externalCase.Status == "resolved")
            externalCase.SetStatus("open");

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.external-case-opened",
            nameof(FeedbackExternalCase),
            externalCase.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                externalCase.Id,
                caseKey,
                created,
                problemIds = selected.Select(item => item.ProblemId).ToArray(),
                action = caseAction
            }),
            http.TraceIdentifier));

        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(externalCase.Id, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> ExternalCaseDetailsAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        IAlphaDbContext db,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.FeedbackExternalCases.AsNoTracking().AnyAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (!exists) return Results.NotFound();
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> AddExternalCaseEventAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseEventRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Note))
            return Results.BadRequest(new { error = "case_note_required" });
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "note", request.Note, currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> AssignExternalCaseAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseAssignmentRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();

        var assignee = request.AssignToMe ? currentUser.UserId : (Guid?)null;
        externalCase.Assign(assignee);
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, assignee.HasValue ? "assigned" : "unassigned",
            assignee.HasValue ? "התיק נלקח לטיפול." : "השיוך הוסר.", currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> UpdateExternalCaseStatusAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseStatusRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();

        var normalized = request.Status?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is not ("open" or "waiting" or "resolved"))
            return Results.BadRequest(new { error = "external_case_status_invalid" });

        externalCase.SetStatus(normalized);
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "status", request.Note, currentUser.UserId));

        if (normalized == "resolved")
        {
            var links = await db.FeedbackExternalCaseProblems.AsNoTracking()
                .Where(item => item.CaseId == caseId)
                .ToListAsync(ct);
            var reportIds = links.Select(item => item.ReportId).Distinct().ToArray();
            var activeRows = await ActiveActionableFeedbackAsync(reportIds, db, ct);
            var activeByProblemId = activeRows
                .Where(row => row.ErrorCode.HasValue)
                .ToDictionary(
                    row => FeedbackResolutionWireProjection.BuildProblemId(
                        row.FeedbackId, row.ContributionId, row.Sequence, row.ErrorCode!.Value),
                    row => row,
                    StringComparer.Ordinal);
            var alreadyResolved = await db.FeedbackProblemResolutions.AsNoTracking()
                .Where(item => links.Select(link => link.ProblemId).Contains(item.ProblemId))
                .Select(item => item.ProblemId)
                .ToListAsync(ct);
            var resolvedSet = alreadyResolved.ToHashSet(StringComparer.Ordinal);

            foreach (var link in links)
            {
                if (resolvedSet.Contains(link.ProblemId)
                    || !activeByProblemId.TryGetValue(link.ProblemId, out var row)
                    || !FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
                    || (!playbook.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase)
                        && !playbook.Actions.HasFlag(FeedbackResolutionAction.Reconcile)))
                    continue;

                db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
                    link.ProblemId, row.FeedbackId, row.ReportId, row.ReportProductId,
                    row.ContributionId, row.ErrorCode.Value, "external-case-resolved", currentUser.UserId));
            }
        }

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.external-case-status",
            nameof(FeedbackExternalCase),
            externalCase.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new { status = normalized }),
            http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> UpdateExternalCaseTemplateAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseTemplateRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();

        try
        {
            externalCase.UpdateTemplate(request.Destination, request.Subject, request.MessageTemplate);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "external_case_template_invalid" });
        }

        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "template-updated", "תבנית הפנייה עודכנה.", currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> UploadExternalCaseAttachmentAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        HttpRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        ICurrentUser currentUser,
        IMalwareScanner scanner,
        IDataProtectionService protector,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });

        var file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        var maxBytes = Math.Min(10L * 1024 * 1024,
            configuration.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? 10L * 1024 * 1024);
        if (file is null || file.Length == 0 || file.Length > maxBytes)
            return Results.BadRequest(new { error = "case_attachment_size_invalid" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
            return Results.BadRequest(new { error = "case_attachment_type_invalid" });

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        var validSignature = ext switch
        {
            ".pdf" => bytes.Length >= 5 && bytes.AsSpan().StartsWith("%PDF-"u8),
            ".png" => bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A,
            ".jpg" or ".jpeg" => bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            _ => false
        };
        if (!validSignature)
            return Results.BadRequest(new { error = "case_attachment_signature_invalid" });

        await using var scanStream = new MemoryStream(bytes, writable: false);
        var scan = await scanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (scan.Verdict == MalwareScanVerdict.Infected)
            return Results.BadRequest(new { error = "case_attachment_security_failed" });

        var encrypted = protector.ProtectBytes(bytes, $"feedback-external-case-attachment:{caseId:N}");
        var attachment = new FeedbackExternalCaseAttachment(
            caseId, file.FileName, file.ContentType, encrypted, bytes.LongLength,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            currentUser.UserId);
        db.FeedbackExternalCaseAttachments.Add(attachment);
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "attachment", attachment.OriginalFileName, currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> DownloadExternalCaseAttachmentAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        Guid attachmentId,
        IAlphaDbContext db,
        OrganizationAccessService access,
        IDataProtectionService protector,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var belongs = await db.FeedbackExternalCases.AsNoTracking().AnyAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (!belongs) return Results.NotFound();
        var attachment = await db.FeedbackExternalCaseAttachments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == attachmentId && item.CaseId == caseId, ct);
        if (attachment is null) return Results.NotFound();

        var bytes = protector.UnprotectBytes(
            attachment.Content, $"feedback-external-case-attachment:{caseId:N}");
        if (!string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                attachment.Sha256,
                StringComparison.Ordinal))
            return Results.Problem("Attachment integrity check failed.", statusCode: 500);
        return Results.File(bytes, attachment.ContentType, attachment.OriginalFileName);
    }

    private static async Task<object> ExternalCaseResponseAsync(
        Guid caseId,
        Guid organizationId,
        Guid employerId,
        IAlphaDbContext db,
        CancellationToken ct)
    {
        var externalCase = await db.FeedbackExternalCases.AsNoTracking()
            .SingleAsync(item => item.Id == caseId
                && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        var assigneeName = externalCase.AssignedToUserId.HasValue
            ? await db.Users.AsNoTracking()
                .Where(user => user.Id == externalCase.AssignedToUserId.Value)
                .Select(user => user.DisplayName)
                .SingleOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        var problemRows = await db.FeedbackExternalCaseProblems.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(ct);
        var problems = problemRows.Select(item => new
        {
            item.ProblemId,
            item.ReportId,
            item.ReportProductId,
            item.ErrorCode,
            description = EmployerInterfaceLineFeedbackParser.Description(item.ErrorCode),
            item.CreatedAt
        }).ToArray();
        var events = await db.FeedbackExternalCaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(ct);
        var actorIds = events.Select(item => item.ActorUserId).Distinct().ToArray();
        var actors = await db.Users.AsNoTracking()
            .Where(user => actorIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, ct);
        var attachments = await db.FeedbackExternalCaseAttachments.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.OriginalFileName,
                item.ContentType,
                item.SizeBytes,
                item.Sha256,
                item.CreatedAt
            })
            .ToListAsync(ct);

        return new
        {
            externalCase.Id,
            externalCase.CaseKey,
            externalCase.Status,
            externalCase.Destination,
            externalCase.Subject,
            externalCase.MessageTemplate,
            externalCase.AssignedToUserId,
            assigneeName,
            externalCase.CreatedAt,
            externalCase.UpdatedAt,
            externalCase.ClosedAt,
            problems,
            events = events.Select(item => new
            {
                item.Id,
                item.EventType,
                item.Note,
                item.ActorUserId,
                actorName = actors.GetValueOrDefault(item.ActorUserId) ?? string.Empty,
                item.CreatedAt
            }),
            attachments
        };
    }

    private static string ExternalCaseDestination(string family) => family switch
    {
        "payment" or "refund" => "מסלקה / גוף מוסדי",
        "externalInstitution" => "גוף מוסדי",
        "documents" => "גוף מוסדי / מסלקה",
        _ => "גוף חיצוני"
    };

    private static string BuildExternalCaseTemplate(FeedbackResolutionProblemDto problem)
    {
        var lines = new List<string>
        {
            "שלום,",
            "",
            $"נבקש את בדיקתכם בנוגע למשוב מסלקה קוד {problem.Code}.",
            problem.Description,
        };
        if (!string.IsNullOrWhiteSpace(problem.EmployeeName))
            lines.Add($"עובד: {problem.EmployeeName}");
        if (!string.IsNullOrWhiteSpace(problem.FundCompanyName) || !string.IsNullOrWhiteSpace(problem.ProductName))
            lines.Add($"מוצר/יצרן: {problem.FundCompanyName} {problem.ProductName}".Trim());
        if (!string.IsNullOrWhiteSpace(problem.PolicyNumber))
            lines.Add($"מספר פוליסה: {problem.PolicyNumber}");
        lines.Add($"דיווח: {problem.ReportId:D}");
        lines.Add("");
        lines.Add("נודה לבדיקה ולעדכון.");
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<IResult> ResolveProblemsAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        ResolveProblemsRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        EmployerInterface006ExportService employerInterfaceExporter,
        IDataProtectionService protector,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (request.ProblemIds.Count == 0) return Results.BadRequest(new { error = "problem_ids_required" });
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var canCreate = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);
        if (!canCreate && !canEditEmployee) return Results.Forbid();

        var sourceExists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (!sourceExists) return Results.NotFound();

        ManualReport? validationTarget = null;
        if (request.ValidatedReportId.HasValue)
        {
            validationTarget = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == request.ValidatedReportId.Value
                && x.OrganizationId == organizationId
                && x.EmployerId == employerId
                && (x.Id == reportId || (x.IsCorrectionWorkspace && x.SourceReportId == reportId)), ct);
            if (validationTarget is null)
                return Results.BadRequest(new { error = "resolution_validation_target_invalid" });
        }

        if (request.Source is "deposit-save" or "workspace-validation")
        {
            if (!request.ValidatedReportId.HasValue)
                return Results.BadRequest(new { error = "resolution_validation_target_required" });

            var validation = await ReportValidationEndpoints.ValidateForFeedbackResolutionAsync(
                organizationId,
                employerId,
                request.ValidatedReportId.Value,
                "deposits",
                db,
                employerInterfaceExporter,
                protector,
                ct);
            if (validation is null) return Results.NotFound();
            if (!validation.IsValid)
                return Results.Conflict(new
                {
                    error = "resolution_revalidation_failed",
                    validationCodes = validation.Codes,
                    validationErrors = validation.Errors
                });

            if (validationTarget?.IsCorrectionWorkspace == true)
            {
                var pendingChanges = await CorrectionWorkflowService.PendingChangeCountAsync(
                    validationTarget.Id, db, protector, ct);
                if (pendingChanges <= 0)
                    return Results.Conflict(new { error = "resolution_no_correction_change" });
            }
        }

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var byProblemId = activeRows
            .Where(x => x.ErrorCode.HasValue)
            .ToDictionary(
                x => FeedbackResolutionWireProjection.BuildProblemId(
                    x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode!.Value),
                x => x,
                StringComparer.Ordinal);

        var requested = request.ProblemIds.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Any(problemId => !byProblemId.ContainsKey(problemId)))
            return Results.Conflict(new { error = "resolution_problem_stale" });

        Guid? employeeWorkspaceId = null;
        if (request.Source == "employee-save")
        {
            if (!canCreate)
                return Results.Forbid();

            var employeeContext = await BuildResolutionContextAsync(
                "report", organizationId, employerId, reportId, null,
                canCreate, canEditEmployee, activeRows, db, protector, ct);
            var employeeProblems = employeeContext.Problems
                .Where(problem => requested.Contains(problem.ProblemId))
                .ToDictionary(problem => problem.ProblemId, StringComparer.Ordinal);

            Guid? focusedProductId = null;
            var employmentIds = new HashSet<Guid>();
            foreach (var problemId in requested)
            {
                if (!employeeProblems.TryGetValue(problemId, out var problem)
                    || problem.EmploymentId is null)
                    return Results.Conflict(new { error = "resolution_problem_stale" });

                var keys = problem.ReportedValues.Keys
                    .Concat(problem.CurrentValues.Keys)
                    .Distinct(StringComparer.Ordinal);
                var changed = keys.Any(key =>
                    !string.Equals(
                        problem.ReportedValues.GetValueOrDefault(key) ?? string.Empty,
                        problem.CurrentValues.GetValueOrDefault(key) ?? string.Empty,
                        StringComparison.Ordinal));
                if (!changed)
                    return Results.Conflict(new { error = "resolution_employee_not_changed" });

                if (!string.Equals(problem.CorrectionBehavior, "correctionWorkspace", StringComparison.Ordinal))
                    return Results.BadRequest(new { error = "resolution_employee_correction_required" });

                focusedProductId ??= problem.ReportProductId;
                employmentIds.Add(problem.EmploymentId.Value);
            }

            CorrectionWorkflowService.WorkspaceResult? workspace;
            try
            {
                workspace = await CorrectionWorkflowService.EnsureWorkspaceAsync(
                    organizationId, employerId, reportId, focusedProductId, db, protector, ct);
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "correction_workspace_conflict" });
            }
            if (workspace is null)
                return Results.Conflict(new { error = "correction_workspace_source_invalid" });

            foreach (var employmentId in employmentIds)
            {
                if (!await CorrectionWorkflowService.SyncEmployeeMasterToWorkspaceAsync(
                        organizationId, employerId, workspace.ReportId, employmentId, db, protector, ct))
                    return Results.Conflict(new { error = "resolution_employee_workspace_sync_failed" });
            }

            var validation = await ReportValidationEndpoints.ValidateForFeedbackResolutionAsync(
                organizationId,
                employerId,
                workspace.ReportId,
                "employees",
                db,
                employerInterfaceExporter,
                protector,
                ct);
            if (validation is null) return Results.NotFound();
            if (!validation.IsValid)
                return Results.Conflict(new
                {
                    error = "resolution_revalidation_failed",
                    validationCodes = validation.Codes,
                    validationErrors = validation.Errors
                });

            var pendingChanges = await CorrectionWorkflowService.PendingChangeCountAsync(
                workspace.ReportId, db, protector, ct);
            if (pendingChanges <= 0)
                return Results.Conflict(new { error = "resolution_no_correction_change" });

            var existingLinks = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
                .Where(link => requested.Contains(link.ProblemId))
                .Select(link => link.ProblemId)
                .ToListAsync(ct);
            var existingLinkSet = existingLinks.ToHashSet(StringComparer.Ordinal);
            foreach (var problemId in requested)
            {
                if (existingLinkSet.Contains(problemId)) continue;
                var problem = employeeProblems[problemId];
                db.FeedbackCorrectionResolutionLinks.Add(new FeedbackCorrectionResolutionLink(
                    problemId,
                    reportId,
                    workspace.ReportId,
                    problem.ReportProductId,
                    problem.ResolverType,
                    currentUser.UserId));
            }

            employeeWorkspaceId = workspace.ReportId;
        }

        var latestDecisionByProblem = await db.FeedbackProblemDecisions.AsNoTracking()
            .Where(decision => requested.Contains(decision.ProblemId))
            .OrderByDescending(decision => decision.DecidedAt)
            .ThenByDescending(decision => decision.CreatedAt)
            .ToListAsync(ct);
        var latestDecisionOutcomes = latestDecisionByProblem
            .GroupBy(decision => decision.ProblemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Outcome, StringComparer.Ordinal);

        if (request.Source == "workspace-validation")
        {
            var workspaceId = request.ValidatedReportId!.Value;
            var linkedProblemIds = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
                .Where(link => link.WorkspaceReportId == workspaceId && requested.Contains(link.ProblemId))
                .Select(link => link.ProblemId)
                .ToListAsync(ct);
            if (linkedProblemIds.Distinct(StringComparer.Ordinal).Count() != requested.Length)
                return Results.Conflict(new { error = "resolution_correction_link_missing" });
        }

        if (request.Source == "deposit-save" && validationTarget?.IsCorrectionWorkspace == true)
        {
            var existingLinks = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
                .Where(link => requested.Contains(link.ProblemId))
                .Select(link => link.ProblemId)
                .ToListAsync(ct);
            var existingLinkSet = existingLinks.ToHashSet(StringComparer.Ordinal);
            foreach (var problemId in requested)
            {
                if (existingLinkSet.Contains(problemId)) continue;
                var row = byProblemId[problemId];
                if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook))
                    return Results.Conflict(new { error = "resolution_context_unsupported" });
                db.FeedbackCorrectionResolutionLinks.Add(new FeedbackCorrectionResolutionLink(
                    problemId,
                    reportId,
                    validationTarget.Id,
                    row.ReportProductId,
                    FeedbackResolutionWireProjection.WireName(playbook.Resolver),
                    currentUser.UserId));
            }
        }

        if (request.Source == "deposit-save")
        {
            // A submitted 006 report is immutable. Correction-backed feedback may only
            // be prepared against a workspace belonging to the original report.
            // A revalidate-only payment exception does not require a new 006 revision.
            var requiresWorkspace = requested.Any(problemId =>
            {
                var row = byProblemId[problemId];
                return FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
                    && playbook.CorrectionBehavior == FeedbackCorrectionBehavior.CorrectionWorkspace;
            });
            if (requiresWorkspace && validationTarget?.IsCorrectionWorkspace != true)
                return Results.Conflict(new { error = "resolution_correction_workspace_required" });
        }

        var existingResolutionProblemIds = await db.FeedbackProblemResolutions.AsNoTracking()
            .Where(item => requested.Contains(item.ProblemId))
            .Select(item => item.ProblemId)
            .ToHashSetAsync(ct);

        foreach (var problemId in requested)
        {
            var row = byProblemId[problemId];
            if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook))
                return Results.Conflict(new { error = "resolution_context_unsupported" });

            var sourceAllowed = request.Source switch
            {
                "employee-save" => playbook.ResolutionType == FeedbackResolutionType.Edit
                    && playbook.Resolver == FeedbackResolverType.Employee
                    && playbook.Actions.HasFlag(FeedbackResolutionAction.EditEmployee),
                "deposit-save" => playbook.ResolutionType == FeedbackResolutionType.Edit
                    && playbook.CorrectionBehavior is FeedbackCorrectionBehavior.CorrectionWorkspace
                        or FeedbackCorrectionBehavior.RevalidateOnly
                    && playbook.Resolver is FeedbackResolverType.Payment
                        or FeedbackResolverType.Contribution
                        or FeedbackResolverType.EmploymentStatus
                        or FeedbackResolverType.ProductPolicy,
                "workspace-validation" => (playbook.ResolutionType == FeedbackResolutionType.Edit
                        && playbook.CorrectionBehavior == FeedbackCorrectionBehavior.CorrectionWorkspace)
                    || (playbook.ResolutionType == FeedbackResolutionType.Decision
                        && playbook.Actions.HasFlag(FeedbackResolutionAction.PrepareCorrection)
                        && latestDecisionOutcomes.TryGetValue(problemId, out var decisionOutcome)
                        && string.Equals(decisionOutcome, "correction", StringComparison.Ordinal)),
                _ => false
            };
            if (!sourceAllowed) return Results.BadRequest(new { error = "resolution_source_not_allowed" });

            // Correction-backed problems intentionally remain actionable until the linked
            // revision is completed. Re-validating or resuming the workspace must therefore
            // be idempotent instead of attempting to insert the same unique ProblemId again.
            if (existingResolutionProblemIds.Contains(problemId))
                continue;

            db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
                problemId, row.FeedbackId, row.ReportId, row.ReportProductId, row.ContributionId,
                row.ErrorCode.Value, request.Source, currentUser.UserId));
        }

        foreach (var problemId in requested)
        {
            var row = byProblemId[problemId];
            db.AuditEvents.Add(new AuditEvent(
                currentUser.UserId,
                "feedback-resolution.problem-resolved",
                nameof(EmployerInterfaceContributionFeedback),
                row.Id,
                organizationId,
                employerId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    problemId,
                    row.ErrorCode,
                    source = request.Source,
                    validatedReportId = request.ValidatedReportId ?? employeeWorkspaceId
                }),
                http.TraceIdentifier));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "resolution_problem_already_resolved" });
        }

        return Results.Ok(new { resolvedProblemIds = requested });
    }

    private static async Task<IResult> CorrectionWorkspaceResolutionLinksAsync(
        Guid organizationId,
        Guid employerId,
        Guid workspaceReportId,
        IAlphaDbContext db,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var workspace = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(report =>
            report.Id == workspaceReportId
            && report.OrganizationId == organizationId
            && report.EmployerId == employerId
            && report.IsCorrectionWorkspace
            && report.SourceReportId.HasValue, ct);
        if (workspace is null) return Results.NotFound();

        var links = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
            .Where(link => link.WorkspaceReportId == workspaceReportId)
            .OrderBy(link => link.CreatedAt)
            .ToListAsync(ct);
        var ids = links.Select(link => link.ProblemId).Distinct(StringComparer.Ordinal).ToArray();
        var resolved = await EffectiveResolvedProblemIdsAsync(ids, db, ct);
        var pending = links.Where(link => !resolved.Contains(link.ProblemId)).ToArray();

        return Results.Ok(new
        {
            sourceReportId = workspace.SourceReportId,
            problemIds = pending.Select(link => link.ProblemId).Distinct(StringComparer.Ordinal).ToArray(),
            resolverTypes = pending.Select(link => link.ResolverType).Distinct(StringComparer.Ordinal).ToArray()
        });
    }

    private static async Task<IResult> UploadResolutionDocumentAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        string problemId,
        HttpRequest request,
        IAlphaDbContext db,
        OrganizationAccessService access,
        ICurrentUser currentUser,
        IMalwareScanner scanner,
        IDataProtectionService protector,
        IConfiguration configuration,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var reportExists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (!reportExists) return Results.NotFound();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var row = activeRows.FirstOrDefault(x => x.ErrorCode.HasValue
            && string.Equals(
                FeedbackResolutionWireProjection.BuildProblemId(
                    x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode.Value),
                problemId,
                StringComparison.Ordinal));
        if (row is null) return Results.Conflict(new { error = "resolution_problem_stale" });
        if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
            || playbook.Resolver != FeedbackResolverType.Documents
            || !playbook.Actions.HasFlag(FeedbackResolutionAction.UploadDocument))
            return Results.BadRequest(new { error = "resolution_action_not_allowed" });

        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });
        var file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        var maxBytes = Math.Min(10L * 1024 * 1024,
            configuration.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? 10L * 1024 * 1024);
        if (file is null || file.Length == 0 || file.Length > maxBytes)
            return Results.BadRequest(new { error = $"File must be 1 byte to {maxBytes} bytes." });
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "PDF only." });

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan().StartsWith("%PDF-"u8))
            return Results.BadRequest(new { error = "The uploaded file is not a valid PDF file." });

        await using var scanStream = new MemoryStream(bytes, writable: false);
        var scan = await scanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (scan.Verdict == MalwareScanVerdict.Infected)
            return Results.BadRequest(new { error = "The uploaded file failed the security scan." });

        var encrypted = protector.ProtectBytes(bytes, $"feedback-resolution-document:{problemId}");
        var document = new FeedbackResolutionDocument(
            problemId, reportId, row.ReportProductId, file.FileName, "application/pdf", encrypted,
            bytes.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            currentUser.UserId);
        db.FeedbackResolutionDocuments.Add(document);
        db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
            problemId, row.FeedbackId, row.ReportId, row.ReportProductId, row.ContributionId,
            row.ErrorCode.Value, "document-upload", currentUser.UserId));
        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.problem-resolved",
            nameof(EmployerInterfaceContributionFeedback),
            row.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                problemId,
                row.ErrorCode,
                source = "document-upload",
                documentId = document.Id
            }),
            http.TraceIdentifier));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "resolution_problem_already_resolved" });
        }

        return Results.Ok(new
        {
            document.Id,
            document.OriginalFileName,
            document.SizeBytes,
            document.Sha256,
            resolvedProblemId = problemId
        });
    }

    private static async Task<FeedbackResolutionContextResponse> BuildResolutionContextAsync(
        string contextType,
        Guid organizationId,
        Guid employerId,
        Guid? requestedReportId,
        Guid? requestedReportProductId,
        bool canCreateReport,
        bool canEditEmployee,
        IReadOnlyCollection<EmployerInterfaceContributionFeedback> rows,
        IAlphaDbContext db,
        IDataProtectionService protector,
        CancellationToken ct)
    {
        if (rows.Count == 0)
            return new FeedbackResolutionContextResponse(
                contextType, employerId, requestedReportId, requestedReportProductId,
                false, canCreateReport, canEditEmployee,
                Array.Empty<int>(), Array.Empty<FeedbackResolutionProblemDto>(),
                Array.Empty<FeedbackResolutionGroupDto>());

        var unsupportedCodes = rows
            .Where(x => x.ErrorCode.HasValue && !FeedbackResolutionPlaybookCatalog.TryGet(x.ErrorCode.Value, out _))
            .Select(x => x.ErrorCode!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var reportIds = rows.Select(x => x.ReportId).Distinct().ToArray();
        var productIds = rows.Select(x => x.ReportProductId).Distinct().ToArray();
        var contributionIds = rows.Select(x => x.ContributionId).Distinct().ToArray();
        var feedbackIds = rows.Select(x => x.FeedbackId).Distinct().ToArray();

        var reports = await db.ManualReports.AsNoTracking()
            .Where(x => reportIds.Contains(x.Id) && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .ToDictionaryAsync(x => x.Id, ct);
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => productIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var reportEmployeeIds = products.Values.Select(x => x.ReportEmployeeId).Distinct().ToArray();
        var reportEmployees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => reportEmployeeIds.Contains(x.Id)
                && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .ToDictionaryAsync(x => x.Id, ct);
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => contributionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);
        var payments = await db.ManualReportPayments.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        var employmentIds = reportEmployees.Values.Select(x => x.EmploymentId).Distinct().ToArray();
        var employments = await db.Employments.AsNoTracking()
            .Where(x => employmentIds.Contains(x.Id) && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .ToDictionaryAsync(x => x.Id, ct);
        var personIds = employments.Values.Select(x => x.PersonId).Distinct().ToArray();
        var people = await db.People.AsNoTracking()
            .Where(x => personIds.Contains(x.Id) && x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.Id, ct);

        var currentProducts = await db.EmployeePensionProducts.AsNoTracking()
            .Where(x => employmentIds.Contains(x.EmploymentId))
            .ToListAsync(ct);
        var currentProductIds = currentProducts.Select(x => x.Id).ToArray();
        var currentContributions = currentProductIds.Length == 0
            ? Array.Empty<EmployeePensionContribution>()
            : await db.EmployeePensionContributions.AsNoTracking()
                .Where(x => currentProductIds.Contains(x.EmployeePensionProductId))
                .ToArrayAsync(ct);

        var currentPaymentAccount = await new ReportPaymentAccountService(db)
            .ResolveForReportAsync(employerId, null, ct);

        var transferRows = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => reportIds.Contains(x.ReportId) && feedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt)
            .ToListAsync(ct);

        var rowProblemIds = rows.Where(row => row.ErrorCode.HasValue)
            .Select(row => FeedbackResolutionWireProjection.BuildProblemId(
                row.FeedbackId, row.ContributionId, row.Sequence, row.ErrorCode!.Value))
            .ToArray();
        var pendingResolutions = await db.FeedbackProblemResolutions.AsNoTracking()
            .Where(resolution => rowProblemIds.Contains(resolution.ProblemId))
            .Select(resolution => resolution.ProblemId).ToListAsync(ct);
        var pendingResolutionIds = pendingResolutions.ToHashSet(StringComparer.Ordinal);
        var pendingCorrectionLinks = await db.FeedbackCorrectionResolutionLinks.AsNoTracking()
            .Where(link => rowProblemIds.Contains(link.ProblemId))
            .Select(link => new { link.ProblemId, link.WorkspaceReportId }).ToListAsync(ct);
        var relatedWorkspaceIds = pendingCorrectionLinks.Select(link => link.WorkspaceReportId)
            .Distinct().ToArray();
        var transmittedCorrectionIds = await db.ManualReports.AsNoTracking()
            .Where(report => relatedWorkspaceIds.Contains(report.Id)
                && report.IsRevisionSnapshot && report.Status == ManualReportStatus.Completed)
            .Select(report => report.Id).ToHashSetAsync(ct);
        var transmittedProblemIds = pendingCorrectionLinks
            .Where(link => pendingResolutionIds.Contains(link.ProblemId)
                && transmittedCorrectionIds.Contains(link.WorkspaceReportId))
            .Select(link => link.ProblemId).ToHashSet(StringComparer.Ordinal);
        var transmittedDocumentMap = await db.ManualReports.AsNoTracking()
            .Where(report => report.CorrectionWorkspaceId.HasValue
                && transmittedCorrectionIds.Contains(report.CorrectionWorkspaceId.Value)
                && report.IsTechnicalCorrectionDocument)
            .Select(report => new { report.Id, WorkspaceId = report.CorrectionWorkspaceId!.Value })
            .ToListAsync(ct);
        var returnedDocumentIds = transmittedDocumentMap.Select(document => document.Id).ToArray();
        var documentLatestTransmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(tx => returnedDocumentIds.Contains(tx.ReportId))
            .OrderByDescending(tx => tx.AttemptNumber)
            .Select(tx => new { tx.ReportId, tx.Id }).ToListAsync(ct);
        var latestTransmissionIds = documentLatestTransmissions
            .GroupBy(tx => tx.ReportId).ToDictionary(group => group.Key, group => group.First().Id);
        var documentFeedback = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(feedback => feedback.ReportId.HasValue
                && returnedDocumentIds.Contains(feedback.ReportId.Value))
            .Select(feedback => new { feedback.ReportId, feedback.TransmissionId })
            .ToListAsync(ct);
        var returnedWorkspaceIds = transmittedDocumentMap
            .Where(document => latestTransmissionIds.TryGetValue(document.Id, out var latestTx)
                && documentFeedback.Any(feedback => feedback.ReportId == document.Id
                    && feedback.TransmissionId == latestTx))
            .Select(document => document.WorkspaceId).ToHashSet();
        var returnedProblemIds = pendingCorrectionLinks
            .Where(link => pendingResolutionIds.Contains(link.ProblemId)
                && returnedWorkspaceIds.Contains(link.WorkspaceReportId))
            .Select(link => link.ProblemId).ToHashSet(StringComparer.Ordinal);


        var problems = new List<FeedbackResolutionProblemDto>(rows.Count);
        foreach (var row in rows.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Sequence))
        {
            if (!row.ErrorCode.HasValue || !FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode.Value, out var playbook))
                continue;
            if (!products.TryGetValue(row.ReportProductId, out var product)
                || !reportEmployees.TryGetValue(product.ReportEmployeeId, out var reportEmployee)
                || !reports.TryGetValue(row.ReportId, out var report))
                continue;

            contributions.TryGetValue(row.ContributionId, out var contribution);
            employments.TryGetValue(reportEmployee.EmploymentId, out var employment);
            Person? person = null;
            if (employment is not null) people.TryGetValue(employment.PersonId, out person);

            var liveProduct = FeedbackResolutionContextMatcher.FindCurrentProduct(
                product, reportEmployee.EmploymentId, currentProducts);
            var liveContribution = FeedbackResolutionContextMatcher.FindCurrentContribution(
                contribution, liveProduct, currentContributions);

            metadata.TryGetValue(row.ReportProductId, out var productMetadata);
            payments.TryGetValue(row.ReportProductId, out var payment);
            var transferIdentifier = !string.IsNullOrWhiteSpace(productMetadata?.InterfaceTransferIdentifier)
                ? productMetadata.InterfaceTransferIdentifier
                : row.ReportProductId.ToString("D").ToUpperInvariant();
            var transfer = transferRows.FirstOrDefault(x =>
                x.ReportId == row.ReportId
                && string.Equals(x.TransferIdentifier, transferIdentifier, StringComparison.OrdinalIgnoreCase));

            var reportedValues = ReportedValues(playbook.Resolver, report, reportEmployee, product, contribution, productMetadata, payment, protector);
            var currentValues = CurrentValues(playbook.Resolver, employment, person, liveProduct, liveContribution, currentPaymentAccount, protector);
            var feedbackValues = FeedbackValues(row, transfer)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var feedbackProblemId = FeedbackResolutionWireProjection.BuildProblemId(
                row.FeedbackId, row.ContributionId, row.Sequence, playbook.Code);
            feedbackValues["resolutionWorkflowStatus"] = returnedProblemIds.Contains(feedbackProblemId)
                ? "feedback-returned-needs-review"
                : transmittedProblemIds.Contains(feedbackProblemId)
                    ? "transmitted-awaiting-feedback"
                    : pendingResolutionIds.Contains(feedbackProblemId)
                    ? "correction-in-progress"
                    : "needs-treatment";
            var previousRecordIdentifier = contribution?.PreviousRecordIdentifier;
            var groupKey = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
                playbook, employerId, row.ReportId, row.ReportProductId, row.ContributionId,
                reportEmployee.EmploymentId, transferIdentifier, previousRecordIdentifier, row.FeedbackId, row.Sequence);

            problems.Add(new FeedbackResolutionProblemDto(
                ProblemId: FeedbackResolutionWireProjection.BuildProblemId(row.FeedbackId, row.ContributionId, row.Sequence, playbook.Code),
                Code: playbook.Code,
                Description: string.IsNullOrWhiteSpace(row.ErrorDescription)
                    ? EmployerInterfaceLineFeedbackParser.Description(row.ErrorCode)
                    : row.ErrorDescription,
                Scope: FeedbackResolutionWireProjection.WireName(playbook.Scope),
                ResolutionType: FeedbackResolutionWireProjection.WireName(playbook.ResolutionType),
                Family: FeedbackResolutionWireProjection.WireName(playbook.Family),
                ResolverType: FeedbackResolutionWireProjection.WireName(playbook.Resolver),
                GroupStrategy: FeedbackResolutionWireProjection.WireName(playbook.GroupStrategy),
                GroupKey: groupKey,
                CorrectionBehavior: FeedbackResolutionWireProjection.WireName(playbook.CorrectionBehavior),
                AvailableActions: FeedbackResolutionWireProjection.ActionNames(playbook.Actions),
                CanEscalateExternally: playbook.CanEscalateExternally,
                FeedbackId: row.FeedbackId,
                ReportId: row.ReportId,
                ReportProductId: row.ReportProductId,
                ContributionId: row.ContributionId,
                ReportEmployeeId: reportEmployee.Id,
                EmploymentId: reportEmployee.EmploymentId,
                PersonId: reportEmployee.PersonId,
                EmployeeName: $"{reportEmployee.FirstName} {reportEmployee.LastName}".Trim(),
                ProductName: product.FundName,
                FundCompanyName: product.FundCompanyName,
                PolicyNumber: product.PolicyNumber,
                ReportedValues: reportedValues,
                CurrentValues: currentValues,
                FeedbackValues: feedbackValues,
                LatestDecision: null,
                LatestDecisionNote: string.Empty,
                LatestDecisionAt: null,
                ReceivedAt: row.ReceivedAt));
        }

        if (problems.Count > 0)
        {
            var problemIds = problems.Select(problem => problem.ProblemId).ToArray();
            var latestDecisions = await db.FeedbackProblemDecisions.AsNoTracking()
                .Where(decision => problemIds.Contains(decision.ProblemId))
                .OrderByDescending(decision => decision.DecidedAt)
                .ThenByDescending(decision => decision.CreatedAt)
                .ToListAsync(ct);
            var latestByProblem = latestDecisions
                .GroupBy(decision => decision.ProblemId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            for (var index = 0; index < problems.Count; index++)
            {
                var problem = problems[index];
                if (!latestByProblem.TryGetValue(problem.ProblemId, out var decision)) continue;
                problems[index] = problem with
                {
                    LatestDecision = decision.Outcome,
                    LatestDecisionNote = decision.Note,
                    LatestDecisionAt = decision.DecidedAt
                };
            }
        }

        if (problems.Count > 0)
        {
            var problemIds = problems.Select(problem => problem.ProblemId).ToArray();
            var caseLinks = await db.FeedbackExternalCaseProblems.AsNoTracking()
                .Where(link => problemIds.Contains(link.ProblemId))
                .ToListAsync(ct);
            if (caseLinks.Count > 0)
            {
                var caseIds = caseLinks.Select(link => link.CaseId).Distinct().ToArray();
                var cases = await db.FeedbackExternalCases.AsNoTracking()
                    .Where(item => caseIds.Contains(item.Id))
                    .ToDictionaryAsync(item => item.Id, ct);
                var assigneeIds = cases.Values
                    .Where(item => item.AssignedToUserId.HasValue)
                    .Select(item => item.AssignedToUserId!.Value)
                    .Distinct()
                    .ToArray();
                var assigneeNames = assigneeIds.Length == 0
                    ? new Dictionary<Guid, string>()
                    : await db.Users.AsNoTracking()
                        .Where(user => assigneeIds.Contains(user.Id))
                        .ToDictionaryAsync(user => user.Id, user => user.DisplayName, ct);
                var latestCaseByProblem = caseLinks
                    .OrderByDescending(link => link.CreatedAt)
                    .GroupBy(link => link.ProblemId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                for (var index = 0; index < problems.Count; index++)
                {
                    var problem = problems[index];
                    if (!latestCaseByProblem.TryGetValue(problem.ProblemId, out var link)
                        || !cases.TryGetValue(link.CaseId, out var externalCase))
                        continue;
                    problems[index] = problem with
                    {
                        ExternalCaseId = externalCase.Id,
                        ExternalCaseStatus = externalCase.Status,
                        ExternalCaseAssigneeName = externalCase.AssignedToUserId.HasValue
                            ? assigneeNames.GetValueOrDefault(externalCase.AssignedToUserId.Value) ?? string.Empty
                            : string.Empty
                    };
                }
            }
        }

        var groups = FeedbackResolutionWireProjection.BuildGroups(
            problems, canCreateReport, canEditEmployee);
        var canResolve = unsupportedCodes.Length == 0 && groups.Any(group => group.CanExecute);

        return new FeedbackResolutionContextResponse(
            contextType, employerId, requestedReportId, requestedReportProductId,
            canResolve, canCreateReport, canEditEmployee, unsupportedCodes, problems, groups);
    }

    private static IReadOnlyDictionary<string, string?> ReportedValues(
        FeedbackResolverType resolver,
        ManualReport report,
        ManualReportEmployee employee,
        ManualReportProduct product,
        ManualContribution? contribution,
        EmployerInterfaceReportProductData? metadata,
        ManualReportPayment? payment,
        IDataProtectionService protector)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Add(string key, object? value) => values[key] = ResolutionValue(value);

        if (resolver is FeedbackResolverType.Employee or FeedbackResolverType.EmploymentStatus
            or FeedbackResolverType.ProductPolicy or FeedbackResolverType.Contribution
            or FeedbackResolverType.Split or FeedbackResolverType.ExternalCase)
        {
            Add("employeeName", $"{employee.FirstName} {employee.LastName}".Trim());
            Add("identifierType", employee.InterfaceIdentifierType);
            Add("identifier", protector.Unprotect(employee.InterfaceIdentifier, $"report-employee-interface-id:{employee.Id}"));
            Add("employeeNumber", employee.EmployeeNumber);
            Add("birthDate", employee.BirthDateSnapshot);
            Add("gender", employee.GenderSnapshot);
            Add("email", protector.Unprotect(employee.EmailSnapshot, $"report-employee-email:{employee.Id}"));
            Add("mobile", protector.Unprotect(employee.MobileSnapshot, $"report-employee-mobile:{employee.Id}"));
            Add("employmentStartDate", employee.EmploymentStartDateSnapshot);
            Add("city", employee.CitySnapshot);
            Add("street", employee.StreetSnapshot);
            Add("houseNumber", employee.HouseNumberSnapshot);
            Add("apartment", employee.ApartmentSnapshot);
            Add("postalCode", employee.PostalCodeSnapshot);
            Add("postOfficeBox", employee.PostOfficeBoxSnapshot);
            Add("monthlySalary", employee.MonthlySalary);
            Add("employeeStatus", metadata?.EmployeeStatus);
            Add("statusStartDate", metadata?.StatusStartDate);
            Add("employmentPercentage", metadata?.EmploymentPercentage);
            Add("workDaysInMonth", metadata?.WorkDaysInMonth);
        }

        if (resolver is FeedbackResolverType.ProductPolicy or FeedbackResolverType.Contribution
            or FeedbackResolverType.Split or FeedbackResolverType.Refund or FeedbackResolverType.ExternalCase
            or FeedbackResolverType.Documents)
        {
            Add("productType", (int)product.ProductType);
            Add("policyNumber", product.PolicyNumber);
            Add("fundCode", product.FundCode);
            Add("fundName", product.FundName);
            Add("fundCompanyName", product.FundCompanyName);
            Add("salaryMonth", product.SalaryMonth);
            Add("salary", product.Salary);
            Add("reportingType", product.ReportingType);
            Add("section14Code", product.Section14Code);
        }

        if (resolver is FeedbackResolverType.Contribution or FeedbackResolverType.Refund)
        {
            Add("contributionAmount", contribution?.Amount);
            Add("contributionPercentage", contribution?.Percentage);
            Add("exemptPayments", contribution?.ExemptPayments);
            Add("recordIdentifier", contribution?.InterfaceRecordIdentifier);
            Add("previousRecordIdentifier", contribution?.PreviousRecordIdentifier);
        }

        if (resolver is FeedbackResolverType.Payment or FeedbackResolverType.Refund)
        {
            Add("paymentMethod", payment?.PaymentMethod);
            Add("providerAccount", payment?.ProviderAccount);
            Add("referenceNumber", payment?.ReferenceNumber);
            Add("valueDate", payment?.ValueDate);
            Add("actualDepositAmount", payment?.ActualDepositAmount);
            Add("paymentMethodCode", metadata?.PaymentMethodCode);
            Add("employerAccountType", metadata?.EmployerAccountType);
            Add("receiverAccountType", metadata?.ReceiverAccountType);
            Add("interfaceTransferIdentifier", metadata?.InterfaceTransferIdentifier);
            Add("clearingIdentifier", metadata?.ClearingIdentifier);
            Add("employerBankCode", payment?.EmployerBankCode);
            Add("employerBranch", payment?.EmployerBranch);
            Add("employerAccount", payment is null ? null : protector.Unprotect(
                payment.EmployerAccount, $"report-payment-account:{payment.ReportProductId}"));
        }

        if (resolver is FeedbackResolverType.ReportCorrection or FeedbackResolverType.ConditionalField)
        {
            Add("reportingMonth", report.ReportingMonth);
            Add("reportKind", (int)report.ReportKind);
            Add("salaryPaymentDate", report.SalaryPaymentDate);
            Add("policyNumber", product.PolicyNumber);
            Add("salaryMonth", product.SalaryMonth);
            Add("operationCode", metadata?.OperationCode);
            Add("depositStatus", metadata?.DepositStatus);
            Add("recordIdentifier", contribution?.InterfaceRecordIdentifier);
            Add("previousRecordIdentifier", contribution?.PreviousRecordIdentifier);
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string?> CurrentValues(
        FeedbackResolverType resolver,
        Employment? employment,
        Person? person,
        EmployeePensionProduct? product,
        EmployeePensionContribution? contribution,
        EmployerPaymentAccount? paymentAccount,
        IDataProtectionService protector)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Add(string key, object? value) => values[key] = ResolutionValue(value);

        if (resolver is FeedbackResolverType.Employee or FeedbackResolverType.EmploymentStatus)
        {
            Add("entityFound", employment is not null && person is not null);
            if (employment is not null && person is not null)
            {
                Add("employeeName", $"{person.FirstName} {person.LastName}".Trim());
                Add("identifierType", (int)person.IdentifierType);
                Add("identifier", person.NationalIdEncrypted is null ? null
                    : protector.Unprotect(person.NationalIdEncrypted, "person-national-id"));
                Add("employeeNumber", employment.EmployeeNumber);
                Add("birthDate", person.BirthDate);
                Add("gender", person.Gender.HasValue ? (int)person.Gender.Value : null);
                Add("email", person.Email);
                Add("mobile", person.Mobile);
                Add("city", person.City);
                Add("street", person.Street);
                Add("houseNumber", person.HouseNumber);
                Add("apartment", person.Apartment);
                Add("postalCode", person.PostalCode);
                Add("postOfficeBox", person.PostOfficeBox);
                Add("employmentStartDate", employment.StartDate);
                Add("employmentStatus", (int)employment.Status);
                Add("monthlySalary", employment.MonthlySalary);
            }
        }

        if (resolver is FeedbackResolverType.ProductPolicy or FeedbackResolverType.Contribution
            or FeedbackResolverType.Split or FeedbackResolverType.Refund or FeedbackResolverType.ExternalCase)
        {
            Add("entityFound", product is not null);
            if (product is not null)
            {
                Add("productType", (int)product.ProductType);
                Add("policyNumber", product.PolicyNumber);
                Add("fundCode", product.FundCode);
                Add("fundName", product.FundName);
                Add("fundCompanyName", product.FundCompanyName);
                Add("salary", product.Salary);
                Add("reportingType", product.ReportingType);
                Add("section14Code", product.Section14Code);
                Add("isActive", product.IsActive);
                Add("effectiveFrom", product.EffectiveFrom);
                Add("effectiveTo", product.EffectiveTo);
            }
        }

        if (resolver is FeedbackResolverType.Contribution or FeedbackResolverType.Refund)
        {
            Add("contributionFound", contribution is not null);
            if (contribution is not null)
            {
                Add("contributionAmount", contribution.Amount);
                Add("contributionPercentage", contribution.Percentage);
                Add("exemptPayments", contribution.ExemptPayments);
            }
        }

        if (resolver is FeedbackResolverType.Payment or FeedbackResolverType.Refund)
        {
            Add("currentPaymentAccountFound", paymentAccount is not null);
            if (paymentAccount is not null)
            {
                Add("currentPaymentBankId", paymentAccount.BankId);
                Add("currentPaymentBranchId", paymentAccount.BranchId);
                Add("currentPaymentAccountNumber", paymentAccount.AccountNumberEncrypted is null ? null
                    : protector.Unprotect(paymentAccount.AccountNumberEncrypted, "bank-account-number"));
                Add("currentPaymentAccountHolderName", paymentAccount.AccountHolderName);
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string?> FeedbackValues(
        EmployerInterfaceContributionFeedback row,
        EmployerInterfaceTransferFeedback? transfer)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Add(string key, object? value) => values[key] = ResolutionValue(value);

        Add("intakeStatus", row.IntakeStatus);
        Add("errorAmount", row.ErrorAmount);
        Add("errorDate", row.ErrorDate);
        Add("contributionTypeCode", row.ContributionTypeCode);
        Add("calculatedSalary", row.CalculatedSalary);
        Add("salaryMonth", row.SalaryMonth);
        Add("policyNumber", row.PolicyNumber);
        Add("contributionRate", row.ContributionRate);
        Add("contributionAmount", row.ContributionAmount);
        Add("recordIdentifier", row.RecordIdentifier);
        Add("sourceFileName", row.SourceFileName);

        if (transfer is not null)
        {
            Add("reportedDepositAmount", transfer.ReportedDepositAmount);
            Add("actualReceivedAmount", transfer.ActualReceivedAmount);
            Add("allocatedAmount", transfer.AllocatedAmount);
            Add("inTransitAmount", transfer.InTransitAmount);
            Add("proactiveRefundAmount", transfer.ProactiveRefundAmount);
            Add("employerAccountRefundAmount", transfer.EmployerAccountRefundAmount);
            Add("moneyTreatmentStatus", transfer.MoneyTreatmentStatus);
            Add("statusDetail", transfer.StatusDetail);
            Add("paymentReference", transfer.PaymentReference);
            Add("transferValueDate", transfer.ValueDate);
            Add("trustAccountValueDate", transfer.TrustAccountValueDate);
            Add("clearingIdentifier", transfer.ClearingIdentifier);
        }

        return values;
    }

    private static string? ResolutionValue(object? value) => value switch
    {
        null => null,
        string text => text,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        float number => number.ToString(CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        Enum enumValue => Convert.ToInt32(enumValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static async Task<IResult> DepositDetailsAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        var row = await (from p in db.ManualReportProducts.AsNoTracking()
                         join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                         where p.Id == reportProductId && e.ReportId == reportId select new { Product = p, Employee = e })
            .SingleOrDefaultAsync(ct);
        if (row is null) return Results.NotFound();

        var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => x.ReportProductId == reportProductId
                && (x.Amount != 0m || x.Percentage != 0m || x.ExemptPayments != 0m))
            .OrderBy(x => x.Party).ThenBy(x => x.Component).ToListAsync(ct);
        var feedbackRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => x.ReportProductId == reportProductId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Sequence).ToListAsync(ct);
        var manufacturer = feedbackRows.GroupBy(x => x.ContributionId).SelectMany(group =>
        {
            var latest = group.First(); return group.Where(x => x.FeedbackId == latest.FeedbackId).OrderBy(x => x.Sequence);
        }).ToArray();
        var manufacturerProblemIds = manufacturer
            .Where(x => x.ErrorCode.HasValue && ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .Select(x => FeedbackResolutionWireProjection.BuildProblemId(
                x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode!.Value))
            .ToArray();
        var resolvedManufacturerProblemIds = await EffectiveResolvedProblemIdsAsync(manufacturerProblemIds, db, ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking().SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        var transferKey = !string.IsNullOrWhiteSpace(metadata?.InterfaceTransferIdentifier)
            ? metadata.InterfaceTransferIdentifier : reportProductId.ToString("D").ToUpperInvariant();
        var transfer = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.TransferIdentifier == transferKey
                && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(ct);
        var payment = await db.ManualReportPayments.AsNoTracking().SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        var treatment = await db.ReportProductTreatments.AsNoTracking().SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        var history = await db.ReportProductTreatmentHistory.AsNoTracking().Where(x => x.ReportProductId == reportProductId)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var userIds = history.Select(x => x.UpdatedByUserId).Append(treatment?.UpdatedByUserId ?? Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var labels = TreatmentStatuses.ToDictionary(x => x.Code, x => x.Label, StringComparer.Ordinal);

        static int TypeCode(ManualContribution c) => (c.Party, c.Component) switch
        {
            (ContributionParty.Employer, ContributionComponent.Severance) => 1,
            (ContributionParty.Employee, ContributionComponent.Benefits) => 2,
            (ContributionParty.Employer, ContributionComponent.Benefits) => 3,
            (ContributionParty.Employee, ContributionComponent.Severance) => 4,
            (ContributionParty.Employee, ContributionComponent.Disability) => 5,
            (ContributionParty.Employer, ContributionComponent.Disability) => 6,
            (ContributionParty.Employee, ContributionComponent.Other) => 7,
            (ContributionParty.Employer, ContributionComponent.Other) => 8,
            _ => 0
        };
        static string Label(ManualContribution c) => (c.Party, c.Component) switch
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

        return Results.Ok(new
        {
            report = new { report.Id, report.ReportingMonth, report.ReportKind, report.Status, canEdit = report.IsEditable && canCreateReport },
            employee = new
            {
                row.Employee.Id, row.Employee.EmploymentId, name = row.Employee.FirstName + " " + row.Employee.LastName,
                nationalId = protector.Unprotect(row.Employee.NationalId, $"report-employee-national-id:{row.Employee.Id}"),
                row.Employee.MonthlySalary
            },
            product = new
            {
                row.Product.Id, row.Product.FundName, row.Product.FundCompanyName, row.Product.FundCode,
                row.Product.PolicyNumber, row.Product.SalaryMonth, row.Product.Salary, row.Product.ReportingType,
                totalAmount = contributions.Sum(x => x.Amount)
            },
            employerContributions = contributions.Select(x => new
            {
                id = x.Id, contributionTypeCode = TypeCode(x), label = Label(x), x.Amount, x.Percentage, x.ExemptPayments
            }),
            manufacturerContributions = manufacturer.Select(x => new
            {
                x.ContributionId, x.RecordIdentifier, x.Sequence, x.IntakeStatus, x.ErrorCode, x.ErrorDescription,
                isResolved = x.ErrorCode.HasValue
                    && resolvedManufacturerProblemIds.Contains(FeedbackResolutionWireProjection.BuildProblemId(
                        x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode.Value)),
                errorScope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode).ToString().ToLowerInvariant(),
                x.ErrorAmount, x.ErrorDate, x.ContributionTypeCode, x.CalculatedSalary, x.SalaryMonth, x.PolicyNumber,
                x.ContributionRate, x.ContributionAmount, x.SourceFileName, x.ReceivedAt
            }),
            money = transfer is null ? null : new
            {
                transfer.ReportedDepositAmount, transfer.ActualReceivedAmount, transfer.AllocatedAmount, transfer.InTransitAmount,
                transfer.ProactiveRefundAmount, transfer.EmployerAccountRefundAmount, transfer.MoneyTreatmentStatus,
                transfer.StatusDetail, transfer.PaymentReference, transfer.ValueDate, transfer.TrustAccountValueDate,
                transfer.CorrectnessTimestamp, transfer.ClearingIdentifier, transfer.ReceivedAt
            },
            payment = payment is null ? null : new
            {
                payment.ProviderName, payment.ProviderAccount, payment.PaymentMethod, payment.ValueDate, payment.TrustAccountValueDate,
                payment.ActualDepositAmount, payment.MasavSenderCode, payment.ReferenceNumber, payment.EmployerBankName,
                payment.EmployerBankCode, payment.EmployerBranch,
                employerAccount = protector.Unprotect(payment.EmployerAccount, $"report-payment-account:{payment.ReportProductId}")
            },
            treatment = treatment is null ? null : new
            {
                treatment.StatusCode, label = labels.GetValueOrDefault(treatment.StatusCode) ?? treatment.StatusCode,
                treatment.Note, treatment.UpdatedAt, updatedBy = users.GetValueOrDefault(treatment.UpdatedByUserId) ?? "משתמש"
            },
            treatmentHistory = history.Select(x => new
            {
                x.PreviousStatusCode, previousStatusLabel = labels.GetValueOrDefault(x.PreviousStatusCode) ?? x.PreviousStatusCode,
                x.StatusCode, statusLabel = labels.GetValueOrDefault(x.StatusCode) ?? x.StatusCode, x.Note, x.CreatedAt,
                updatedBy = users.GetValueOrDefault(x.UpdatedByUserId) ?? "משתמש"
            }),
            canUpdateTreatment = canCreateReport,
            canCreateCorrection = ReportFeedbackStatusResolver.CanCreateCorrection(
                canCreateReport, report.IsEditable, report.Status, report.ReportKind,
                metadata is not null, metadata?.OperationCode == 6)
        });
    }


    private static object FeedbackIssue(EmployerInterfaceContributionFeedback row) => new
    {
        code = row.ErrorCode ?? 0,
        description = string.IsNullOrWhiteSpace(row.ErrorDescription)
            ? EmployerInterfaceLineFeedbackParser.Description(row.ErrorCode)
            : row.ErrorDescription,
        scope = EmployerInterfaceLineFeedbackParser.ErrorScope(row.ErrorCode).ToString().ToLowerInvariant(),
        row.ReportId,
        row.ReportProductId,
        row.ContributionId,
        row.ReceivedAt
    };

    private static async Task<IReadOnlyList<EmployerInterfaceContributionFeedback>> ActiveActionableFeedbackAsync(
        IReadOnlyCollection<Guid> reportIds, IAlphaDbContext db, CancellationToken ct)
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
        IReadOnlyCollection<string> problemIds, IAlphaDbContext db, CancellationToken ct)
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
            .Where(contribution => productIds.Contains(contribution.ReportProductId))
            .Select(contribution => new { contribution.Id, contribution.ReportProductId })
            .ToListAsync(ct);
        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(tx => documentIds.Contains(tx.ReportId))
            .OrderByDescending(tx => tx.AttemptNumber)
            .Select(tx => new { tx.Id, tx.ReportId })
            .ToListAsync(ct);
        var latestTx = transmissions.GroupBy(tx => tx.ReportId)
            .ToDictionary(group => group.Key, group => group.First().Id);
        var feedbackFiles = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(file => file.ReportId.HasValue && documentIds.Contains(file.ReportId.Value))
            .Select(file => new { file.Id, file.ReportId, file.TransmissionId })
            .ToListAsync(ct);
        var freshFeedbackIds = feedbackFiles
            .Where(file => file.ReportId.HasValue && latestTx.TryGetValue(file.ReportId.Value, out var txId)
                && file.TransmissionId == txId)
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
                && rows.All(row => row.IntakeStatus == 1 && (row.ErrorCode is 1 or 31))))
            .Select(group => group.Key).ToHashSet();

        var sourceProductIds = correctionLinks.Where(link => link.ReportProductId.HasValue)
            .Select(link => link.ReportProductId!.Value).Distinct().ToArray();
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
            if (!link.ReportProductId.HasValue)
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
                .Where(product => product.SourceReportProductId == link.ReportProductId
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

    private static async Task<IResult> EmployerContextAsync(
        Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();

        var reportIds = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var rows = await ActiveActionableFeedbackAsync(reportIds, db, ct);
        var issues = rows
            .Where(x => EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money)
            .GroupBy(x => new { x.ErrorCode, x.ErrorDescription })
            .Select(g => FeedbackIssue(g.OrderByDescending(x => x.ReceivedAt).First()))
            .ToArray();

        return Results.Ok(new
        {
            employer = new
            {
                employer.Id,
                employer.LegalName,
                employer.RegistrationNumber,
                employer.WithholdingFileNumber,
                status = employer.Status.ToString(),
                contactName = string.Join(" ", new[] { employer.ContactFirstName, employer.ContactLastName }
                    .Where(x => !string.IsNullOrWhiteSpace(x))),
                employer.ContactPhone,
                employer.ContactEmail,
                employer.ContactMobile
            },
            issues
        });
    }

    private static async Task<IResult> ReportContextAsync(
        Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId
                && x.EmployerId == employerId && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (report is null) return Results.NotFound();

        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();

        var rows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var scopedIssues = rows
            .Where(x =>
            {
                var scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode);
                return scope == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report;
            })
            .GroupBy(x => new { x.ErrorCode, x.ErrorDescription, Scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode) })
            .Select(g => FeedbackIssue(g.First()))
            .ToList<object>();

        var latestTransmission = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(report.ValidationError))
            scopedIssues.Insert(0, new
            {
                code = 0,
                description = report.ValidationError,
                scope = "report",
                ReportId = report.Id,
                ReportProductId = (Guid?)null,
                ContributionId = (Guid?)null,
                ReceivedAt = report.UpdatedAt
            });
        if (latestTransmission is not null && !string.IsNullOrWhiteSpace(latestTransmission.ErrorMessage))
            scopedIssues.Insert(0, new
            {
                code = 0,
                description = latestTransmission.ErrorMessage,
                scope = "report",
                ReportId = report.Id,
                ReportProductId = (Guid?)null,
                ContributionId = (Guid?)null,
                ReceivedAt = latestTransmission.UpdatedAt
            });

        var employeeCount = await db.ManualReportEmployees.AsNoTracking().CountAsync(x => x.ReportId == reportId, ct);
        var productIds = await (from product in db.ManualReportProducts.AsNoTracking()
                                join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                                where employee.ReportId == reportId
                                select product.Id).ToArrayAsync(ct);
        var totalAmount = productIds.Length == 0
            ? 0m
            : await db.ManualContributions.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId))
                .SumAsync(x => x.Amount, ct);

        return Results.Ok(new
        {
            employer = new { employer.Id, employer.LegalName },
            report = new
            {
                report.Id,
                report.ReportingMonth,
                report.SalaryPaymentDate,
                report.ReportKind,
                report.Status,
                employeeCount,
                totalAmount,
                report.CreatedAt,
                report.UpdatedAt
            },
            issues = scopedIssues
        });
    }

    private static async Task<IResult> UpdateTreatmentAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId, UpdateTreatmentRequest request,
        IAlphaDbContext db, OrganizationAccessService access, ICurrentUser currentUser, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!TreatmentStatuses.Any(x => x.Code == request.StatusCode)) return Results.BadRequest(new { error = "treatment_status_invalid" });
        if ((request.Note?.Length ?? 0) > 4000) return Results.BadRequest(new { error = "treatment_note_too_long" });
        var belongs = await (from p in db.ManualReportProducts.AsNoTracking()
                             join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                             where p.Id == reportProductId && e.ReportId == reportId && e.OrganizationId == organizationId && e.EmployerId == employerId
                             select p.Id).AnyAsync(ct);
        if (!belongs) return Results.NotFound();

        var treatment = await db.ReportProductTreatments.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (!ReportFeedbackStatusResolver.TreatmentVersionMatches(treatment?.UpdatedAt, request.ExpectedUpdatedAt))
            return Results.Conflict(new { error = "treatment_conflict" });
        var creatingTreatment = treatment is null;
        var previous = treatment?.StatusCode ?? string.Empty;
        if (treatment is null)
        {
            treatment = new ReportProductTreatment(reportProductId, request.StatusCode, request.Note, currentUser.UserId);
            db.ReportProductTreatments.Add(treatment);
        }
        else treatment.Update(request.StatusCode, request.Note, currentUser.UserId);
        db.ReportProductTreatmentHistory.Add(new ReportProductTreatmentHistory(reportProductId, previous, request.StatusCode, request.Note, currentUser.UserId));
        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "report-feedback.treatment-updated",
            nameof(ReportProductTreatment),
            treatment.Id,
            organizationId,
            employerId,
            JsonSerializer.Serialize(new { reportId, reportProductId, previousStatus = previous, statusCode = request.StatusCode }),
            http.TraceIdentifier));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "treatment_conflict" });
        }
        catch (DbUpdateException) when (creatingTreatment)
        {
            var existsNow = await db.ReportProductTreatments.AsNoTracking()
                .AnyAsync(x => x.ReportProductId == reportProductId, CancellationToken.None);
            if (existsNow) return Results.Conflict(new { error = "treatment_conflict" });
            throw;
        }
        return Results.Ok(new
        {
            treatment.StatusCode, label = TreatmentStatuses.First(x => x.Code == request.StatusCode).Label,
            treatment.Note, treatment.UpdatedAt
        });
    }

    private static async Task<IResult> ExportAsync(
        Guid organizationId, Guid employerId, Guid reportId, string exportType,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
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

    private static async Task<HashSet<Guid>> ActiveFeedbackIdsAsync(
        Guid reportId, IAlphaDbContext db, CancellationToken ct)
    {
        var latestTransmissionId = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .OrderByDescending(x => x.AttemptNumber)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);

        var query = db.EmployerInterfaceFeedback.AsNoTracking().Where(x => x.ReportId == reportId);
        query = latestTransmissionId.HasValue
            ? query.Where(x => x.TransmissionId == latestTransmissionId.Value)
            : query.Where(x => x.TransmissionId == null);
        return (await query.Select(x => x.Id).ToListAsync(ct)).ToHashSet();
    }

    private static async Task<IResult> DetailsAsync(
        Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.AttemptNumber).ToListAsync(ct);
        var latest = transmissions.FirstOrDefault();
        var officialFeedback = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.ReportId == reportId)
            .OrderByDescending(x => x.ReceivedAt)
            .Select(x => new { x.Id, x.DocumentType, x.SourceFileName, x.InterfaceFileNumber, x.PayloadHash, x.TransmissionId, x.ReceivedAt })
            .ToListAsync(ct);
        var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
        var employeesById = employees.ToDictionary(x => x.Id);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var productsByRecordId = contributions
            .GroupBy(x => (string.IsNullOrWhiteSpace(x.InterfaceRecordIdentifier) ? x.Id.ToString("D") : x.InterfaceRecordIdentifier).ToUpperInvariant(), StringComparer.Ordinal)
            .Where(group => group.Select(x => x.ReportProductId).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().ReportProductId, StringComparer.Ordinal);
        var recordFeedback = new Dictionary<Guid, List<EmployerInterfaceLineFeedbackParser.RecordStatus>>();
        foreach (var file in await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.ReportId == reportId
                && activeFeedbackIdsForReport.Contains(x.Id))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct))
        {
            var xml = protector.Unprotect(file.RawXml, $"employer-interface-feedback:{file.PayloadHash}");
            foreach (var item in EmployerInterfaceLineFeedbackParser.Parse(xml))
            {
                if (!productsByRecordId.TryGetValue(item.RecordIdentifier, out var productId)) continue;
                if (!recordFeedback.TryGetValue(productId, out var matches)) recordFeedback[productId] = matches = [];
                if (matches.Any(x => x.RecordIdentifier == item.RecordIdentifier)) continue;
                matches.Add(item with { SourceFileName = file.SourceFileName, ReceivedAt = file.ReceivedAt });
            }
        }
        var depositFeedback = products.Select(productRow =>
        {
            recordFeedback.TryGetValue(productRow.Id, out var statuses);
            return new { reportProductId = productRow.Id, hasRecordFeedback = statuses is { Count: > 0 }, records = statuses ?? [] };
        }).ToArray();

        var activeContributionRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => x.ReportId == reportId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.CreatedAt)
            .Select(x => new { x.ContributionId, x.ErrorCode })
            .ToListAsync(ct);
        var latestActiveContributionRows = activeContributionRows.GroupBy(x => x.ContributionId).Select(g => g.First()).ToArray();
        var expectedContributionCount = contributions.Count(ReportFeedbackStatusResolver.IsEffectiveContribution);
        var activeErrorCount = latestActiveContributionRows.Count(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode));
        var normalizedFeedbackStatus = ReportFeedbackStatusResolver.ResolveReportState(
            latest?.Status,
            activeFeedbackIdsForReport.Count,
            expectedContributionCount,
            latestActiveContributionRows.Length,
            activeErrorCount);

        var issues = new List<object>();
        if (!string.IsNullOrWhiteSpace(report.ValidationError))
            issues.Add(new { source = "report", code = "REPORT_VALIDATION", description = report.ValidationError, employeeId = (Guid?)null, employeeName = (string?)null, productId = (Guid?)null, productName = (string?)null, actionType = "EditReport" });
        if (latest is not null && !string.IsNullOrWhiteSpace(latest.ErrorMessage))
            issues.Add(new { source = "transmission", code = latest.Status.ToString().ToUpperInvariant(), description = latest.ErrorMessage, employeeId = (Guid?)null, employeeName = (string?)null, productId = (Guid?)null, productName = (string?)null, actionType = "RetryTransmission" });
        foreach (var employee in employees.Where(x => x.ValidationStatus == ManualReportItemStatus.Error && !string.IsNullOrWhiteSpace(x.ValidationError)))
            issues.Add(new { source = "employee", code = "EMPLOYEE_VALIDATION", description = employee.ValidationError, employeeId = (Guid?)employee.EmploymentId, employeeName = $"{employee.FirstName} {employee.LastName}", productId = (Guid?)null, productName = (string?)null, actionType = "EditReport" });
        foreach (var productRow in products.Where(x => x.ValidationStatus == ManualReportItemStatus.Error && !string.IsNullOrWhiteSpace(x.ValidationError)))
        {
            employeesById.TryGetValue(productRow.ReportEmployeeId, out var employee);
            issues.Add(new { source = "product", code = "PRODUCT_VALIDATION", description = productRow.ValidationError, employeeId = employee?.EmploymentId, employeeName = employee is null ? null : $"{employee.FirstName} {employee.LastName}", productId = (Guid?)productRow.Id, productName = string.IsNullOrWhiteSpace(productRow.FundName) ? productRow.PolicyNumber : productRow.FundName, actionType = "EditReport" });
        }

        return Results.Ok(new
        {
            report = new { report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.ReportKind, report.Status, report.ValidationError, report.CreatedAt, report.UpdatedAt },
            feedbackStatus = normalizedFeedbackStatus, issueCount = issues.Count + activeErrorCount, issues, officialFeedback, depositFeedback,
            transmissions = transmissions.Select(x => new { x.Id, x.AttemptNumber, x.Status, x.Provider, x.ExternalId, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt })
        });
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
