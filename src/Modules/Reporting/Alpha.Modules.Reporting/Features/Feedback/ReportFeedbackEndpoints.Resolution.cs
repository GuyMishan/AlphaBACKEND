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
    private static async Task<IResult> EmployerResolutionContextAsync(
        Guid organizationId, Guid employerId, AlphaDbContext db,
        IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();

        var reportIds = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var activeRows = await ActiveActionableFeedbackAsync(reportIds, db, ct);
        var selectedTargets = await TreatmentTargetsAsync(activeRows, employerId, db, protector, ct);
        var rows = activeRows.Where(row =>
            TreatmentTargetFor(row, selectedTargets) == FeedbackTreatmentTarget.Employer).ToArray();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);

        return Results.Ok(await BuildResolutionContextAsync(
            "employer", organizationId, employerId, null, null, canCreateReport, canEditEmployee, rows, db, protector, ct,
            canEditEmployer: await access.CanEditEmployerAsync(organizationId, employerId, ct)));
    }

    private static async Task<IResult> ReportResolutionContextAsync(
        Guid organizationId, Guid employerId, Guid reportId, AlphaDbContext db,
        IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (!exists) return Results.NotFound();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var selectedTargets = await TreatmentTargetsAsync(activeRows, employerId, db, protector, ct);
        var rows = activeRows.Where(row =>
            TreatmentTargetFor(row, selectedTargets) == FeedbackTreatmentTarget.Report).ToArray();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct);

        return Results.Ok(await BuildResolutionContextAsync(
            "report", organizationId, employerId, reportId, null, canCreateReport, canEditEmployee, rows, db, protector, ct));
    }

    private static async Task<IResult> DepositResolutionContextAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId, AlphaDbContext db,
        IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var belongs = await (from product in db.ManualReportProducts.AsNoTracking()
                             join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                             where product.Id == reportProductId && employee.ReportId == reportId
                                 && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                             select product.Id).AnyAsync(ct);
        if (!belongs) return Results.NotFound();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var selectedTargets = await TreatmentTargetsAsync(activeRows, employerId, db, protector, ct);
        var rows = activeRows.Where(row => row.ReportProductId == reportProductId
            && TreatmentTargetFor(row, selectedTargets)
                is FeedbackTreatmentTarget.Deposit or FeedbackTreatmentTarget.Employee)
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
        AlphaDbContext db,
        IOrganizationAuthorization access,
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

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var selectedTargets = await TreatmentTargetsAsync(activeRows, employerId, db, protector, ct);
        var rows = activeRows.Where(row => row.ReportProductId == reportProductId
            && TreatmentTargetFor(row, selectedTargets)
                is FeedbackTreatmentTarget.Deposit or FeedbackTreatmentTarget.Employee)
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
        AlphaDbContext db,
        IOrganizationAuthorization access,
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

        if (selectedProblems.Length == 0 || selectedProblems.Any(problem => !problem.TreatmentCanCorrect))
            return Results.BadRequest(new { error = "resolution_action_not_allowed" });

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
        AlphaDbContext db,
        IOrganizationAuthorization access,
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
        AlphaDbContext db,
        IOrganizationAuthorization access,
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
        AlphaDbContext db,
        IOrganizationAuthorization access,
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

    private static async Task<IResult> ResolveProblemsAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        ResolveProblemsRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
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
        AlphaDbContext db,
        IOrganizationAuthorization access,
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

    private static async Task<FeedbackResolutionContextResponse> BuildResolutionContextAsync(
        string contextType,
        Guid organizationId,
        Guid employerId,
        Guid? requestedReportId,
        Guid? requestedReportProductId,
        bool canCreateReport,
        bool canEditEmployee,
        IReadOnlyCollection<EmployerInterfaceContributionFeedback> rows,
        AlphaDbContext db,
        IDataProtectionService protector,
        CancellationToken ct,
        bool canEditEmployer = false)
    {
        if (rows.Count == 0)
            return new FeedbackResolutionContextResponse(
                contextType, employerId, requestedReportId, requestedReportProductId,
                false, canCreateReport, canEditEmployee,
                Array.Empty<int>(), Array.Empty<FeedbackResolutionProblemDto>(),
                Array.Empty<FeedbackResolutionGroupDto>());

        var dbTreatments = await FeedbackTreatmentDbReader.LoadAsync(db, ct);
        var targetSelections = await TreatmentTargetsAsync(rows, employerId, db, protector, ct);
        var unsupportedCodes = rows
            .Where(x => x.ErrorCode.HasValue && (!FeedbackResolutionPlaybookCatalog.TryGet(x.ErrorCode.Value, out _) || !dbTreatments.ContainsKey(x.ErrorCode.Value)))
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
        var negativeSourceReportIds = reports.Values
            .Where(report => report.ReportKind == ManualReportKind.Negative && report.SourceReportId.HasValue)
            .Select(report => report.SourceReportId!.Value).Distinct().ToArray();
        var sourceReportProducts = await (
            from candidate in db.ManualReportProducts.AsNoTracking()
            join sourceEmployee in db.ManualReportEmployees.AsNoTracking()
                on candidate.ReportEmployeeId equals sourceEmployee.Id
            where negativeSourceReportIds.Contains(sourceEmployee.ReportId)
                && sourceEmployee.OrganizationId == organizationId && sourceEmployee.EmployerId == employerId
            select new { candidate.Id, sourceEmployee.ReportId, sourceEmployee.EmploymentId,
                candidate.ProductType, candidate.FundCode, candidate.PolicyNumber,
                candidate.SalaryMonth, candidate.AllocationOrder }).ToListAsync(ct);
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

        // Re-evaluate local document evidence whenever a feedback context is requested.
        // A newer library document is a pending report correction, never manufacturer approval.
        var employerDocuments = await db.EmployerDocuments.AsNoTracking()
            .Where(doc => doc.OrganizationId == organizationId && doc.EmployerId == employerId)
            .Select(doc => new { doc.DocumentTypeCode, doc.EmploymentId, doc.EmployeePensionProductId, doc.CreatedAt })
            .ToListAsync(ct);

        var currentEmployerUpdatedAt = await db.Employers.AsNoTracking()
            .Where(x => x.Id == employerId && x.OrganizationId == organizationId)
            .Select(x => (DateTimeOffset?)x.UpdatedAt)
            .SingleOrDefaultAsync(ct);

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
            .Select(tx => new { tx.ReportId, tx.Id, tx.RoutingKey }).ToListAsync(ct);
        var latestTransmissionIds = documentLatestTransmissions
            .GroupBy(tx => tx.ReportId).ToDictionary(group => group.Key, group =>
                group.GroupBy(tx => string.IsNullOrWhiteSpace(tx.RoutingKey)
                        ? "legacy" : tx.RoutingKey, StringComparer.OrdinalIgnoreCase)
                    .Select(route => route.First().Id).ToHashSet());
        var documentFeedback = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(feedback => feedback.ReportId.HasValue
                && returnedDocumentIds.Contains(feedback.ReportId.Value))
            .Select(feedback => new { feedback.ReportId, feedback.TransmissionId })
            .ToListAsync(ct);
        var returnedWorkspaceIds = transmittedDocumentMap
            .Where(document => latestTransmissionIds.TryGetValue(document.Id, out var latestTxIds)
                && documentFeedback.Any(feedback => feedback.ReportId == document.Id
                    && feedback.TransmissionId.HasValue
                    && latestTxIds.Contains(feedback.TransmissionId.Value)))
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

            var contextResolver = playbook.Code == 18
                && EmployerInterface006FieldMapInitializer.Code18FieldNumber(row.ErrorAmount) is int missingNumber
                && EmployerInterface006FieldMapInitializer.EditSurface(
                    report.ReportKind == ManualReportKind.Negative ? 13 : 12, missingNumber) == "employeeProfile"
                ? FeedbackResolverType.Employee : playbook.Resolver;
            var reportedValues = ReportedValues(contextResolver, report, reportEmployee, product, contribution, productMetadata, payment, protector);
            var currentValues = CurrentValues(contextResolver, employment, person, liveProduct, liveContribution, currentPaymentAccount, protector);
            var feedbackValues = FeedbackValues(row, transfer)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (playbook.Code is 102 or 103 && report.ReportKind == ManualReportKind.Negative
                && report.SourceReportId.HasValue)
            {
                var matchingSources = sourceReportProducts.Where(candidate =>
                    candidate.ReportId == report.SourceReportId.Value
                    && candidate.EmploymentId == reportEmployee.EmploymentId
                    && candidate.ProductType == product.ProductType
                    && candidate.FundCode == product.FundCode
                    && candidate.PolicyNumber == product.PolicyNumber
                    && candidate.SalaryMonth == product.SalaryMonth
                    && candidate.AllocationOrder == product.AllocationOrder).ToArray();
                var sourceProductId = product.SourceReportProductId
                    ?? (matchingSources.Length == 1 ? matchingSources[0].Id : (Guid?)null);
                if (sourceProductId.HasValue)
                {
                    feedbackValues["negativeCorrectionSourceReportId"] = report.SourceReportId.Value.ToString("D");
                    feedbackValues["negativeCorrectionSourceProductId"] = sourceProductId.Value.ToString("D");
                    feedbackValues["negativeCorrectionSourceMonth"] = report.ReportingMonth.ToString("yyyy-MM");
                }
            }
            var feedbackProblemId = FeedbackResolutionWireProjection.BuildProblemId(
                row.FeedbackId, row.ContributionId, row.Sequence, playbook.Code);
            feedbackValues["resolutionWorkflowStatus"] = returnedProblemIds.Contains(feedbackProblemId)
                ? "feedback-returned-needs-review"
                : transmittedProblemIds.Contains(feedbackProblemId)
                    ? "transmitted-awaiting-feedback"
                    : pendingResolutionIds.Contains(feedbackProblemId)
                    ? "correction-in-progress"
                    : "needs-treatment";
            // Type 3 is employer-wide. Type 6 (collective agreement) belongs to
            // the affected employee pension product in the current official 006 flow.
            // Merely uploading type 3 cannot satisfy code 102/103.
            var requiredDocumentType = playbook.Code switch
            {
                102 or 103 => 6,
                111 or 112 => 3,
                _ => 0
            };
            if (requiredDocumentType != 0
                && employerDocuments.Any(doc => doc.DocumentTypeCode == requiredDocumentType
                    && doc.CreatedAt > row.ReceivedAt
                    && (requiredDocumentType == 3 && doc.EmploymentId == null
                        || requiredDocumentType == 6 && liveProduct is not null
                            && doc.EmploymentId == reportEmployee.EmploymentId
                            && doc.EmployeePensionProductId == liveProduct.Id))
                && feedbackValues["resolutionWorkflowStatus"] == "needs-treatment")
                feedbackValues["resolutionWorkflowStatus"] = "document-updated-awaiting-report";
            var previousRecordIdentifier = contribution?.PreviousRecordIdentifier;
            var treatmentTarget = TreatmentTargetFor(row, targetSelections);
            // Re-check the freshness of the source profile for every affected
            // historical report on read, regardless of which screen saved it.
            // This is a local change signal, never a substitute for official feedback.
            if (feedbackValues["resolutionWorkflowStatus"] == "needs-treatment"
                && playbook.ResolutionType == FeedbackResolutionType.Edit)
            {
                var sourceUpdatedAt = treatmentTarget switch
                {
                    FeedbackTreatmentTarget.Employer => currentEmployerUpdatedAt,
                    FeedbackTreatmentTarget.Employee => new DateTimeOffset?[]
                    {
                        employment?.UpdatedAt, person?.UpdatedAt
                    }.Max(),
                    FeedbackTreatmentTarget.Deposit => liveProduct?.UpdatedAt,
                    _ => null
                };
                if (sourceUpdatedAt > row.ReceivedAt)
                    feedbackValues["resolutionWorkflowStatus"] = "source-data-updated-awaiting-report";
            }

            if (playbook.Code == 18)
            {
                var missingFieldNumber = EmployerInterface006FieldMapInitializer.Code18FieldNumber(row.ErrorAmount);
                // Error code 18 is the only case where the official workbook defines
                // PERUT-SHGIHA-SHUM as a numbered missing field.
                var sourceInterfaceType = report.ReportKind == ManualReportKind.Negative ? 13 : 12;
                var missingField = missingFieldNumber.HasValue
                    ? EmployerInterface006FieldMapInitializer.Find(sourceInterfaceType, missingFieldNumber.Value)
                    : null;
                feedbackValues["missingFieldNumber"] = missingFieldNumber?.ToString(CultureInfo.InvariantCulture);
                feedbackValues["missingFieldName"] = missingField?.FieldName;
                feedbackValues["missingFieldXmlTag"] = missingField?.XmlTag;
                feedbackValues["missingFieldXmlBlock"] = missingField?.XmlBlock;
                feedbackValues["missingFieldInterfaceType"] = sourceInterfaceType.ToString(CultureInfo.InvariantCulture);
                feedbackValues["missingFieldMapped"] = (missingField is not null) ? "true" : "false";
                feedbackValues["missingFieldCorrectionSupported"] = report.ReportKind == ManualReportKind.Current ? "true" : "false";
                feedbackValues["missingFieldEditSurface"] = missingFieldNumber.HasValue
                    ? EmployerInterface006FieldMapInitializer.EditSurface(sourceInterfaceType, missingFieldNumber.Value)
                    : "unknown";
                if (missingField is not null)
                {
                    // Prefer the actual XML block over a hardcoded classification by error code.
                    treatmentTarget = Code18TargetForField(missingField);
                }
                // Missing/non-integer/unmapped field numbers keep the safe original scope.
            }
            var effectiveStrategy = treatmentTarget == FeedbackTreatmentTarget.Report
                ? FeedbackResolutionGroupStrategy.PerReport
                : playbook.Code == 56 && treatmentTarget == FeedbackTreatmentTarget.Deposit
                    ? FeedbackResolutionGroupStrategy.PerTransfer : playbook.GroupStrategy;
            // Conditional missing-field errors must never group unrelated numbered
            // fields or deposit identities under a single code-only workspace.
            var groupKey = playbook.Code == 18
                ? $"conditionalField:report:{row.ReportId:N}:field:{feedbackValues.GetValueOrDefault("missingFieldNumber") ?? "unknown"}:target:{treatmentTarget}:entity:{(treatmentTarget == FeedbackTreatmentTarget.Deposit || feedbackValues.GetValueOrDefault("missingFieldEditSurface") == "reportTransfer" ? row.ReportProductId.ToString("N") : "shared")}"
                : treatmentTarget == FeedbackTreatmentTarget.Report
                ? $"{FeedbackResolutionWireProjection.WireName(playbook.Resolver)}:report:{row.ReportId:N}:code:{playbook.Code}"
                : playbook.Code == 56 && treatmentTarget == FeedbackTreatmentTarget.Deposit
                    ? $"payment:deposit:{row.ReportId:N}:{row.ReportProductId:N}"
                : FeedbackResolutionWireProjection.BuildResolutionGroupKey(
                    playbook, employerId, row.ReportId, row.ReportProductId, row.ContributionId,
                    reportEmployee.EmploymentId, transferIdentifier, previousRecordIdentifier, row.FeedbackId, row.Sequence);

            // Only mapped deposit-scoped conditional fields can enter the
            // immutable correction workspace. Header/integration fields never
            // receive deposit write permissions just because their code is 18.
            // Field classification is not proof the corresponding editor can actually edit it.
            // The embedded deposit editor currently edits payment + employee-status metadata,
            // not product identity, contribution rows, or arbitrary report-transfer fields.
            var editSurface = feedbackValues.GetValueOrDefault("missingFieldEditSurface");
            var depositConditionalEdit = playbook.Code == 18
                && report.ReportKind == ManualReportKind.Current
                && feedbackValues.GetValueOrDefault("missingFieldMapped") == "true"
                && treatmentTarget == FeedbackTreatmentTarget.Deposit
                && editSurface == "depositEmployeeStatus"
                // These values are actually fields of the scoped payment/006 metadata editor.
                // 70 (salary month), 72 (salary layer), 73 (salary), 78 (policy)
                // come from the report product, not the payment editor.
                && EmployerInterface006FieldMapInitializer.Code18FieldNumber(row.ErrorAmount) is 71 or 74 or 75 or 76 or 77;
            // Only exact product/contribution and payment fields may enter the workspace.
            // Route source-owned product rows to their report-product editor, not payment.
            var code18Field = EmployerInterface006FieldMapInitializer.Code18FieldNumber(row.ErrorAmount);
            var productConditionalEdit = playbook.Code == 18
                && report.ReportKind == ManualReportKind.Current
                && feedbackValues.GetValueOrDefault("missingFieldMapped") == "true"
                && treatmentTarget == FeedbackTreatmentTarget.Deposit
                && code18Field is 59 or 70 or 72 or 73 or 78 or 79 or 80 or 81 or 82;
            var transferConditionalEdit = playbook.Code == 18
                && report.ReportKind == ManualReportKind.Current
                && feedbackValues.GetValueOrDefault("missingFieldMapped") == "true"
                && editSurface == "reportTransfer"
                && code18Field is 35 or 37 or 38 or 40 or 41 or 42 or 44 or 45 or 46 or 49 or 50 or 51 or 52 or 53;
            var oldPensionConditionalEdit = playbook.Code == 18
                && report.ReportKind == ManualReportKind.Current
                && feedbackValues.GetValueOrDefault("missingFieldMapped") == "true"
                && treatmentTarget == FeedbackTreatmentTarget.Deposit
                && code18Field == 60;
            var supportedConditionalEdit = depositConditionalEdit || productConditionalEdit || transferConditionalEdit || oldPensionConditionalEdit;
            var conditionalEmployeeProfile = playbook.Code == 18
                && report.ReportKind == ManualReportKind.Current
                && feedbackValues.GetValueOrDefault("missingFieldMapped") == "true"
                && treatmentTarget == FeedbackTreatmentTarget.Deposit
                && editSurface == "employeeProfile";

            problems.Add(new FeedbackResolutionProblemDto(
                ProblemId: FeedbackResolutionWireProjection.BuildProblemId(row.FeedbackId, row.ContributionId, row.Sequence, playbook.Code),
                Code: playbook.Code,
                Description: string.IsNullOrWhiteSpace(row.ErrorDescription)
                    ? EmployerInterfaceLineFeedbackParser.Description(row.ErrorCode)
                    : row.ErrorDescription,
                Scope: FeedbackResolutionWireProjection.WireName(playbook.Scope),
                TargetScope: FeedbackResolutionWireProjection.WireName(treatmentTarget),
                ResolutionType: FeedbackResolutionWireProjection.WireName(supportedConditionalEdit || conditionalEmployeeProfile ? FeedbackResolutionType.Edit : playbook.ResolutionType),
                Family: FeedbackResolutionWireProjection.WireName(playbook.Family),
                ResolverType: FeedbackResolutionWireProjection.WireName(conditionalEmployeeProfile ? FeedbackResolverType.Employee : playbook.Resolver),
                GroupStrategy: FeedbackResolutionWireProjection.WireName(effectiveStrategy),
                GroupKey: groupKey,
                CorrectionBehavior: FeedbackResolutionWireProjection.WireName(supportedConditionalEdit || conditionalEmployeeProfile ? FeedbackCorrectionBehavior.CorrectionWorkspace : playbook.CorrectionBehavior),
                AvailableActions: FeedbackResolutionWireProjection.ActionNames(conditionalEmployeeProfile
                    ? FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.PrepareCorrection
                    : supportedConditionalEdit ? FeedbackResolutionAction.PrepareCorrection : playbook.Actions),
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
                ReceivedAt: row.ReceivedAt,
                TreatmentChoice: dbTreatments[playbook.Code].Choice,
                TreatmentCanCorrect: dbTreatments[playbook.Code].CanCorrect,
                TreatmentCanInquire: dbTreatments[playbook.Code].CanInquire,
                CorrectionTarget: dbTreatments[playbook.Code].CorrectionTarget,
                CorrectionFields: dbTreatments[playbook.Code].CorrectionFields,
                ExternalRecipientType: dbTreatments[playbook.Code].RecipientType));
        }

        if (problems.Count > 0)
        {
            var problemIds = problems.Select(problem => problem.ProblemId).ToArray();
            var latestDecisions = await db.FeedbackProblemDecisions.AsNoTracking()
                .Where(decision => problemIds.Contains(decision.ProblemId)
                    && !decision.Outcome.StartsWith("target:"))
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
            problems, canCreateReport, canEditEmployee, canEditEmployer);
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
}
