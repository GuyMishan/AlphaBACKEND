using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportTransmissionEndpoints
{
    public static IEndpointRouteBuilder MapReportTransmissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports").RequireAuthorization().WithTags("Report transmission");
        group.MapGet("/{reportId:guid}/transmissions", GetHistoryAsync); group.MapPost("/{reportId:guid}/transmissions", SendAsync); return endpoints;
    }
    private static async Task<IResult> GetHistoryAsync(Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct)) return Results.NotFound();
        return Results.Ok(await db.ReportTransmissions.AsNoTracking().Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId).OrderByDescending(x => x.AttemptNumber).Select(x => new { x.Id, x.Provider, x.AttemptNumber, x.Status, x.ExternalId, x.PayloadHash, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt }).ToListAsync(ct));
    }
    private static async Task<IResult> SendAsync(Guid organizationId, Guid employerId, Guid reportId, SendReportRequest? request, IAlphaDbContext db, OrganizationAccessService access, IEnumerable<IReportTransmissionProvider> providers, EmployerInterfaceService employerInterface, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.FirstOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct); if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Validated) return Results.Conflict(new { error = "ניתן לשלוח רק דיווח שעבר ולידציה סופית בהצלחה.", status = report.Status.ToString() });
        var providerName = string.IsNullOrWhiteSpace(request?.Provider) ? "MockClearinghouse" : request.Provider.Trim(); var provider = providers.FirstOrDefault(x => string.Equals(x.Name, providerName, StringComparison.OrdinalIgnoreCase)); if (provider is null) return Results.BadRequest(new { error = "ספק השליחה שנבחר אינו קיים.", provider = providerName });
        var payload = await employerInterface.ExportAsync(report, ct); var validation = employerInterface.Validate(payload, "employer-interface"); if (!validation.IsValid) return Results.BadRequest(new { error = "ממשק המעסיקים שנוצר אינו תקין.", validation.Issues });
        var hash = EmployerInterfaceService.Hash(payload); var attemptNumber = (await db.ReportTransmissions.Where(x => x.ReportId == reportId).MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
        var transmission = new ReportTransmission(reportId, organizationId, employerId, provider.Name, attemptNumber); transmission.Start(hash); db.ReportTransmissions.Add(transmission); report.MarkTransmissionStarted(); await db.SaveChangesAsync(ct);
        try { var result = await provider.SendAsync(new ReportTransmissionEnvelope(reportId, organizationId, employerId, payload, hash), ct); transmission.Complete(result.Success ? ReportTransmissionStatus.Accepted : ReportTransmissionStatus.Rejected, result.ExternalId, result.ResponsePayload, result.ErrorMessage); if (result.Success) report.MarkSent(); else report.MarkTransmissionError(result.ErrorMessage ?? "הדיווח נדחה על ידי ספק השליחה."); await db.SaveChangesAsync(ct); return Results.Ok(ToResponse(report, transmission)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { transmission.Complete(ReportTransmissionStatus.Error, null, null, ex.Message); report.MarkTransmissionError(ex.Message); await db.SaveChangesAsync(CancellationToken.None); return Results.Json(ToResponse(report, transmission), statusCode: StatusCodes.Status502BadGateway); }
    }
    private static object ToResponse(ManualReport report, ReportTransmission transmission) => new { reportId = report.Id, reportStatus = report.Status, payloadFormat = "EmployerInterfaceXml", interfaceVersion = EmployerInterfaceService.CurrentVersion, transmission = new { transmission.Id, transmission.Provider, transmission.AttemptNumber, transmission.Status, transmission.ExternalId, transmission.PayloadHash, transmission.ErrorMessage, transmission.StartedAt, transmission.SentAt, transmission.CompletedAt } };
    public sealed record SendReportRequest(string? Provider);
}
