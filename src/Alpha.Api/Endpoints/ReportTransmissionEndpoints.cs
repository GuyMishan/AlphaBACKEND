using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Entitlements;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportTransmissionEndpoints
{
    public static IEndpointRouteBuilder MapReportTransmissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports").RequireAuthorization().WithTags("Report transmission");
        group.MapGet("/{reportId:guid}/transmissions", GetHistoryAsync);
        group.MapPost("/{reportId:guid}/transmissions", SendAsync);
        return endpoints;
    }

    private static async Task<IResult> GetHistoryAsync(Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct)) return Results.NotFound();
        return Results.Ok(await db.ReportTransmissions.AsNoTracking().Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId).OrderByDescending(x => x.AttemptNumber).Select(x => new { x.Id, x.Provider, x.AttemptNumber, x.Status, x.ExternalId, x.PayloadHash, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt }).ToListAsync(ct));
    }

    private static async Task<IResult> SendAsync(Guid organizationId, Guid employerId, Guid reportId, SendReportRequest? request, IAlphaDbContext db, OrganizationAccessService access, EntitlementService entitlements, IEnumerable<IReportTransmissionProvider> providers, EmployerInterface006ExportService exporter, CancellationToken ct)
    {
        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var entitlement = await entitlements.CanTransmitReport(organizationId, ct);
        if (!entitlement.Allowed)
            return Results.Json(new { error = entitlement.Error, feature = entitlement.Feature }, statusCode: StatusCodes.Status409Conflict);
        var report = await db.ManualReports.FirstOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Validated) return Results.Conflict(new { error = "Only a report that passed final validation can be transmitted.", status = report.Status.ToString() });

        var providerName = string.IsNullOrWhiteSpace(request?.Provider) ? "MockClearinghouse" : request.Provider.Trim();
        var provider = providers.FirstOrDefault(x => string.Equals(x.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return Results.BadRequest(new { error = "The selected transmission provider does not exist.", provider = providerName });

        var generated = await exporter.ExportAsync(report, ct);
        if (!generated.Validation.IsValid)
            return Results.BadRequest(new { error = "Generated Employer Interface XML failed its report-type-specific official Version 006 XSD validation and was not transmitted.", generated.Validation });

        var payloadBytes = generated.Bytes;
        var hash = EmployerInterfaceService.Hash(payloadBytes);
        var attemptNumber = (await db.ReportTransmissions.Where(x => x.ReportId == reportId).MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
        var transmission = new ReportTransmission(reportId, organizationId, employerId, provider.Name, attemptNumber);
        transmission.Start(hash);
        db.ReportTransmissions.Add(transmission);
        report.MarkTransmissionStarted();
        await db.SaveChangesAsync(ct);

        try
        {
            var result = await provider.SendAsync(new ReportTransmissionEnvelope(reportId, organizationId, employerId, payloadBytes, hash), ct);
            transmission.Complete(result.Success ? ReportTransmissionStatus.Accepted : ReportTransmissionStatus.Rejected, result.ExternalId, result.ResponsePayload, result.ErrorMessage);
            if (result.Success) report.MarkSent(); else report.MarkTransmissionError(result.ErrorMessage ?? "The report was rejected by the transmission provider.");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(report, transmission, generated.Validation));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            transmission.Complete(ReportTransmissionStatus.Error, null, null, ex.Message);
            report.MarkTransmissionError(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(ToResponse(report, transmission, generated.Validation), statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static object ToResponse(ManualReport report, ReportTransmission transmission, EmployerInterfaceService.FileValidation validation) => new
    {
        reportId = report.Id,
        reportStatus = report.Status,
        payloadFormat = "EmployerInterfaceXml",
        interfaceVersion = EmployerInterfaceService.CurrentVersion,
        documentType = validation.DocumentType?.ToString(),
        schema = validation.SchemaFileName,
        transmission = new { transmission.Id, transmission.Provider, transmission.AttemptNumber, transmission.Status, transmission.ExternalId, transmission.PayloadHash, transmission.ErrorMessage, transmission.StartedAt, transmission.SentAt, transmission.CompletedAt }
    };

    public sealed record SendReportRequest(string? Provider);
}
