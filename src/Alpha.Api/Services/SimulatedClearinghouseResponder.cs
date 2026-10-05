using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Services;

public sealed record SimulatedTransferOutcome(
    string TransferIdentifier,
    string Mode,
    int? ErrorCode = null);

public sealed record SimulatedVaultFeedbackInstruction(
    Guid ReportId,
    Guid TransmissionId,
    Guid OrganizationId,
    Guid EmployerId,
    string Scenario,
    string SourcePayloadFileName,
    DateTimeOffset CreatedAt,
    int? ErrorCode = null,
    string FeedbackInterface = "EMPFED",
    string ErrorDetail = "",
    IReadOnlyList<SimulatedTransferOutcome>? TransferOutcomes = null);

public sealed record SimulatedClearinghouseScenario(
    string Mode,
    int? ErrorCode = null,
    string FeedbackInterface = "EMPFED",
    string ErrorDetail = "")
{
    public string CanonicalName =>
        FeedbackInterface switch
        {
            ClearinghouseInitialFeedbackCatalog.TechnicalInterface when Mode == "accepted" => "fedbka:accepted",
            ClearinghouseInitialFeedbackCatalog.TechnicalInterface when Mode == "duplicate" => "fedbka:duplicate",
            ClearinghouseInitialFeedbackCatalog.TechnicalInterface when ErrorCode.HasValue => $"fedbka:{ErrorCode.Value}",
            ClearinghouseInitialFeedbackCatalog.TechnicalInterface when Mode == "all-errors" => "fedbka:all-errors",
            ClearinghouseInitialFeedbackCatalog.ContentInterface when Mode == "accepted" => "fedbkb:accepted",
            _ => ErrorCode.HasValue ? $"{Mode}:{ErrorCode.Value}" : Mode
        };
}

public sealed class SimulatedClearinghouseResponder(
    IServiceScopeFactory scopeFactory,
    IOptions<SimulatedClearinghouseVaultOptions> options,
    IOptions<EmployerInterface006Options> employerInterfaceOptions,
    ILogger<SimulatedClearinghouseResponder> logger) : BackgroundService
{
    private readonly SimulatedClearinghouseVaultOptions _options = options.Value;
    private readonly EmployerInterface006Options _employerInterface = employerInterfaceOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.AutoRespond || _employerInterface.EnvironmentCode != 1)
            return;

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOutboxAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Simulated clearing-house auto responder failed."); }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ScanOutboxAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(_options.RootDirectory.Trim());
        var outboxRoot = Path.Combine(root, "outbox");
        Directory.CreateDirectory(outboxRoot);

        foreach (var employerDirectory in Directory.EnumerateDirectories(outboxRoot))
        {
            if (!Guid.TryParse(Path.GetFileName(employerDirectory), out var employerId))
                continue;

            foreach (var payloadPath in Directory.EnumerateFiles(employerDirectory)
                         .Where(x => x.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                             || x.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                             || x.EndsWith(".tst", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(payloadPath);
                var respondedPath = Path.Combine(root, "responded", employerId.ToString("N"), fileName + ".done");
                if (File.Exists(respondedPath)) continue;

                var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(payloadPath);
                if (age < TimeSpan.FromSeconds(Math.Clamp(_options.ResponseDelaySeconds, 0, 300)))
                    continue;

                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IAlphaDbContext>();
                var transmission = await db.ReportTransmissions.AsNoTracking()
                    .Where(x => x.EmployerId == employerId && x.PayloadFileName == fileName
                        && (x.Status == ReportTransmissionStatus.Accepted || x.Status == ReportTransmissionStatus.Sent))
                    .OrderByDescending(x => x.AttemptNumber)
                    .Select(x => new { x.Id, x.ReportId, x.OrganizationId, x.EmployerId })
                    .FirstOrDefaultAsync(ct);
                if (transmission is null) continue;

                var scenario = ResolveScenario(payloadPath, _options.DefaultScenario);
                var inbox = Path.Combine(root, "inbox", employerId.ToString("N"));
                Directory.CreateDirectory(inbox);

                var expanded = ExpandScenario(scenario);
                var mixedOutcomes = scenario.Mode == "mixed"
                    ? await BuildMixedOutcomesAsync(db, transmission.ReportId, ct)
                    : null;

                foreach (var item in expanded)
                {
                    if (item.Mode == "unsupported")
                    {
                        logger.LogWarning(
                            "Simulated clearing-house scenario {Scenario} was rejected because the authoritative code catalog is not available.",
                            scenario.CanonicalName);
                        continue;
                    }

                    var instruction = new SimulatedVaultFeedbackInstruction(
                        transmission.ReportId,
                        transmission.Id,
                        transmission.OrganizationId,
                        transmission.EmployerId,
                        item.Mode,
                        fileName,
                        DateTimeOffset.UtcNow,
                        item.ErrorCode,
                        item.FeedbackInterface,
                        item.Mode == "duplicate"
                            ? ClearinghouseInitialFeedbackCatalog.DuplicateFileDetail(fileName)
                            : item.ErrorDetail,
                        item.Mode == "mixed" ? mixedOutcomes : null);

                    var stage = item.FeedbackInterface.ToLowerInvariant();
                    var suffix = item.ErrorCode.HasValue
                        ? $".{stage}-{item.ErrorCode.Value:000}"
                        : $".{stage}-{item.Mode}";
                    var instructionPath = Path.Combine(inbox, fileName + suffix + ".simulation.json");
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(instruction));
                    await WriteAtomicallyAsync(instructionPath, bytes, ct);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(respondedPath)!);
                await File.WriteAllTextAsync(respondedPath, scenario.CanonicalName, ct);
                logger.LogInformation(
                    "Simulated clearing-house queued {Scenario} feedback for report {ReportId}.",
                    scenario.CanonicalName,
                    transmission.ReportId);
            }
        }
    }

    private static SimulatedClearinghouseScenario ResolveScenario(string payloadPath, string defaultScenario)
    {
        var sidecar = payloadPath + ".scenario";
        var raw = File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : defaultScenario?.Trim();
        return ParseScenario(raw);
    }

    public static SimulatedClearinghouseScenario ParseScenario(string? value)
    {
        var raw = value?.Trim().ToLowerInvariant() ?? string.Empty;

        if (raw == "all-errors") return new("all-errors");
        if (raw is "success" or "error" or "partial" or "in-transit" or "mixed") return new(raw);

        if (raw == "fedbka:accepted")
            return new("accepted", null, ClearinghouseInitialFeedbackCatalog.TechnicalInterface);
        if (raw == "fedbka:duplicate")
            return new("duplicate", 1, ClearinghouseInitialFeedbackCatalog.TechnicalInterface);
        if (raw == "fedbka:all-errors")
            return new("all-errors", null, ClearinghouseInitialFeedbackCatalog.TechnicalInterface);
        if (raw == "fedbkb:accepted")
            return new("accepted", null, ClearinghouseInitialFeedbackCatalog.ContentInterface);

        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && parts[0] is "error" or "partial"
            && int.TryParse(parts[1], out var summaryCode)
            && EmployerInterfaceLineFeedbackParser.IsOfficialErrorCode(summaryCode)
            && summaryCode != 1)
            return new(parts[0], summaryCode);

        if (parts.Length == 2
            && parts[0] == "fedbka"
            && int.TryParse(parts[1], out var technicalCode)
            && ClearinghouseInitialFeedbackCatalog.IsStageAFileError(technicalCode))
            return new(
                "error",
                technicalCode,
                ClearinghouseInitialFeedbackCatalog.TechnicalInterface,
                ClearinghouseInitialFeedbackCatalog.StageADescription(technicalCode));

        // FEDBKB error codes are intentionally fail-closed until its official, request-specific
        // Events Interface schema/codebook is committed to this repository.
        if (raw.StartsWith("fedbkb:", StringComparison.Ordinal))
            return new("unsupported", null, ClearinghouseInitialFeedbackCatalog.ContentInterface,
                "FEDBKB error simulation requires the authoritative request-specific Events Interface specification.");

        return new("success");
    }

    public static string NormalizeScenario(string? value) => ParseScenario(value).CanonicalName;

    public static IReadOnlyList<SimulatedClearinghouseScenario> ExpandScenario(SimulatedClearinghouseScenario scenario)
    {
        if (scenario.Mode != "all-errors") return [scenario];

        if (scenario.FeedbackInterface == ClearinghouseInitialFeedbackCatalog.TechnicalInterface)
        {
            return ClearinghouseInitialFeedbackCatalog.StageAFileErrors
                .Select(item => new SimulatedClearinghouseScenario(
                    "error",
                    item.Code,
                    ClearinghouseInitialFeedbackCatalog.TechnicalInterface,
                    item.Description))
                .ToArray();
        }

        return EmployerInterfaceLineFeedbackParser.OfficialFailureCodes
            .Select(code => new SimulatedClearinghouseScenario("error", code))
            .ToArray();
    }


    private static async Task<IReadOnlyList<SimulatedTransferOutcome>> BuildMixedOutcomesAsync(
        IAlphaDbContext db,
        Guid reportId,
        CancellationToken ct)
    {
        var employeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .Select(x => x.Id)
            .ToArrayAsync(ct);
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .OrderBy(x => x.AllocationOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        var transferIds = products
            .Select(product =>
                metadata.TryGetValue(product.Id, out var item)
                && !string.IsNullOrWhiteSpace(item.InterfaceTransferIdentifier)
                    ? item.InterfaceTransferIdentifier.Trim().ToUpperInvariant()
                    : product.Id.ToString("D").ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (transferIds.Length == 0)
            return [];

        var pattern = new (string Mode, int? ErrorCode)[]
        {
            ("success", null),
            ("error", 53),
            ("partial", 116),
            ("in-transit", null)
        };

        return transferIds
            .Select((transferId, index) =>
            {
                var outcome = pattern[index % pattern.Length];
                return new SimulatedTransferOutcome(transferId, outcome.Mode, outcome.ErrorCode);
            })
            .ToArray();
    }

    private static async Task WriteAtomicallyAsync(string targetPath, byte[] content, CancellationToken ct)
    {
        if (File.Exists(targetPath)) return;
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, ct);
            File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

public sealed class SimulatedClearinghouseTechnicalFeedbackHandler(IAlphaDbContext db)
{
    public async Task<bool> HandleAsync(
        Guid expectedEmployerId,
        SimulatedVaultFeedbackInstruction instruction,
        CancellationToken ct)
    {
        if (instruction.EmployerId != expectedEmployerId)
            return false;

        if (instruction.FeedbackInterface is not (
            ClearinghouseInitialFeedbackCatalog.TechnicalInterface
            or ClearinghouseInitialFeedbackCatalog.ContentInterface))
            return false;

        if (instruction.FeedbackInterface == ClearinghouseInitialFeedbackCatalog.ContentInterface
            && !string.Equals(instruction.Scenario, "accepted", StringComparison.OrdinalIgnoreCase))
            return false;

        if (instruction.FeedbackInterface == ClearinghouseInitialFeedbackCatalog.TechnicalInterface
            && instruction.ErrorCode.HasValue
            && !ClearinghouseInitialFeedbackCatalog.IsStageAFileError(instruction.ErrorCode.Value))
            return false;

        var transmission = await db.ReportTransmissions
            .SingleOrDefaultAsync(x => x.Id == instruction.TransmissionId
                && x.ReportId == instruction.ReportId
                && x.EmployerId == instruction.EmployerId
                && x.OrganizationId == instruction.OrganizationId, ct);
        if (transmission is null)
            return false;

        var report = await db.ManualReports
            .SingleOrDefaultAsync(x => x.Id == instruction.ReportId
                && x.EmployerId == instruction.EmployerId
                && x.OrganizationId == instruction.OrganizationId, ct);
        if (report is null)
            return false;

        var response = JsonSerializer.Serialize(instruction);
        var rejected = instruction.FeedbackInterface == ClearinghouseInitialFeedbackCatalog.TechnicalInterface
            && (instruction.ErrorCode.HasValue
                || string.Equals(instruction.Scenario, "duplicate", StringComparison.OrdinalIgnoreCase));

        if (rejected)
        {
            var code = instruction.ErrorCode ?? 1;
            var detail = string.IsNullOrWhiteSpace(instruction.ErrorDetail)
                ? ClearinghouseInitialFeedbackCatalog.StageADescription(code)
                : instruction.ErrorDetail;
            transmission.Complete(ReportTransmissionStatus.Rejected, transmission.ExternalId, response,
                $"FEDBKA {code}: {detail}");
            report.MarkTransmissionError($"המסלקה דחתה את הקובץ טכנית (FEDBKA {code}): {detail}");
        }
        else
        {
            transmission.Complete(ReportTransmissionStatus.Accepted, transmission.ExternalId, response, null);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class SimulatedClearinghouseFeedbackIngestor(
    IAlphaDbContext db,
    IDataProtectionService protector)
{
    public async Task<Guid?> IngestAsync(
        Guid expectedEmployerId,
        SimulatedVaultFeedbackInstruction instruction,
        string sourceFileName,
        CancellationToken ct)
    {
        if (instruction.EmployerId != expectedEmployerId)
            return null;

        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == instruction.ReportId
                && x.OrganizationId == instruction.OrganizationId
                && x.EmployerId == instruction.EmployerId, ct);
        if (report is null) return null;

        var transmissionExists = await db.ReportTransmissions.AsNoTracking()
            .AnyAsync(x => x.Id == instruction.TransmissionId
                && x.ReportId == instruction.ReportId
                && x.EmployerId == instruction.EmployerId, ct);
        if (!transmissionExists) return null;

        var parsedScenario = SimulatedClearinghouseResponder.ParseScenario(
            instruction.ErrorCode.HasValue ? $"{instruction.Scenario}:{instruction.ErrorCode.Value}" : instruction.Scenario);
        var scenario = parsedScenario.Mode;
        var selectedErrorCode = parsedScenario.ErrorCode
            ?? (instruction.ErrorCode.HasValue && EmployerInterfaceLineFeedbackParser.IsOfficialErrorCode(instruction.ErrorCode.Value)
                ? instruction.ErrorCode.Value
                : null);
        var rawSimulation = JsonSerializer.Serialize(instruction with
        {
            Scenario = scenario,
            ErrorCode = selectedErrorCode
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawSimulation))).ToLowerInvariant();

        var existing = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.EmployerId == instruction.EmployerId && x.PayloadHash == hash)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        if (existing.HasValue) return existing;

        var feedback = new EmployerInterfaceFeedback(
            instruction.OrganizationId,
            instruction.EmployerId,
            EmployerInterfaceDocumentType.SummaryFeedback,
            EmployerInterfaceService.CurrentVersion,
            sourceFileName,
            hash,
            protector.Protect(rawSimulation, $"employer-interface-feedback:{hash}"),
            $"SIM-{instruction.TransmissionId:N}"[..Math.Min(34, $"SIM-{instruction.TransmissionId:N}".Length)]);
        feedback.Correlate(instruction.ReportId, instruction.TransmissionId);
        db.EmployerInterfaceFeedback.Add(feedback);

        var employeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == instruction.ReportId)
            .Select(x => x.Id)
            .ToArrayAsync(ct);
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToListAsync(ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        var outcomeByTransfer = (instruction.TransferOutcomes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.TransferIdentifier))
            .ToDictionary(
                x => x.TransferIdentifier.Trim().ToUpperInvariant(),
                x => x,
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in products.GroupBy(product =>
        {
            if (metadata.TryGetValue(product.Id, out var item)
                && !string.IsNullOrWhiteSpace(item.InterfaceTransferIdentifier))
                return item.InterfaceTransferIdentifier.Trim().ToUpperInvariant();
            return product.Id.ToString("D").ToUpperInvariant();
        }, StringComparer.OrdinalIgnoreCase))
        {
            var groupProductIds = group.Select(x => x.Id).ToHashSet();
            var reported = contributions.Where(x => groupProductIds.Contains(x.ReportProductId)).Sum(x => x.Amount);
            var transferScenario = outcomeByTransfer.TryGetValue(group.Key, out var transferOutcome)
                ? SimulatedClearinghouseResponder.ParseScenario(
                    transferOutcome.ErrorCode.HasValue ? $"{transferOutcome.Mode}:{transferOutcome.ErrorCode.Value}" : transferOutcome.Mode)
                : parsedScenario;
            var transferMode = transferScenario.Mode;
            var (received, allocated, inTransit, status) = transferMode switch
            {
                "in-transit" => (reported, 0m, reported, 3),
                "partial" => (reported, Math.Round(reported / 2m, 2), reported - Math.Round(reported / 2m, 2), 3),
                "error" => (0m, 0m, 0m, 4),
                _ => (reported, reported, 0m, 1)
            };

            var firstProduct = group.First();
            metadata.TryGetValue(firstProduct.Id, out var firstMetadata);
            db.EmployerInterfaceTransferFeedback.Add(new EmployerInterfaceTransferFeedback(
                feedback.Id,
                instruction.ReportId,
                group.Key,
                $"SIM-{instruction.TransmissionId:N}",
                reported,
                received,
                allocated,
                inTransit,
                0m,
                0m,
                status,
                $"SIMULATION:{transferMode}",
                instruction.TransmissionId.ToString("N"),
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"),
                DateTimeOffset.UtcNow));
        }

        var effective = contributions
            .Where(x => ReportFeedbackStatusResolver.IsEffectiveContribution(x))
            .OrderBy(x => x.ReportProductId)
            .ThenBy(x => x.Id)
            .ToArray();

        for (var index = 0; index < effective.Length; index++)
        {
            var contribution = effective[index];
            var product = products.Single(x => x.Id == contribution.ReportProductId);
            metadata.TryGetValue(contribution.ReportProductId, out var contributionMetadata);
            var contributionTransferId = contributionMetadata is not null
                && !string.IsNullOrWhiteSpace(contributionMetadata.InterfaceTransferIdentifier)
                ? contributionMetadata.InterfaceTransferIdentifier.Trim().ToUpperInvariant()
                : contribution.ReportProductId.ToString("D").ToUpperInvariant();
            var contributionScenario = outcomeByTransfer.TryGetValue(contributionTransferId, out var contributionOutcome)
                ? SimulatedClearinghouseResponder.ParseScenario(
                    contributionOutcome.ErrorCode.HasValue ? $"{contributionOutcome.Mode}:{contributionOutcome.ErrorCode.Value}" : contributionOutcome.Mode)
                : parsedScenario;
            var errorCode = contributionScenario.Mode switch
            {
                "error" => contributionScenario.ErrorCode ?? selectedErrorCode ?? 53,
                "partial" => contributionScenario.ErrorCode ?? selectedErrorCode ?? 53,
                _ => 1
            };
            var intakeStatus = errorCode == 1 ? 1 : 2;
            var recordId = string.IsNullOrWhiteSpace(contribution.InterfaceRecordIdentifier)
                ? contribution.Id.ToString("D").ToUpperInvariant()
                : contribution.InterfaceRecordIdentifier;

            // Keep simulated feedback internally consistent with the selected official
            // error. Code 53 specifically means the returned salary/rate/amount relationship
            // does not reconcile, so returning the exact original values would make the UI
            // show an error while every compared number still looks identical.
            var simulatedContributionAmount = errorCode == 53
                ? contribution.Amount + 1m
                : contribution.Amount;

            db.EmployerInterfaceContributionFeedback.Add(new EmployerInterfaceContributionFeedback(
                feedback.Id,
                instruction.ReportId,
                contribution.ReportProductId,
                contribution.Id,
                recordId,
                0,
                intakeStatus,
                errorCode,
                EmployerInterfaceLineFeedbackParser.Description(errorCode),
                errorCode == 1 ? null : contribution.Amount,
                errorCode == 1 ? null : DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                product.Salary,
                product.SalaryMonth,
                product.PolicyNumber,
                contribution.Percentage,
                simulatedContributionAmount,
                sourceFileName,
                DateTimeOffset.UtcNow));
        }

        await db.SaveChangesAsync(ct);
        return feedback.Id;
    }
}
