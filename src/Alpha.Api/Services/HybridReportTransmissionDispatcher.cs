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

    /// <summary>
    /// Resume only unattempted recipient packages or explicitly rejected ones.
    /// A Sending/Error attempt is ambiguous and must be reconciled manually.
    /// Never redispatch a successfully accepted recipient.
    /// </summary>
    public static async Task<IResult> ResumeAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, IReadOnlyCollection<IReportTransmissionProvider> availableProviders,
        EmployerInterface006ExportService exporter,
        EmployerInterfaceFileSequenceService fileSequences,
        IDataProtectionService protector, CancellationToken ct)
    {
        var report = await db.ManualReports.SingleOrDefaultAsync(item =>
            item.Id == reportId && item.OrganizationId == organizationId
                && item.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status is not (ManualReportStatus.Processing or ManualReportStatus.Sent))
            return Results.Conflict(new { error = "manufacturer_route_not_resumable" });

        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(item => item.ReportId == reportId)
            .OrderByDescending(item => item.AttemptNumber).ToListAsync(ct);
        if (transmissions.Count < 2
            || transmissions.Any(item => string.IsNullOrWhiteSpace(item.RoutingKey)))
            return Results.Conflict(new { error = "manufacturer_route_no_hybrid_plan" });
        var latestRoutes = transmissions.GroupBy(item => item.RoutingKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.RoutingKey, StringComparer.OrdinalIgnoreCase).ToArray();

        if (latestRoutes.Any(item => item.Status is ReportTransmissionStatus.Sending
            or ReportTransmissionStatus.Error))
            return Results.Conflict(new { error = "manufacturer_route_reconciliation_required" });

        foreach (var previous in latestRoutes)
        {
            if (previous.Status == ReportTransmissionStatus.Accepted) continue;
            var provider = availableProviders.FirstOrDefault(item =>
                string.Equals(item.Name, previous.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider is null || !provider.IsConfigured)
                return Results.Json(new { error = "manufacturer_route_provider_unavailable",
                    provider = previous.Provider }, statusCode: StatusCodes.Status503ServiceUnavailable);

            ReportTransmission target;
            ReportTransmissionEnvelope envelope;
            if (previous.Status == ReportTransmissionStatus.Pending)
            {
                // The prepared payload and its attachments are immutable evidence;
                // a process restart must never create a different package.
                target = previous;
                envelope = await RebuildEnvelopeAsync(previous, organizationId, employerId,
                    db, protector, ct);
            }
            else if (previous.Status == ReportTransmissionStatus.Rejected)
            {
                // Definitive provider rejection permits a new, uniquely named
                // attempt; prior attempts remain immutable for audit.
                Guid[] productIds;
                try
                {
                    productIds = JsonSerializer.Deserialize<Guid[]>(
                        previous.RoutedProductIdsJson) ?? [];
                }
                catch (JsonException)
                {
                    return Results.Conflict(new { error = "manufacturer_route_scope_invalid" });
                }
                if (productIds.Length == 0)
                    return Results.Conflict(new { error = "manufacturer_route_scope_invalid" });

                var reservation = await fileSequences.ReserveAsync(employerId, ct);
                var generated = await exporter.ExportAsync(report, ct,
                    reservation.Sequence, reservation.PreparedAt, productIds);
                if (!generated.Validation.IsValid || string.IsNullOrWhiteSpace(generated.PayloadFileName))
                    return Results.Conflict(new { error = "manufacturer_route_retry_validation_failed",
                        validation = generated.Validation });

                var attachments = (generated.AttachmentFiles ?? [])
                    .Select(file => new ReportTransmissionAttachment(file.FileName,
                        file.ContentType, file.Content, file.Sha256)).ToArray();
                var attemptNumber = (await db.ReportTransmissions
                    .Where(item => item.ReportId == reportId)
                    .MaxAsync(item => (int?)item.AttemptNumber, ct) ?? 0) + 1;
                target = new ReportTransmission(reportId, organizationId, employerId,
                    previous.Provider, attemptNumber);
                target.ConfigureRoute(previous.RoutingKey, productIds);
                var hash = EmployerInterfaceService.Hash(generated.Bytes);
                var manifest = JsonSerializer.Serialize(attachments.Select(item => new
                {
                    item.FileName, item.ContentType, sizeBytes = item.Content.LongLength, item.Sha256
                }));
                target.Prepare(hash, generated.PayloadFileName,
                    protector.ProtectBytes(generated.Bytes, $"report-transmission:{target.Id}"), manifest);
                db.ReportTransmissions.Add(target);
                try
                {
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateException)
                {
                    return Results.Conflict(new { error = "manufacturer_route_concurrent_retry" });
                }
                envelope = new ReportTransmissionEnvelope(reportId, organizationId, employerId,
                    generated.Bytes, hash, attachments, generated.PayloadFileName);
            }
            else
            {
                return Results.Conflict(new { error = "manufacturer_route_invalid_attempt_state" });
            }

            var claimed = await db.ReportTransmissions
                .Where(item => item.Id == target.Id && item.ReportId == reportId
                    && item.Status == ReportTransmissionStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, ReportTransmissionStatus.Sending)
                    .SetProperty(item => item.StartedAt, DateTimeOffset.UtcNow)
                    .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            if (claimed != 1)
                return Results.Conflict(new { error = "manufacturer_route_concurrent_dispatch" });

            // ExecuteUpdate does not synchronize tracked entities. Only attach
            // the updated transmission after successfully claiming the row.
            if (db is DbContext ef)
            {
                var tracked = ef.ChangeTracker.Entries<ReportTransmission>()
                    .FirstOrDefault(entry => entry.Entity.Id == target.Id);
                if (tracked is not null) tracked.State = EntityState.Detached;
            }
            var dispatch = await db.ReportTransmissions.SingleAsync(
                item => item.Id == target.Id, CancellationToken.None);
            try
            {
                var result = await provider.SendAsync(envelope, CancellationToken.None);
                dispatch.Complete(result.Success ? ReportTransmissionStatus.Accepted
                        : ReportTransmissionStatus.Rejected,
                    result.ExternalId,
                    string.IsNullOrWhiteSpace(result.ResponsePayload) ? string.Empty
                        : protector.Protect(result.ResponsePayload,
                            $"report-transmission-response:{dispatch.Id}"),
                    result.ErrorMessage);
                await db.SaveChangesAsync(CancellationToken.None);
                if (!result.Success)
                    return Results.Conflict(new { error = "manufacturer_route_partial_rejection",
                        provider = dispatch.Provider, transmissionId = dispatch.Id });
            }
            catch (Exception)
            {
                dispatch.Complete(ReportTransmissionStatus.Error, null, null,
                    "Remote transmission outcome is uncertain; reconciliation required.");
                await db.SaveChangesAsync(CancellationToken.None);
                return Results.Json(new { error = "manufacturer_route_reconciliation_required",
                    provider = dispatch.Provider }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        var allAttempts = await db.ReportTransmissions.AsNoTracking()
            .Where(item => item.ReportId == reportId)
            .OrderByDescending(item => item.AttemptNumber).ToListAsync(CancellationToken.None);
        var allAccepted = allAttempts.GroupBy(item => item.RoutingKey,
                StringComparer.OrdinalIgnoreCase)
            .All(group => group.First().Status == ReportTransmissionStatus.Accepted);
        if (!allAccepted)
            return Results.Conflict(new { error = "manufacturer_route_pending_recipient" });

        report.MarkSent();
        await db.SaveChangesAsync(CancellationToken.None);
        await CorrectionWorkflowService.FinalizeRevisionIfCompleteAsync(reportId, db,
            CancellationToken.None);
        return Results.Ok(new { reportId, reportStatus = report.Status,
            requiresProducerFeedback = true });
    }

    private static async Task<ReportTransmissionEnvelope> RebuildEnvelopeAsync(
        ReportTransmission transmission, Guid organizationId, Guid employerId,
        IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        var bytes = protector.UnprotectBytes(transmission.Payload,
            $"report-transmission:{transmission.Id}");
        if (!string.Equals(EmployerInterfaceService.Hash(bytes), transmission.PayloadHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("manufacturer_route_payload_hash_mismatch");

        var attachments = new List<ReportTransmissionAttachment>();
        var stored = await db.ManualReportAttachments.AsNoTracking()
            .Where(item => item.ReportId == transmission.ReportId).ToListAsync(ct);
        var scope = JsonSerializer.Deserialize<Guid[]>(transmission.RoutedProductIdsJson) ?? [];
        using var manifest = JsonDocument.Parse(transmission.AttachmentManifestJson);
        foreach (var element in manifest.RootElement.EnumerateArray())
        {
            var hash = element.GetProperty("Sha256").GetString() ?? string.Empty;
            var filename = element.GetProperty("FileName").GetString() ?? string.Empty;
            var mime = element.GetProperty("ContentType").GetString() ?? string.Empty;
            var source = stored.FirstOrDefault(item =>
                string.Equals(item.Sha256, hash, StringComparison.OrdinalIgnoreCase)
                && (!item.ReportProductId.HasValue
                    || scope.Contains(item.ReportProductId.Value)));
            if (source is null)
                throw new InvalidOperationException("manufacturer_route_attachment_not_found");
            var content = protector.UnprotectBytes(source.Content,
                $"report-attachment:{source.ReportId}:{source.ReportProductId}:{source.DocumentTypeCode}");
            if (element.GetProperty("sizeBytes").GetInt64() != content.LongLength)
                throw new InvalidOperationException("manufacturer_route_attachment_size_mismatch");
            attachments.Add(new ReportTransmissionAttachment(filename, mime, content, hash));
        }
        return new ReportTransmissionEnvelope(transmission.ReportId, organizationId, employerId,
            bytes, transmission.PayloadHash, attachments.ToArray(), transmission.PayloadFileName);
    }

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
                prepared.Add(new PreparedRecipient(pair.Provider, evidence,
                    new ReportTransmissionEnvelope(reportId, organizationId, employerId,
                        package.Bytes, hash, attachments, package.PayloadFileName)));
            }
            // Persist the complete plan atomically. If one package fails its
            // preflight, no partial destination rows can be accidentally saved.
            foreach (var batch in prepared) db.ReportTransmissions.Add(batch.Transmission);
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
