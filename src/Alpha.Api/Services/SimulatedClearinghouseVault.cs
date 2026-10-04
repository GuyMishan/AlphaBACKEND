using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Services;

public sealed class SimulatedClearinghouseVaultOptions
{
    public const string SectionName = "EmployerInterface006:SimulatedVault";

    public bool Enabled { get; set; }
    public string RootDirectory { get; set; } = "simulated-clearinghouse-vault";
    public int PollIntervalSeconds { get; set; } = 5;
    public bool AutoRespond { get; set; } = true;
    public string DefaultScenario { get; set; } = "success";
    public int ResponseDelaySeconds { get; set; } = 2;
}

public sealed class SimulatedVaultReportTransmissionProvider(
    IOptions<SimulatedClearinghouseVaultOptions> vaultOptions,
    IOptions<EmployerInterface006Options> employerInterfaceOptions) : IReportTransmissionProvider
{
    private readonly SimulatedClearinghouseVaultOptions _vault = vaultOptions.Value;
    private readonly EmployerInterface006Options _employerInterface = employerInterfaceOptions.Value;

    public string Name => "SimulatedVault";

    public bool IsConfigured =>
        _vault.Enabled
        && _employerInterface.EnvironmentCode == 1
        && !string.IsNullOrWhiteSpace(_vault.RootDirectory);

    public async Task<ReportTransmissionProviderResult> SendAsync(
        ReportTransmissionEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new ReportTransmissionProviderResult(
                false,
                "Unavailable",
                null,
                null,
                "The simulated clearing-house vault is disabled or Employer Interface environment is not TEST.");
        }

        var payloadFileName = SafeFileName(envelope.PayloadFileName);
        if (string.IsNullOrWhiteSpace(payloadFileName))
            return new ReportTransmissionProviderResult(false, "Rejected", null, null, "A payload file name is required.");
        if (!EmployerInterface006FileNaming.IsOfficialOutboundEmployerInterfaceName(payloadFileName))
            return new ReportTransmissionProviderResult(
                false,
                "Rejected",
                null,
                null,
                "The simulated clearing-house vault only accepts official Annex VI Employer Interface outbound file names.");

        var employerFolder = envelope.EmployerId.ToString("N");
        var outbox = Path.Combine(ResolveRoot(_vault.RootDirectory), "outbox", employerFolder);
        Directory.CreateDirectory(outbox);

        var payloadPath = Path.Combine(outbox, payloadFileName);
        await WriteAtomicallyAsync(payloadPath, envelope.Payload, cancellationToken);

        if (envelope.Attachments is { Count: > 0 })
        {
            var attachmentsFolder = Path.Combine(outbox, payloadFileName + ".attachments");
            Directory.CreateDirectory(attachmentsFolder);
            foreach (var attachment in envelope.Attachments)
            {
                var attachmentName = SafeFileName(attachment.FileName);
                if (string.IsNullOrWhiteSpace(attachmentName)) continue;
                await WriteAtomicallyAsync(
                    Path.Combine(attachmentsFolder, attachmentName),
                    attachment.Content,
                    cancellationToken);
            }
        }

        var externalId = $"SIMVAULT-{envelope.PayloadHash[..Math.Min(16, envelope.PayloadHash.Length)].ToUpperInvariant()}";
        var response = JsonSerializer.Serialize(new
        {
            queued = true,
            externalId,
            provider = Name,
            payloadFileName,
            employerId = envelope.EmployerId,
            inboxConvention = $"inbox/{employerFolder}"
        });

        return new ReportTransmissionProviderResult(true, "Queued", externalId, response, null);
    }

    private static string SafeFileName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFileName(value.Trim());

    private static string ResolveRoot(string configuredRoot) =>
        Path.GetFullPath(configuredRoot.Trim());

    private static async Task WriteAtomicallyAsync(string targetPath, byte[] content, CancellationToken ct)
    {
        if (File.Exists(targetPath))
        {
            var existing = await File.ReadAllBytesAsync(targetPath, ct);
            if (existing.AsSpan().SequenceEqual(content))
                return;
            throw new IOException($"A different file already exists in the simulated vault: {Path.GetFileName(targetPath)}");
        }

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

public sealed class SimulatedClearinghouseVaultWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SimulatedClearinghouseVaultOptions> options,
    IOptions<EmployerInterface006Options> employerInterfaceOptions,
    ILogger<SimulatedClearinghouseVaultWorker> logger) : BackgroundService
{
    private readonly SimulatedClearinghouseVaultOptions _options = options.Value;
    private readonly EmployerInterface006Options _employerInterface = employerInterfaceOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _employerInterface.EnvironmentCode != 1)
            return;

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessInboxAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Simulated clearing-house vault inbox scan failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ProcessInboxAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(_options.RootDirectory.Trim());
        var inboxRoot = Path.Combine(root, "inbox");
        Directory.CreateDirectory(inboxRoot);

        foreach (var employerDirectory in Directory.EnumerateDirectories(inboxRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileName(employerDirectory), out var employerId))
                continue;

            var candidates = Directory.EnumerateFiles(employerDirectory)
                .Where(IsFeedbackFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var path in candidates)
                await ProcessFileAsync(root, employerId, path, ct);
        }
    }

    private async Task ProcessFileAsync(string root, Guid employerId, string inboxPath, CancellationToken ct)
    {
        var safeName = Path.GetFileName(inboxPath);
        var processingDirectory = Path.Combine(root, "processing", employerId.ToString("N"));
        Directory.CreateDirectory(processingDirectory);
        var processingPath = Path.Combine(processingDirectory, $"{Guid.NewGuid():N}-{safeName}");

        try
        {
            File.Move(inboxPath, processingPath);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAlphaDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<EmployerInterfaceService>();

            var employer = await db.Employers.AsNoTracking()
                .Where(x => x.Id == employerId)
                .Select(x => new { x.Id, x.OrganizationId })
                .SingleOrDefaultAsync(ct);
            if (employer is null)
            {
                MoveTo(root, "failed", employerId, processingPath, safeName);
                logger.LogWarning("Simulated vault feedback could not be routed because employer {EmployerId} does not exist.", employerId);
                return;
            }

            var info = new FileInfo(processingPath);
            if (info.Length <= 0 || info.Length > 20L * 1024 * 1024)
            {
                MoveTo(root, "failed", employerId, processingPath, safeName);
                logger.LogWarning("Simulated vault feedback for employer {EmployerId} failed the file-size boundary.", employerId);
                return;
            }

            if (safeName.EndsWith(".simulation.json", StringComparison.OrdinalIgnoreCase))
            {
                var json = await File.ReadAllTextAsync(processingPath, ct);
                var instruction = JsonSerializer.Deserialize<SimulatedVaultFeedbackInstruction>(json);
                if (instruction is null)
                {
                    MoveTo(root, "failed", employerId, processingPath, safeName);
                    return;
                }

                var simulatedIngestor = scope.ServiceProvider.GetRequiredService<SimulatedClearinghouseFeedbackIngestor>();
                var feedbackId = await simulatedIngestor.IngestAsync(employerId, instruction, safeName, ct);
                if (!feedbackId.HasValue)
                {
                    MoveTo(root, "failed", employerId, processingPath, safeName);
                    logger.LogWarning("Simulated vault scenario feedback for employer {EmployerId} was not ingested.", employerId);
                    return;
                }

                MoveTo(root, "processed", employerId, processingPath, safeName);
                logger.LogInformation(
                    "Simulated clearing-house scenario feedback was ingested for employer {EmployerId}; feedback {FeedbackId}.",
                    employerId,
                    feedbackId.Value);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(processingPath, ct);
            var validation = service.Validate(bytes);
            if (!validation.IsValid
                || validation.DocumentType is not (EmployerInterfaceDocumentType.SummaryFeedback
                    or EmployerInterfaceDocumentType.AnnualSummaryFeedback))
            {
                MoveTo(root, "failed", employerId, processingPath, safeName);
                logger.LogWarning("Simulated vault feedback for employer {EmployerId} failed official feedback validation.", employerId);
                return;
            }

            var result = await service.IngestAsync(
                employer.OrganizationId,
                employer.Id,
                safeName,
                bytes,
                paymentAccountId: null,
                salaryPaymentDate: null,
                ct);

            if (!result.Validation.IsValid || result.FeedbackId is null)
            {
                MoveTo(root, "failed", employerId, processingPath, safeName);
                logger.LogWarning("Simulated vault feedback for employer {EmployerId} was not ingested.", employerId);
                return;
            }

            MoveTo(root, "processed", employerId, processingPath, safeName);
            logger.LogInformation(
                "Simulated clearing-house feedback was ingested for employer {EmployerId}; feedback {FeedbackId}.",
                employerId,
                result.FeedbackId);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Simulated vault file claim/move failed for employer {EmployerId}.", employerId);
        }
        catch (Exception ex)
        {
            if (File.Exists(processingPath))
                MoveTo(root, "failed", employerId, processingPath, safeName);
            logger.LogError(ex, "Simulated vault feedback processing failed for employer {EmployerId}.", employerId);
        }
    }

    private static bool IsFeedbackFile(string path)
    {
        var extension = Path.GetExtension(path);
        return path.EndsWith(".simulation.json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tst", StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveTo(string root, string bucket, Guid employerId, string sourcePath, string originalName)
    {
        var destinationDirectory = Path.Combine(root, bucket, employerId.ToString("N"));
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, originalName);
        if (File.Exists(destinationPath))
        {
            var name = Path.GetFileNameWithoutExtension(originalName);
            var extension = Path.GetExtension(originalName);
            destinationPath = Path.Combine(destinationDirectory, $"{name}-{Guid.NewGuid():N}{extension}");
        }
        File.Move(sourcePath, destinationPath);
    }
}
