using System.Text.Json;
using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Application.Reporting;
using Alpha.Domain.Reporting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

/// <summary>
/// Dispatches independently validated recipient packages while retaining a
/// single employer-facing report. Enabled only by explicit feature flag.
/// </summary>
public static class HybridReportTransmissionDispatcher
{
    public sealed record Recipient(string Provider, Guid[] ProductIds);

    private sealed record PreparedRecipient(
        IReportTransmissionProvider Provider,
        ReportTransmission Transmission,
        ReportTransmissionEnvelope Envelope);

    public static async Task<IResult> SendAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, IReadOnlyCollection<Recipient> recipients,
        IReadOnlyCollection<IReportTransmissionProvider> availableProviders,
        EmployerInterface006ExportService exporter,
        EmployerInterfaceFileSequenceService fileSequences,
        IDataProtectionService protector, CancellationToken ct)
    {
        if (recipients.Count < 2 || recipients.Any(route => route.ProductIds.Length == 0)
            || recipients.SelectMany(route => route.ProductIds).Distinct().Count()
                != recipients.Sum(route => route.ProductIds.Length))
            return Results.Conflict(new { error = "manufacturer_route_partition_invalid" });

        var selected = new List<(Recipient Route, IReportTransmissionProvider Provider)>();
        foreach (var route in recipients)
        {
            var provider = availableProviders.SingleOrDefault(item =>
                string.Equals(item.Name, route.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider is null || !provider.IsConfigured)
                return Results.Json(new { error = "manufacturer_route_provider_unavailable",
                    route = route.Provider }, statusCode: StatusCodes.Status503ServiceUnavailable);
            selected.Add((route, provider));
        }

        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == reportId
                && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Validated)
            return Results.Conflict(new { error = "manufacturer_route_report_not_validated" });

        // Preflight all manufacturer-scoped XML/XSD files, before the irreversible
        // transition into Processing or reserving any official file sequence.
        foreach (var pair in selected)
        {
            var preview = await exporter.ExportAsync(report, ct, 1,
                includedProductIds: pair.Route.ProductIds);
            if (!preview.Validation.IsValid)
                return Results.Conflict(new
                {
                    error = "manufacturer_route_package_invalid",
                    provider = pair.Route.Provider,
                    validation = preview.Validation
                });
        }

        var claimed = await db.ManualReports
            .Where(item => item.Id == reportId && item.OrganizationId == organizationId
                && item.EmployerId == employerId && item.Status == ManualReportStatus.Validated)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ManualReportStatus.Processing)
                .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (claimed != 1)
            return Results.Conflict(new { error = "manufacturer_route_report_already_claimed" });

        report = await db.ManualReports.SingleAsync(item => item.Id == reportId, ct);
        var nextAttempt = (await db.ReportTransmissions
            .Where(item => item.ReportId == reportId)
            .MaxAsync(item => (int?)item.AttemptNumber, ct) ?? 0) + 1;
        var prepared = new List<PreparedRecipient>();

        try
        {
            foreach (var pair in selected)
            {
                var reservation = await fileSequences.ReserveAsync(employerId, CancellationToken.None);
                var package = await exporter.ExportAsync(report, CancellationToken.None,
                    reservation.Sequence, reservation.PreparedAt, pair.Route.ProductIds);
                if (!package.Validation.IsValid || string.IsNullOrWhiteSpace(package.PayloadFileName))
                    throw new InvalidOperationException("manufacturer_route_final_package_invalid");

                var hash = EmployerInterfaceService.Hash(package.Bytes);
                var attachments = (package.AttachmentFiles ?? [])
                    .Select(item => new ReportTransmissionAttachment(
                        item.FileName, item.ContentType, item.Content, item.Sha256)).ToArray();
                var evidence = new ReportTransmission(reportId, organizationId, employerId,
                    pair.Provider.Name, nextAttempt++);
                evidence.ConfigureRoute(pair.Provider.Name, pair.Route.ProductIds);
                var manifest = JsonSerializer.Serialize(attachments.Select(item => new
                {
                    item.FileName, item.ContentType, sizeBytes = item.Content.LongLength, item.Sha256
                }));
                evidence.Prepare(hash, package.PayloadFileName,
                    protector.ProtectBytes(package.Bytes, $"report-transmission:{evidence.Id}"), manifest);
                db.ReportTransmissions.Add(evidence);
                prepared.Add(new PreparedRecipient(pair.Provider, evidence,
                    new ReportTransmissionEnvelope(reportId, organizationId, employerId,
                        package.Bytes, hash, attachments, package.PayloadFileName)));
            }
            // Save every independent planned package before the first remote side effect.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            // No provider was contacted, so this failure can be retried after
            // the report's validation error is fixed.
            report.MarkTransmissionError("Failed to prepare recipient-specific transmission packages.");
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Conflict(new { error = "manufacturer_route_preparation_failed" });
        }

        foreach (var batch in prepared)
        {
            try
            {
                batch.Transmission.BeginDispatch();
                await db.SaveChangesAsync(CancellationToken.None);
                var result = await batch.Provider.SendAsync(batch.Envelope, CancellationToken.None);
                batch.Transmission.Complete(
                    result.Success ? ReportTransmissionStatus.Accepted : ReportTransmissionStatus.Rejected,
                    result.ExternalId,
                    string.IsNullOrWhiteSpace(result.ResponsePayload) ? string.Empty
                        : protector.Protect(result.ResponsePayload,
                            $"report-transmission-response:{batch.Transmission.Id}"),
                    result.ErrorMessage);
                await db.SaveChangesAsync(CancellationToken.None);
                if (!result.Success)
                {
                    // Previously accepted recipients must NOT be sent again.
                    // Pending destinations remain explicitly prepared on the ledger.
                    return Results.Conflict(new
                    {
                        error = "manufacturer_route_partial_rejection",
                        rejectedProvider = batch.Provider.Name,
                        reportStatus = "processing",
                        transmissions = prepared.Select(item => new
                        {
                            item.Transmission.Id,
                            item.Transmission.Provider,
                            item.Transmission.RoutingKey,
                            item.Transmission.Status
                        })
                    });
                }
            }
            catch (Exception)
            {
                batch.Transmission.Complete(ReportTransmissionStatus.Error, null, null,
                    "Transmission outcome uncertain; reconciliation required.");
                await db.SaveChangesAsync(CancellationToken.None);
                return Results.Json(new
                {
                    error = "manufacturer_route_reconciliation_required",
                    provider = batch.Provider.Name
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        // All recipient transports succeeded. This is NOT confirmation from
        // institutions; positive feedback remains a separate lifecycle stage.
        report.MarkSent();
        await db.SaveChangesAsync(CancellationToken.None);
        await CorrectionWorkflowService.FinalizeRevisionIfCompleteAsync(reportId, db,
            CancellationToken.None);
        return Results.Ok(new
        {
            reportId, reportStatus = report.Status,
            requiresProducerFeedback = true,
            transmissions = prepared.Select(item => new
            {
                item.Transmission.Id,
                item.Transmission.Provider,
                item.Transmission.RoutingKey,
                item.Transmission.Status
            })
        });
    }
}
