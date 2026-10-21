using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Billing.Contracts;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Contracts;
using Alpha.Modules.Reporting.Application;
using Alpha.Modules.Reporting.Features.EmployerInterface006;
using Alpha.Modules.Reporting.Features.Corrections;
using Alpha.Modules.Reporting.Features.Import;
using Alpha.Domain.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Reporting.Features.Transmissions;

public static class ReportTransmissionEndpoints
{
    public static IEndpointRouteBuilder MapReportTransmissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports").RequireAuthorization().WithTags("Report transmission");
        group.MapGet("/{reportId:guid}/transmissions", GetHistoryAsync);
        group.MapGet("/{reportId:guid}/transmission-routing", GetRoutingAsync);
        group.MapGet("/{reportId:guid}/transmission-routing/validate", ValidateRoutingAsync);
        group.MapPost("/{reportId:guid}/transmissions/resume-hybrid", ResumeHybridAsync);
        group.MapPost("/{reportId:guid}/transmissions", SendAsync);
        group.MapPost("/{reportId:guid}/simulation/payload", BuildSimulationPayloadAsync);
        group.MapPost("/{reportId:guid}/simulation/transmissions", SendSimulationAsync);
        return endpoints;
    }

    private static async Task<IResult> ResumeHybridAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        AlphaDbContext db, IOrganizationAuthorization access,
        IEntitlementService entitlements, ITransmissionBillingGate billingGate,
        IEnumerable<IReportTransmissionProvider> providers,
        EmployerInterface006ExportService exporter,
        EmployerInterfaceFileSequenceService fileSequences,
        IDataProtectionService protector, IConfiguration configuration,
        CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Reporting:ManufacturerRouting:EnableHybridDispatch"))
            return Results.Conflict(new { error = "manufacturer_route_split_disabled" });
        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct))
            return Results.Forbid();
        if (!await db.EmployerDocuments.AsNoTracking().AnyAsync(
            d => d.OrganizationId == organizationId && d.EmployerId == employerId
                && d.DocumentTypeCode == 7 && d.EmploymentId == null, ct))
            return Results.Conflict(new { error = "employer_legal_agreement_required" });
        var entitlement = await entitlements.CanTransmitReport(organizationId, ct);
        if (!entitlement.Allowed)
            return Results.Conflict(new { error = entitlement.Error, feature = entitlement.Feature });
        var billing = await billingGate.CanTransmitAsync(employerId, ct);
        if (!billing.Allowed)
            return Results.Conflict(new { error = billing.Error });
        return await HybridReportTransmissionDispatcher.ResumeAsync(
            organizationId, employerId, reportId, db, providers.ToArray(),
            exporter, fileSequences, protector, ct);
    }

    private static async Task<IResult> ValidateRoutingAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        AlphaDbContext db, IOrganizationAuthorization access,
        IEnumerable<IReportTransmissionProvider> providers,
        EmployerInterface006ExportService exporter,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct))
            return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == reportId && item.OrganizationId == organizationId
                && item.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var available = providers.ToArray();
        var defaultProvider = available.FirstOrDefault(provider => provider.IsConfigured)?.Name
            ?? available.FirstOrDefault()?.Name ?? string.Empty;
        var products = await (
            from product in db.ManualReportProducts.AsNoTracking()
            join employee in db.ManualReportEmployees.AsNoTracking()
                on product.ReportEmployeeId equals employee.Id
            where employee.ReportId == reportId
            select new { product.Id, product.FundCode, product.FundCompanyName, product.SourceReportProductId }).ToListAsync(ct);
        IReadOnlyList<ManufacturerHistoricalRoutingResolver.Assignment> assignments;
        try
        {
            assignments = await ManufacturerHistoricalRoutingResolver.ResolveAsync(
                report, products.Select(product =>
                    new ManufacturerHistoricalRoutingResolver.Product(product.Id, product.FundCode,
                        product.FundCompanyName, product.SourceReportProductId)).ToArray(),
                db, defaultProvider, configuration, ct);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        var groups = assignments.GroupBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var packages = new List<object>(groups.Length);
        var allPackagesValid = true;
        var sequence = 1;
        foreach (var group in groups)
        {
            var provider = available.FirstOrDefault(item =>
                string.Equals(item.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            // Export in-memory and validate with canonical workbook + official XSD.
            // No file number is reserved, no record is persisted, no send occurs.
            var generated = await exporter.ExportAsync(report, ct,
                fileSequence: sequence++, includedProductIds: group.Select(item => item.ProductId).ToArray());
            allPackagesValid &= generated.Validation.IsValid;
            packages.Add(new
            {
                provider = group.Key,
                providerConfigured = provider?.IsConfigured == true,
                productCount = group.Count(),
                valid = generated.Validation.IsValid,
                errors = generated.Validation.Issues
            });
        }
        var hasEmployerAgreement = await db.EmployerDocuments.AsNoTracking().AnyAsync(
            d => d.OrganizationId == organizationId && d.EmployerId == employerId
                && d.DocumentTypeCode == 7 && d.EmploymentId == null, ct);
        return Results.Ok(new
        {
            reportId,
            employerAgreementRequired = !hasEmployerAgreement,
            canTransmit = hasEmployerAgreement && allPackagesValid && groups.Length > 0
                && (groups.Length == 1
                    || configuration.GetValue<bool>("Reporting:ManufacturerRouting:EnableHybridDispatch"))
                && groups.All(group => available.Any(provider => provider.IsConfigured
                    && string.Equals(provider.Name, group.Key, StringComparison.OrdinalIgnoreCase))),
            requiresSplit = groups.Length > 1,
            packages
        });
    }

    private static async Task<IResult> GetRoutingAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        AlphaDbContext db, IOrganizationAuthorization access,
        IEnumerable<IReportTransmissionProvider> providers,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct))
            return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == reportId && item.OrganizationId == organizationId
                && item.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var available = providers.ToArray();
        var defaultProvider = available.FirstOrDefault(provider => provider.IsConfigured)?.Name
            ?? available.FirstOrDefault()?.Name ?? string.Empty;
        var products = await (
            from product in db.ManualReportProducts.AsNoTracking()
            join employee in db.ManualReportEmployees.AsNoTracking()
                on product.ReportEmployeeId equals employee.Id
            where employee.ReportId == reportId
            select new { product.Id, product.FundCode, product.FundCompanyName, product.SourceReportProductId }
        ).ToArrayAsync(ct);
        try
        {
            var assignments = await ManufacturerHistoricalRoutingResolver.ResolveAsync(
                report, products.Select(item => new ManufacturerHistoricalRoutingResolver.Product(
                    item.Id, item.FundCode, item.FundCompanyName, item.SourceReportProductId)).ToArray(),
                db, defaultProvider, configuration, ct);
            var providersById = assignments.ToDictionary(item => item.ProductId,
                item => item.Provider);
            var destinations = products.Select(product =>
                new { fundCode = product.FundCode, provider = providersById[product.Id] })
                .Distinct().ToArray();
            var distinctProviders = assignments.Select(item => item.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return Results.Ok(new
            {
                provider = distinctProviders.Length == 1 ? distinctProviders[0] : null,
                configured = distinctProviders.Length > 0 && distinctProviders.All(name =>
                    available.Any(provider => provider.IsConfigured
                        && string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase))),
                requiresSplit = distinctProviders.Length > 1,
                hybridEnabled = configuration.GetValue<bool>("Reporting:ManufacturerRouting:EnableHybridDispatch"),
                destinations
            });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetHistoryAsync(Guid organizationId, Guid employerId, Guid reportId, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct)) return Results.NotFound();
        return Results.Ok(await db.ReportTransmissions.AsNoTracking().Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId).OrderByDescending(x => x.AttemptNumber).Select(x => new { x.Id, x.Provider, x.RoutingKey, x.RoutedProductIdsJson, x.AttemptNumber, x.Status, x.ExternalId, x.PayloadHash, x.PayloadFileName, payloadSizeBytes = x.Payload.Length, x.AttachmentManifestJson, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt }).ToListAsync(ct));
    }

    private static async Task<IResult> SendAsync(Guid organizationId, Guid employerId, Guid reportId, SendReportRequest? request,
        AlphaDbContext db, IOrganizationAuthorization access, IEntitlementService entitlements,
        ReportPaymentAccountService paymentAccounts, ITransmissionBillingGate billingGate,
        IEnumerable<IReportTransmissionProvider> providers, EmployerInterface006ExportService exporter,
        EmployerInterfaceFileSequenceService fileSequences, IDataProtectionService protector,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.EmployerDocuments.AsNoTracking().AnyAsync(
            d => d.OrganizationId == organizationId && d.EmployerId == employerId
                && d.DocumentTypeCode == 7 && d.EmploymentId == null, ct))
            return Results.Conflict(new { error = "employer_legal_agreement_required" });
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
        var manufacturerProducts = await (
            from product in db.ManualReportProducts.AsNoTracking()
            join employee in db.ManualReportEmployees.AsNoTracking()
                on product.ReportEmployeeId equals employee.Id
            where employee.ReportId == reportId
            select new { product.Id, product.FundCode, product.FundCompanyName, product.SourceReportProductId }
        ).ToArrayAsync(ct);
        IReadOnlyList<ManufacturerHistoricalRoutingResolver.Assignment> assignments;
        try
        {
            assignments = await ManufacturerHistoricalRoutingResolver.ResolveAsync(
                report, manufacturerProducts.Select(item =>
                    new ManufacturerHistoricalRoutingResolver.Product(
                        item.Id, item.FundCode, item.FundCompanyName, item.SourceReportProductId)).ToArray(),
                db, providerName, configuration, ct);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        var recipientGroups = assignments
            .GroupBy(item => item.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HybridReportTransmissionDispatcher.Recipient(
                group.Key, group.Select(item => item.ProductId).ToArray()))
            .OrderBy(route => route.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (recipientGroups.Length == 0)
            return Results.Conflict(new { error = "manufacturer_route_no_products" });

        if (recipientGroups.Length > 1)
        {
            if (!configuration.GetValue<bool>("Reporting:ManufacturerRouting:EnableHybridDispatch"))
                return Results.Conflict(new { error = "manufacturer_route_split_disabled",
                    requiresSplit = true });
            if (!string.IsNullOrWhiteSpace(request?.Provider))
                return Results.Conflict(new { error = "manufacturer_route_client_override_not_allowed" });
            return await HybridReportTransmissionDispatcher.SendAsync(
                organizationId, employerId, reportId, db, recipientGroups,
                availableProviders, exporter, fileSequences, protector, ct);
        }

        var routedProvider = recipientGroups[0].Provider;
        // The server owns routing policy: an explicit client provider may not
        // override a configured fund destination. Otherwise select that destination.
        if (!string.IsNullOrWhiteSpace(request?.Provider)
            && !string.Equals(routedProvider, providerName, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new
            {
                error = "manufacturer_route_override_conflict",
                requiredProvider = routedProvider
            });
        providerName = routedProvider;

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


    // Dedicated TEST-only transmission. Never route through normal send logic, and never
    // allow caller-selected providers. Authorization and report ownership are unchanged.
    private static async Task<IResult> SendSimulationAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        AlphaDbContext db, IOrganizationAuthorization access,
        EmployerInterface006ExportService exporter,
        EmployerInterfaceFileSequenceService fileSequences,
        IDataProtectionService protector,
        IEnumerable<IReportTransmissionProvider> providers,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("EmployerInterface006:SimulationTestMode")
            || configuration.GetValue<int>("EmployerInterface006:EnvironmentCode") != 1
            || !string.Equals(configuration["EmployerInterface006:ClearinghouseProvider"],
                "Simulated", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct))
            return Results.Forbid();

        var provider = providers.SingleOrDefault(x => x.Name == "SimulatedVault" && x.IsConfigured);
        if (provider is null)
            return Results.Conflict(new { error = "simulation_provider_unavailable" });

        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Draft || report.IsCorrectionWorkspace)
            return Results.Conflict(new { error = "simulation_requires_unsubmitted_draft" });
        if (report.ReportKind == ManualReportKind.Differences)
            return Results.Conflict(new { error = "simulation_requires_transmittable_report_kind" });

        // Never dispatch a multi-manufacturer file into one provider vault.
        // Simulation is currently supported for reports whose products all use the default vault.
        var products = await (
            from p in db.ManualReportProducts.AsNoTracking()
            join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
            where e.ReportId == reportId
            select new ManufacturerHistoricalRoutingResolver.Product(
                p.Id, p.FundCode, p.FundCompanyName, p.SourceReportProductId))
            .ToArrayAsync(ct);
        if (products.Length == 0)
            return Results.Conflict(new { error = "simulation_requires_products" });
        IReadOnlyList<ManufacturerHistoricalRoutingResolver.Assignment> routes;
        try
        {
            routes = await ManufacturerHistoricalRoutingResolver.ResolveAsync(
                report, products, db, provider.Name, configuration, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        if (routes.Any(x => !string.Equals(x.Provider, provider.Name, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { error = "simulation_manufacturer_routing_unsupported" });

        EmployerInterfaceFileSequenceService.Reservation reservation;
        try { reservation = await fileSequences.ReserveAsync(employerId, ct); }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "simulation_file_sequence_unavailable" });
        }

        var generated = await exporter.ExportAsync(report, ct, reservation.Sequence,
            reservation.PreparedAt, skipSimulationValidation: true);
        if (generated.Bytes.Length == 0 || string.IsNullOrWhiteSpace(generated.PayloadFileName))
            return Results.Conflict(new { error = "simulation_xml_generation_failed",
                generated.Validation });

        // Atomic claim: a draft can be dispatched exactly once. There is no retry after
        // an ambiguous provider outcome. Subsequent error scenarios use new drafts.
        var claimed = await db.ManualReports.Where(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && x.Status == ManualReportStatus.Draft)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, ManualReportStatus.Processing)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        if (claimed != 1)
            return Results.Conflict(new { error = "simulation_report_already_claimed" });

        var hash = EmployerInterfaceService.Hash(generated.Bytes);
        var files = (generated.AttachmentFiles ?? [])
            .Select(x => new ReportTransmissionAttachment(x.FileName, x.ContentType, x.Content, x.Sha256))
            .ToArray();
        var attempt = (await db.ReportTransmissions
            .Where(x => x.ReportId == reportId)
            .MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
        var transmission = new ReportTransmission(reportId, organizationId, employerId, provider.Name, attempt);
        var manifest = JsonSerializer.Serialize(files.Select(x => new {
            x.FileName, x.ContentType, sizeBytes = x.Content.LongLength, x.Sha256
        }));
        transmission.Start(hash, generated.PayloadFileName,
            protector.ProtectBytes(generated.Bytes, $"report-transmission:{transmission.Id}"), manifest);
        db.ReportTransmissions.Add(transmission);
        await db.SaveChangesAsync(ct);

        try
        {
            // Once persisted, dispatch is irreversible. Do not use the request-abort token.
            var result = await provider.SendAsync(new ReportTransmissionEnvelope(
                reportId, organizationId, employerId, generated.Bytes, hash, files,
                generated.PayloadFileName), CancellationToken.None);
            transmission.Complete(result.Success ? ReportTransmissionStatus.Accepted :
                ReportTransmissionStatus.Rejected, result.ExternalId,
                string.IsNullOrWhiteSpace(result.ResponsePayload) ? string.Empty :
                    protector.Protect(result.ResponsePayload,
                        $"report-transmission-response:{transmission.Id}"), result.ErrorMessage);
            var trackedReport = await db.ManualReports.SingleAsync(x => x.Id == reportId, CancellationToken.None);
            if (result.Success) trackedReport.MarkSent();
            else trackedReport.MarkTransmissionError(result.ErrorMessage);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Ok(new {
                simulationTestMode = true, transmitted = result.Success,
                reportId, transmissionId = transmission.Id,
                transmissionStatus = transmission.Status,
                generated.PayloadFileName,
                validationBypassed = true,
                generated.Validation
            });
        }
        catch (Exception)
        {
            transmission.Complete(ReportTransmissionStatus.Error, null, null,
                "Simulation dispatch outcome uncertain; reconciliation required.");
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(new { error = "simulation_dispatch_reconciliation_required" },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // This endpoint does not transmit or alter persisted reports. It is a restricted first
    // step for inspecting deliberately invalid 006 payloads in an isolated local TEST runtime.
    // The normal transmission route never calls the export validation bypass.
    private static async Task<IResult> BuildSimulationPayloadAsync(
        Guid organizationId, Guid employerId, Guid reportId,
        AlphaDbContext db, IOrganizationAuthorization access,
        EmployerInterface006ExportService exporter, IConfiguration configuration,
        IHostEnvironment environment, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("EmployerInterface006:SimulationTestMode")
            || configuration.GetValue<int>("EmployerInterface006:EnvironmentCode") != 1
            || !string.Equals(configuration["EmployerInterface006:ClearinghouseProvider"],
                "Simulated", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        if (!await access.CanTransmitReportAsync(organizationId, employerId, ct))
            return Results.Forbid();

        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Draft)
            return Results.Conflict(new { error = "simulation_requires_draft" });

        var generated = await exporter.ExportAsync(report, ct, skipSimulationValidation: true);
        if (generated.Bytes.Length == 0)
            return Results.Conflict(new { error = "simulation_xml_generation_failed",
                generated.Validation });

        return Results.Ok(new {
            simulationTestMode = true,
            transmitted = false,
            generated.PayloadFileName,
            xmlBase64 = Convert.ToBase64String(generated.Bytes),
            generated.Validation
        });
    }

    private static object ToResponse(ManualReport report, ReportTransmission transmission, EmployerInterface006FileValidation validation) => new
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
