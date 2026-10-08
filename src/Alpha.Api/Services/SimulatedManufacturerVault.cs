using Alpha.Application.Reporting;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Services;

/// <summary>
/// Separate TEST-only vault for a manufacturer. Uses the same 006 transport
/// and feedback pipeline as the simulated clearing house but with an isolated root.
/// </summary>
public sealed class SimulatedManufacturerVaultReportTransmissionProvider(
    IOptions<SimulatedClearinghouseVaultOptions> vaultOptions,
    IOptions<EmployerInterface006Options> employerInterfaceOptions)
    : IReportTransmissionProvider
{
    public const string ManufacturerKey = "Menora";
    public const string ProviderName = "SimulatedVault-Menora";

    private readonly SimulatedVaultReportTransmissionProvider _inner =
        new(Options.Create(CreateMenoraOptions(vaultOptions.Value)), employerInterfaceOptions);

    public string Name => ProviderName;
    public bool IsConfigured => _inner.IsConfigured;

    public Task<ReportTransmissionProviderResult> SendAsync(
        ReportTransmissionEnvelope envelope, CancellationToken ct) =>
        _inner.SendAsync(envelope, ct);

    public static SimulatedClearinghouseVaultOptions CreateMenoraOptions(
        SimulatedClearinghouseVaultOptions defaults) => new()
    {
        Enabled = defaults.Enabled,
        ProviderName = ProviderName,
        RootDirectory = Path.Combine(defaults.RootDirectory, "manufacturers", "menora"),
        PollIntervalSeconds = defaults.PollIntervalSeconds,
        AutoRespond = defaults.AutoRespond,
        DefaultScenario = defaults.DefaultScenario,
        ResponseDelaySeconds = defaults.ResponseDelaySeconds
    };
}

/// <summary>
/// Hosts Menora's isolated inbox and mock responder without re-registering
/// the clearing-house background service type or sharing any vault folders.
/// </summary>
public sealed class SimulatedMenoraVaultHostedService(
    IServiceScopeFactory scopes,
    IOptions<SimulatedClearinghouseVaultOptions> defaults,
    IOptions<EmployerInterface006Options> employerOptions,
    ILoggerFactory loggerFactory) : IHostedService
{
    private readonly SimulatedClearinghouseVaultOptions _settings =
        SimulatedManufacturerVaultReportTransmissionProvider.CreateMenoraOptions(defaults.Value);
    private SimulatedClearinghouseResponder? _responder;
    private SimulatedClearinghouseVaultWorker? _worker;

    public async Task StartAsync(CancellationToken ct)
    {
        var settings = Options.Create(_settings);
        _worker = new SimulatedClearinghouseVaultWorker(scopes, settings, employerOptions,
            loggerFactory.CreateLogger<SimulatedClearinghouseVaultWorker>());
        _responder = new SimulatedClearinghouseResponder(scopes, settings, employerOptions,
            loggerFactory.CreateLogger<SimulatedClearinghouseResponder>());
        await _worker.StartAsync(ct);
        await _responder.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_responder is not null) await _responder.StopAsync(ct);
        if (_worker is not null) await _worker.StopAsync(ct);
    }
}
