using Alpha.Modules.Reporting.Infrastructure.Simulation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Alpha.Modules.Reporting.Infrastructure.Transmissions;

/// <summary>Which clearinghouse delivery path the process uses. Exactly one is registered.</summary>
public enum ClearinghouseProvider
{
    /// <summary>The internal TEST simulator (<c>SimulatedVault</c> and optional manufacturer vaults).</summary>
    Simulated,
    /// <summary>The real clearinghouse. Not implemented yet, so selecting it stops startup.</summary>
    Real,
    /// <summary>In-process mock that accepts immediately; Development or <c>AllowMockTransmission</c> only.</summary>
    Mock,
    /// <summary>No delivery: transmissions are refused with 503.</summary>
    Disabled
}

/// <summary>The provider selected at startup, for logging and diagnostics.</summary>
public sealed record SelectedClearinghouseProvider(ClearinghouseProvider Provider);

/// <summary>
/// Resolves <c>EmployerInterface006:ClearinghouseProvider</c>. The selection is explicit and validated at startup:
/// an invalid or incomplete configuration stops the process instead of falling back to another provider.
/// The ASP.NET Core environment never selects the provider; <c>Production</c> may run the simulator.
/// </summary>
public static class ClearinghouseProviderSelection
{
    public const string SettingKey = "EmployerInterface006:ClearinghouseProvider";
    private const string LegacyEnabledKey = SimulatedClearinghouseVaultOptions.SectionName + ":Enabled";

    public static ClearinghouseProvider Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[SettingKey]?.Trim();
        if (string.IsNullOrEmpty(configured))
            throw Invalid($"{SettingKey} is required: Simulated, Real, Mock or Disabled.");
        if (!Enum.TryParse<ClearinghouseProvider>(configured, ignoreCase: true, out var provider)
            || !Enum.IsDefined(provider) || int.TryParse(configured, out _))
            throw Invalid($"{SettingKey} must be Simulated, Real, Mock or Disabled.");

        // The former switch must not contradict the explicit one.
        if (bool.TryParse(configuration[LegacyEnabledKey], out var legacyEnabled)
            && legacyEnabled != (provider == ClearinghouseProvider.Simulated))
            throw Invalid($"{LegacyEnabledKey} contradicts {SettingKey}={provider}; remove {LegacyEnabledKey}.");

        var manufacturerVaults = configuration.GetSection(SimulatedClearinghouseVaultOptions.SectionName + ":Manufacturers")
            .GetChildren().Any(section => section.GetValue<bool>("Enabled"));
        if (manufacturerVaults && provider != ClearinghouseProvider.Simulated)
            throw Invalid($"Simulated manufacturer vaults are enabled but {SettingKey}={provider}.");

        switch (provider)
        {
            case ClearinghouseProvider.Simulated:
                if (configuration.GetValue<int?>("EmployerInterface006:EnvironmentCode") != 1)
                    throw Invalid($"{SettingKey}=Simulated requires EmployerInterface006:EnvironmentCode=1 (TEST files only).");
                if (string.IsNullOrWhiteSpace(configuration[SimulatedClearinghouseVaultOptions.SectionName + ":RootDirectory"]))
                    throw Invalid($"{SettingKey}=Simulated requires {SimulatedClearinghouseVaultOptions.SectionName}:RootDirectory.");
                break;
            case ClearinghouseProvider.Real:
                throw Invalid($"{SettingKey}=Real is not available: this build has no real clearinghouse integration. Use Simulated or Disabled.");
            case ClearinghouseProvider.Mock:
                if (!environment.IsDevelopment() && !configuration.GetValue<bool>("EmployerInterface006:AllowMockTransmission"))
                    throw Invalid($"{SettingKey}=Mock is allowed only in Development or with EmployerInterface006:AllowMockTransmission=true.");
                break;
        }

        return provider;
    }

    private static InvalidOperationException Invalid(string message) => new($"Invalid configuration: {message}");
}
