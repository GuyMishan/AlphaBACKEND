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

public sealed record SimulatedContributionOutcome(
    Guid ContributionId,
    int ErrorCode,
    int Sequence = 0);

public sealed record SimulatedStressContribution(
    Guid ContributionId,
    Guid ProductId,
    Guid EmployeeId);

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
    IReadOnlyList<SimulatedTransferOutcome>? TransferOutcomes = null,
    IReadOnlyList<SimulatedContributionOutcome>? ContributionOutcomes = null);

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
                        && x.Provider == _options.ProviderName
                        && (x.Status == ReportTransmissionStatus.Accepted || x.Status == ReportTransmissionStatus.Sent))
                    .OrderByDescending(x => x.AttemptNumber)
                    .Select(x => new { x.Id, x.ReportId, x.OrganizationId, x.EmployerId })
                    .FirstOrDefaultAsync(ct);
                if (transmission is null) continue;

                var scenario = ResolveScenario(payloadPath, _options.DefaultScenario);
                var inbox = Path.Combine(root, "inbox", employerId.ToString("N"));
                Directory.CreateDirectory(inbox);

                var expanded = ExpandScenario(scenario);
                var contributionOutcomes = scenario.Mode == "stress"
                    ? await BuildStressContributionOutcomesAsync(db, transmission.ReportId, ct)
                    : null;
                var transferOutcomes = scenario.Mode switch
                {
                    "mixed" => await BuildMixedOutcomesAsync(db, transmission.ReportId, ct),
                    "stress" => await BuildStressTransferOutcomesAsync(
                        db, transmission.ReportId, contributionOutcomes ?? [], ct),
                    _ => null
                };

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
                        item.Mode is "mixed" or "stress" ? transferOutcomes : null,
                        item.Mode == "stress" ? contributionOutcomes : null);

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
        if (raw is "success" or "error" or "partial" or "in-transit" or "mixed" or "stress") return new(raw);

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

    public static string ResolveStressTransferMode(IEnumerable<int> errorCodes)
    {
        var actionable = errorCodes
            .Where(code => EmployerInterfaceLineFeedbackParser.IsOfficialErrorCode(code))
            .Where(code => ReportFeedbackStatusResolver.IsActionableFeedbackError(code))
            .Distinct()
            .ToArray();

        if (actionable.Length == 0) return "success";
        if (actionable.Any(code =>
                EmployerInterfaceLineFeedbackParser.ErrorScope(code)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money))
            return "error";
        return "partial";
    }

    private static async Task<IReadOnlyList<SimulatedTransferOutcome>> BuildStressTransferOutcomesAsync(
        IAlphaDbContext db,
        Guid reportId,
        IReadOnlyList<SimulatedContributionOutcome> contributionOutcomes,
        CancellationToken ct)
    {
        var employeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .Select(x => x.Id)
            .ToArrayAsync(ct);
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .Select(x => new { x.Id, x.ReportProductId })
            .ToListAsync(ct);
        var productByContribution = contributions.ToDictionary(x => x.Id, x => x.ReportProductId);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        string TransferId(Guid productId)
        {
            if (metadata.TryGetValue(productId, out var item)
                && !string.IsNullOrWhiteSpace(item.InterfaceTransferIdentifier))
                return item.InterfaceTransferIdentifier.Trim().ToUpperInvariant();
            return productId.ToString("D").ToUpperInvariant();
        }

        var codesByTransfer = contributionOutcomes
            .Where(x => productByContribution.ContainsKey(x.ContributionId))
            .GroupBy(x => TransferId(productByContribution[x.ContributionId]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.ErrorCode).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return products
            .Select(product => TransferId(product.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(transferId =>
            {
                var mode = codesByTransfer.TryGetValue(transferId, out var codes)
                    ? ResolveStressTransferMode(codes)
                    : "success";
                return new SimulatedTransferOutcome(transferId, mode);
            })
            .ToArray();
    }

    private static readonly IReadOnlyDictionary<int, string> StressExclusiveGroupByCode =
        new Dictionary<int, string>
        {
            [5] = "policy-inactive-renewability", [77] = "policy-inactive-renewability",
            [6] = "employer-insurance-balance", [7] = "employer-insurance-balance",
            [69] = "refund-affidavit", [70] = "refund-affidavit",
            [81] = "employee-insurance-balance", [82] = "employee-insurance-balance",
            [83] = "proactive-refund-stage", [84] = "proactive-refund-stage", [85] = "proactive-refund-stage",
            [94] = "cancel-blocker", [95] = "cancel-blocker", [96] = "cancel-blocker",
            [100] = "correction-pairing", [101] = "correction-pairing",
            [102] = "affidavit-state", [103] = "affidavit-state",
            [105] = "employer-benefits-balance", [106] = "employer-benefits-balance",
            [107] = "employee-benefits-balance", [108] = "employee-benefits-balance",
            [109] = "affidavit-state", [110] = "affidavit-state",
            [111] = "affidavit-state", [112] = "affidavit-state",
            [113] = "employer-severance-balance", [114] = "employer-severance-balance"
        };

    public static string? StressExclusiveGroup(int errorCode) =>
        StressExclusiveGroupByCode.GetValueOrDefault(errorCode);

    public static IReadOnlyList<SimulatedContributionOutcome> BuildStressContributionOutcomes(
        IReadOnlyList<SimulatedStressContribution> rows)
    {
        if (rows.Count == 0) return [];

        var orderedRows = rows
            .OrderBy(x => x.EmployeeId)
            .ThenBy(x => x.ProductId)
            .ThenBy(x => x.ContributionId)
            .ToArray();
        var products = orderedRows.GroupBy(x => x.ProductId).Select(x => x.ToArray()).ToArray();
        var employees = orderedRows.GroupBy(x => x.EmployeeId).Select(x => x.ToArray()).ToArray();
        var outcomes = new List<SimulatedContributionOutcome>();
        var contributionIndex = 0;
        var productIndex = 0;
        var employeeIndex = 0;
        var sequenceByContribution = new Dictionary<Guid, int>();
        var exclusiveAssignments = new Dictionary<(string Group, string Target), int>();

        void AddOutcome(Guid contributionId, int code)
        {
            sequenceByContribution.TryGetValue(contributionId, out var sequence);
            outcomes.Add(new SimulatedContributionOutcome(contributionId, code, sequence));
            sequenceByContribution[contributionId] = sequence + 1;
        }

        bool CanAssign(int code, string target)
        {
            var group = StressExclusiveGroup(code);
            if (group is null) return true;
            var effectiveTarget = group == "affidavit-state" ? "report" : target;
            var key = (group, effectiveTarget);
            if (!exclusiveAssignments.TryGetValue(key, out var existing))
            {
                exclusiveAssignments[key] = code;
                return true;
            }
            return existing == code;
        }

        foreach (var code in EmployerInterfaceLineFeedbackParser.OfficialFailureCodes)
        {
            var scope = EmployerInterfaceLineFeedbackParser.ErrorScope(code);
            switch (scope)
            {
                case EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Deposit:
                case EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money:
                {
                    for (var attempt = 0; attempt < products.Length; attempt++)
                    {
                        var target = products[productIndex % products.Length];
                        productIndex++;
                        var targetKey = $"product:{target[0].ProductId:D}";
                        if (!CanAssign(code, targetKey)) continue;
                        foreach (var row in target) AddOutcome(row.ContributionId, code);
                        break;
                    }
                    break;
                }
                case EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Employee:
                {
                    for (var attempt = 0; attempt < employees.Length; attempt++)
                    {
                        var target = employees[employeeIndex % employees.Length];
                        employeeIndex++;
                        var targetKey = $"employee:{target[0].EmployeeId:D}";
                        if (!CanAssign(code, targetKey)) continue;
                        foreach (var row in target) AddOutcome(row.ContributionId, code);
                        break;
                    }
                    break;
                }
                case EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report:
                {
                    if (!CanAssign(code, "report")) break;
                    foreach (var row in orderedRows) AddOutcome(row.ContributionId, code);
                    break;
                }
                default:
                {
                    for (var attempt = 0; attempt < orderedRows.Length; attempt++)
                    {
                        var target = orderedRows[contributionIndex % orderedRows.Length];
                        contributionIndex++;
                        var targetKey = $"contribution:{target.ContributionId:D}";
                        if (!CanAssign(code, targetKey)) continue;
                        AddOutcome(target.ContributionId, code);
                        break;
                    }
                    break;
                }
            }
        }

        return outcomes;
    }

    private static async Task<IReadOnlyList<SimulatedContributionOutcome>> BuildStressContributionOutcomesAsync(
        IAlphaDbContext db,
        Guid reportId,
        CancellationToken ct)
    {
        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .Select(x => new { x.Id })
            .ToArrayAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .Select(x => new { x.Id, x.ReportEmployeeId })
            .ToArrayAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var employeeByProduct = products.ToDictionary(x => x.Id, x => x.ReportEmployeeId);

        var rows = (await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .OrderBy(x => x.ReportProductId)
            .ThenBy(x => x.Id)
            .ToListAsync(ct))
            .Where(ReportFeedbackStatusResolver.IsEffectiveContribution)
            .Select(x => new SimulatedStressContribution(
                x.Id,
                x.ReportProductId,
                employeeByProduct[x.ReportProductId]))
            .ToArray();

        return BuildStressContributionOutcomes(rows);
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
            // A hybrid report has independent recipients. A technical rejection
            // is a failure of this route only; do not put the entire report
            // into Error and strand accepted manufacturer destinations.
            if (string.IsNullOrWhiteSpace(transmission.RoutingKey))
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

        var transmission = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.Id == instruction.TransmissionId
                && x.ReportId == instruction.ReportId
                && x.EmployerId == instruction.EmployerId)
            .Select(x => new { x.RoutingKey, x.RoutedProductIdsJson })
            .SingleOrDefaultAsync(ct);
        if (transmission is null) return null;

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
        if (!string.IsNullOrWhiteSpace(transmission.RoutingKey))
        {
            Guid[] allowedIds;
            try { allowedIds = JsonSerializer.Deserialize<Guid[]>(transmission.RoutedProductIdsJson) ?? []; }
            catch (JsonException) { return null; }
            var allowed = allowedIds.ToHashSet();
            if (allowed.Count == 0 || products.Count(product => allowed.Contains(product.Id)) != allowed.Count)
                return null;
            products = products.Where(product => allowed.Contains(product.Id)).ToList();
        }
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
        var outcomesByContribution = (instruction.ContributionOutcomes ?? [])
            .Where(x => EmployerInterfaceLineFeedbackParser.IsOfficialErrorCode(x.ErrorCode) && x.ErrorCode != 1)
            .GroupBy(x => x.ContributionId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(x => x.Sequence).ThenBy(x => x.ErrorCode).ToArray());

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
            var fallbackErrorCode = contributionScenario.Mode switch
            {
                "error" => contributionScenario.ErrorCode ?? selectedErrorCode ?? 53,
                "partial" => contributionScenario.ErrorCode ?? selectedErrorCode ?? 53,
                _ => 1
            };
            var contributionOutcomes = outcomesByContribution.TryGetValue(contribution.Id, out var stressOutcomes)
                ? stressOutcomes
                : [new SimulatedContributionOutcome(contribution.Id, fallbackErrorCode, 0)];

            foreach (var outcome in contributionOutcomes)
            {
                var errorCode = outcome.ErrorCode;
                var intakeStatus = errorCode == 1 ? 1 : 2;
                var recordId = string.IsNullOrWhiteSpace(contribution.InterfaceRecordIdentifier)
                    ? contribution.Id.ToString("D").ToUpperInvariant()
                    : contribution.InterfaceRecordIdentifier;

                // Only error 53 is a direct salary/rate/amount reconciliation error.
                // Make that mismatch visible while keeping other catalog errors numerically
                // unchanged so the UI can distinguish data gaps from non-numeric manufacturer errors.
                var simulatedContributionAmount = errorCode == 53
                    ? contribution.Amount + 1m
                    : contribution.Amount;

                db.EmployerInterfaceContributionFeedback.Add(new EmployerInterfaceContributionFeedback(
                    feedback.Id,
                    instruction.ReportId,
                    contribution.ReportProductId,
                    contribution.Id,
                    recordId,
                    outcome.Sequence,
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
        }

        await db.SaveChangesAsync(ct);
        return feedback.Id;
    }
}
