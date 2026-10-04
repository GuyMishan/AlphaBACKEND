using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Services;

public sealed record SimulatedVaultFeedbackInstruction(
    Guid ReportId,
    Guid TransmissionId,
    Guid OrganizationId,
    Guid EmployerId,
    string Scenario,
    string SourcePayloadFileName,
    DateTimeOffset CreatedAt);

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
                var instruction = new SimulatedVaultFeedbackInstruction(
                    transmission.ReportId,
                    transmission.Id,
                    transmission.OrganizationId,
                    transmission.EmployerId,
                    scenario,
                    fileName,
                    DateTimeOffset.UtcNow);

                var inbox = Path.Combine(root, "inbox", employerId.ToString("N"));
                Directory.CreateDirectory(inbox);
                var instructionPath = Path.Combine(inbox, fileName + ".simulation.json");
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(instruction));
                await WriteAtomicallyAsync(instructionPath, bytes, ct);

                Directory.CreateDirectory(Path.GetDirectoryName(respondedPath)!);
                await File.WriteAllTextAsync(respondedPath, scenario, ct);
                logger.LogInformation(
                    "Simulated clearing-house queued {Scenario} feedback for report {ReportId}.",
                    scenario,
                    transmission.ReportId);
            }
        }
    }

    private static string ResolveScenario(string payloadPath, string defaultScenario)
    {
        var sidecar = payloadPath + ".scenario";
        var raw = File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : defaultScenario?.Trim();
        return NormalizeScenario(raw);
    }

    internal static string NormalizeScenario(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "partial" => "partial",
            "error" => "error",
            "in-transit" => "in-transit",
            _ => "success"
        };

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

        var scenario = SimulatedClearinghouseResponder.NormalizeScenario(instruction.Scenario);
        var rawSimulation = JsonSerializer.Serialize(instruction with { Scenario = scenario });
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
            var (received, allocated, inTransit, status) = scenario switch
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
                $"SIMULATION:{scenario}",
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
            var errorCode = scenario switch
            {
                "error" => 53,
                "partial" when index == 0 => 53,
                _ => 1
            };
            var intakeStatus = errorCode == 1 ? 1 : 2;
            var recordId = string.IsNullOrWhiteSpace(contribution.InterfaceRecordIdentifier)
                ? contribution.Id.ToString("D").ToUpperInvariant()
                : contribution.InterfaceRecordIdentifier;

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
                contribution.Amount,
                sourceFileName,
                DateTimeOffset.UtcNow));
        }

        await db.SaveChangesAsync(ct);
        return feedback.Id;
    }
}
