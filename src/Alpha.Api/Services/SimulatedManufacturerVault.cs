using Alpha.Application.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Alpha.Api.Services;

/// <summary>
/// An independently configured, TEST-only simulated manufacturer vault.
/// Each manufacturer's outbox/inbox is isolated from the clearinghouse and peers.
/// </summary>
public sealed class SimulatedManufacturerVaultReportTransmissionProvider
    : IReportTransmissionProvider
{
    private readonly SimulatedVaultReportTransmissionProvider _inner;

    public SimulatedManufacturerVaultReportTransmissionProvider(
        IOptions<SimulatedClearinghouseVaultOptions> defaults,
        IOptions<EmployerInterface006Options> employerOptions,
        string manufacturer,
        IConfiguration? configuration = null)
    {
        Manufacturer = ValidateKey(manufacturer);
        Name = ProviderFor(Manufacturer);
        _inner = new SimulatedVaultReportTransmissionProvider(
            Options.Create(CreateManufacturerOptions(defaults.Value, Manufacturer, configuration)),
            employerOptions);
    }

    public string Manufacturer { get; }
    public string Name { get; }
    public bool IsConfigured => _inner.IsConfigured;

    public Task<ReportTransmissionProviderResult> SendAsync(
        ReportTransmissionEnvelope envelope, CancellationToken ct) =>
        _inner.SendAsync(envelope, ct);

    public static string ProviderFor(string manufacturer) => "SimulatedVault-" + ValidateKey(manufacturer);

    public static string ValidateKey(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)
            || !Regex.IsMatch(manufacturer, @"^[a-zA-Z0-9_-]{1,60}$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Simulated manufacturer key must be a safe ASCII identifier.", nameof(manufacturer));
        return manufacturer;
    }

    public static SimulatedClearinghouseVaultOptions CreateManufacturerOptions(
        SimulatedClearinghouseVaultOptions defaults, string manufacturer,
        IConfiguration? configuration = null)
    {
        var key = ValidateKey(manufacturer);
        var overrides = configuration?.GetSection(
            $"EmployerInterface006:SimulatedVault:Manufacturers:{key}");
        return new()
        {
            Enabled = defaults.Enabled,
            ProviderName = ProviderFor(key),
            RootDirectory = Path.Combine(defaults.RootDirectory, "manufacturers",
                key.ToLowerInvariant()),
            PollIntervalSeconds = overrides?.GetValue<int?>("PollIntervalSeconds") ?? defaults.PollIntervalSeconds,
            AutoRespond = overrides?.GetValue<bool?>("AutoRespond") ?? defaults.AutoRespond,
            DefaultScenario = overrides?.GetValue<string>("DefaultScenario") ?? defaults.DefaultScenario,
            ResponseDelaySeconds = overrides?.GetValue<int?>("ResponseDelaySeconds") ?? defaults.ResponseDelaySeconds
        };
    }
}

/// <summary>
/// Runs a separate inbox worker and mock responder for ONE manufacturer;
/// the existing clearinghouse workers continue to use their own root.
/// </summary>
public sealed class SimulatedManufacturerVaultHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<EmployerInterface006Options> _employerOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SimulatedClearinghouseVaultOptions _settings;
    private SimulatedClearinghouseResponder? _responder;
    private SimulatedClearinghouseVaultWorker? _worker;

    public SimulatedManufacturerVaultHostedService(
        IServiceScopeFactory scopes,
        IOptions<SimulatedClearinghouseVaultOptions> defaults,
        IOptions<EmployerInterface006Options> employerOptions,
        ILoggerFactory loggerFactory,
        string manufacturer,
        IConfiguration? configuration = null)
    {
        _scopes = scopes;
        _employerOptions = employerOptions;
        _loggerFactory = loggerFactory;
        _settings = SimulatedManufacturerVaultReportTransmissionProvider
            .CreateManufacturerOptions(defaults.Value, manufacturer, configuration);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var options = Options.Create(_settings);
        _worker = new SimulatedClearinghouseVaultWorker(_scopes, options,
            _employerOptions, _loggerFactory.CreateLogger<SimulatedClearinghouseVaultWorker>());
        _responder = new SimulatedClearinghouseResponder(_scopes, options,
            _employerOptions, _loggerFactory.CreateLogger<SimulatedClearinghouseResponder>());
        await _worker.StartAsync(ct);
        await _responder.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_responder is not null) await _responder.StopAsync(ct);
        if (_worker is not null) await _worker.StopAsync(ct);
    }
}
