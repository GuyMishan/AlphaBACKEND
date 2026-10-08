using System.Text.Json;
using Alpha.Api.Services;
using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Billing;
using Alpha.Application.Entitlements;
using Alpha.Application.Reporting;
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
        return Results.Ok(await db.ReportTransmissions.AsNoTracking().Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId).OrderByDescending(x => x.AttemptNumber).Select(x => new { x.Id, x.Provider, x.AttemptNumber, x.Status, x.ExternalId, x.PayloadHash, x.PayloadFileName, payloadSizeBytes = x.Payload.Length, x.AttachmentManifestJson, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt }).ToListAsync(ct));
    }

    private static async Task<IResult> SendAsync(Guid organizationId, Guid employerId, Guid reportId, SendReportRequest? request,
        IAlphaDbContext db, OrganizationAccessService access, EntitlementService entitlements,
        ReportPaymentAccountService paymentAccounts, BillingGateService billingGate,
        IEnumerable<IReportTransmissionProvider> providers, EmployerInterface006ExportService exporter,
        EmployerInterfaceFileSequenceService fileSequences, IDataProtectionService protector,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var entitlement = await entitlements.CanTransmitReport(organizationId, ct);
        if (!entitlement.Allowed)
            return Results.Json(new { error = entitlement.Error, feature = entitlement.Feature }, statusCode: StatusCodes.Status409Conflict);
        var report = await db.ManualReports.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Validated) return Results.Conflict(new { error = "Only a report that passed final validation can be transmitted.", status = report.Status.ToString() });

        var paymentValidation = await paymentAccounts.ValidateForTransmissionAsync(report, ct);
        if (!paymentValidation.IsValid)
            return Results.Conflict(new { error = paymentValidation.Error });

        var billingDecision = await billingGate.CanTransmitAsync(employerId, ct);
        if (!billingDecision.Allowed)
            return Results.Conflict(new
            {
                error = billingDecision.Error,
                billingMode = billingDecision.BillingMode,
                source = billingDecision.Source,
                billedThroughName = billingDecision.BilledThroughName,
                paymentMethodType = billingDecision.PaymentMethodType,
                paymentMethodStatus = billingDecision.PaymentMethodStatus,
                configured = billingDecision.Configured
            });

        var availableProviders = providers.ToArray();
        var providerName = string.IsNullOrWhiteSpace(request?.Provider)
            ? availableProviders.FirstOrDefault(x => x.IsConfigured)?.Name
                ?? availableProviders.FirstOrDefault()?.Name
                ?? string.Empty
            : request.Provider.Trim();
        // Resolve the full report's actual fund destinations *before* claiming the report.
        // Never transmit a mixed-route 006 file to a single recipient: this would
        // leak other manufacturers' employee data and invalidate feedback correlation.
        var manufacturerFunds = await (
            from product in db.ManualReportProducts.AsNoTracking()
            join employee in db.ManualReportEmployees.AsNoTracking()
                on product.ReportEmployeeId equals employee.Id
            where employee.ReportId == reportId
            select product.FundCode
        ).ToArrayAsync(ct);
        ManufacturerTransmissionRouting.Plan routingPlan;
        try
        {
            routingPlan = ManufacturerTransmissionRouting.Resolve(manufacturerFunds,
                providerName, configuration);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        if (!string.Equals(routingPlan.Provider, providerName, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new
            {
                error = "manufacturer_route_override_conflict",
                requiredProvider = routingPlan.Provider
            });

        var provider = availableProviders.FirstOrDefault(x => string.Equals(x.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return Results.BadRequest(new { error = "The selected transmission provider does not exist.", provider = providerName });
        if (!provider.IsConfigured)
            return Results.Problem(
                title: "Clearing-house transmission is not configured.",
                detail: "A real transmission provider must be configured before reports can be sent.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        // Atomically claim the validated report before reserving a file number or contacting
        // the provider. Only one concurrent sender can transition Validated -> Processing.
        var claimed = await db.ManualReports
            .Where(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
                && x.Status == ManualReportStatus.Validated)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ManualReportStatus.Processing)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (claimed != 1)
            return Results.Conflict(new { error = "The report is already being transmitted or is no longer validated." });

        report = await db.ManualReports.SingleAsync(x => x.Id == reportId, ct);

        EmployerInterfaceFileSequenceService.Reservation reservation;
        try
        {
            reservation = await fileSequences.ReserveAsync(employerId, ct);
        }
        catch (InvalidOperationException)
        {
            report.MarkTransmissionError("Transmission file sequence could not be reserved.");
            await db.SaveChangesAsync(ct);
            return Results.Conflict(new { error = "Transmission file sequence could not be reserved." });
        }

        var generated = await exporter.ExportAsync(report, ct, reservation.Sequence, reservation.PreparedAt);
        if (!generated.Validation.IsValid)
        {
            report.MarkTransmissionError("Generated Employer Interface 006 payload failed final validation.");
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "Generated Employer Interface XML failed its report-type-specific official Version 006 XSD validation and was not transmitted.", generated.Validation });
        }

        var payloadBytes = generated.Bytes;
        if (string.IsNullOrWhiteSpace(generated.PayloadFileName))
        {
            report.MarkTransmissionError("Generated Employer Interface 006 payload did not include an official file name.");
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "Generated Employer Interface 006 payload did not include an official DAT/TST file name." });
        }
        var payloadFileName = generated.PayloadFileName;
        var hash = EmployerInterfaceService.Hash(payloadBytes);
        var attachmentFiles = (generated.AttachmentFiles ?? [])
            .Select(x => new ReportTransmissionAttachment(x.FileName, x.ContentType, x.Content, x.Sha256))
            .ToArray();
        var attemptNumber = (await db.ReportTransmissions.Where(x => x.ReportId == reportId).MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
        var transmission = new ReportTransmission(reportId, organizationId, employerId, provider.Name, attemptNumber);
        var attachmentManifestJson = JsonSerializer.Serialize(attachmentFiles.Select(x => new
        {
            x.FileName,
            x.ContentType,
            sizeBytes = x.Content.LongLength,
            x.Sha256
        }));
        transmission.Start(hash, payloadFileName, protector.ProtectBytes(payloadBytes, $"report-transmission:{transmission.Id}"), attachmentManifestJson);
        db.ReportTransmissions.Add(transmission);
        await db.SaveChangesAsync(ct);

        try
        {
            // After the transmission row is durably persisted, the provider call is an irreversible side effect.
            // Do not tie it to the HTTP request-abort token or a client disconnect could leave a real send
            // stuck locally in Processing/Sending and invite an unsafe duplicate retry.
            var result = await provider.SendAsync(new ReportTransmissionEnvelope(reportId, organizationId, employerId,
                payloadBytes, hash, attachmentFiles, payloadFileName), CancellationToken.None);
            transmission.Complete(result.Success ? ReportTransmissionStatus.Accepted : ReportTransmissionStatus.Rejected, result.ExternalId,
                string.IsNullOrWhiteSpace(result.ResponsePayload) ? string.Empty : protector.Protect(result.ResponsePayload, $"report-transmission-response:{transmission.Id}"),
                result.ErrorMessage);
            if (result.Success) report.MarkSent(); else report.MarkTransmissionError(result.ErrorMessage ?? "The report was rejected by the transmission provider.");
            await db.SaveChangesAsync(CancellationToken.None);
            if (result.Success)
                await CorrectionWorkflowService.FinalizeRevisionIfCompleteAsync(report.Id, db, CancellationToken.None);
            return Results.Ok(ToResponse(report, transmission, generated.Validation));
        }
        catch (Exception)
        {
            // A provider exception is an ambiguous outcome: the remote clearing house may have
            // accepted the payload before the connection failed. Keep the report in Processing
            // so validation/transmission cannot be retried blindly and create a duplicate send.
            // Reconciliation must determine the external outcome before any further irreversible action.
            transmission.Complete(ReportTransmissionStatus.Error, null, null, "Transmission outcome is uncertain; reconciliation is required.");
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(new
            {
                error = "transmission_reconciliation_required",
                detail = "The clearing-house outcome is uncertain. The report remains locked in Processing until the transmission is reconciled.",
                result = ToResponse(report, transmission, generated.Validation)
            }, statusCode: StatusCodes.Status502BadGateway);
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
        transmission = new { transmission.Id, transmission.Provider, transmission.AttemptNumber, transmission.Status, transmission.ExternalId, transmission.PayloadHash, transmission.PayloadFileName, payloadSizeBytes = transmission.Payload.Length, transmission.AttachmentManifestJson, transmission.ErrorMessage, transmission.StartedAt, transmission.SentAt, transmission.CompletedAt }
    };

    public sealed record SendReportRequest(string? Provider);
}
