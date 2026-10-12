using Alpha.Api.Tests.TestInfrastructure;
using Alpha.Modules.Reporting.Contracts;
using Alpha.Modules.Reporting.Infrastructure.Simulation;
using Alpha.Modules.Reporting.Infrastructure.Transmissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

/// <summary>
/// <c>EmployerInterface006:ClearinghouseProvider</c> is the only switch between the simulator and a real clearinghouse.
/// It is explicit, independent of the ASP.NET Core environment, and never falls back to another provider.
/// </summary>
public sealed class ClearinghouseProviderSelectionTests
{
    private static readonly Dictionary<string, string?> SimulatedOnRender = new()
    {
        ["EmployerInterface006:ClearinghouseProvider"] = "Simulated",
        ["EmployerInterface006:EnvironmentCode"] = "1",
        ["EmployerInterface006:SimulatedVault:RootDirectory"] = "/var/data/simulated-clearinghouse-vault"
    };

    [Theory]
    [InlineData("Simulated", ClearinghouseProvider.Simulated)]
    [InlineData("simulated", ClearinghouseProvider.Simulated)]
    [InlineData("Disabled", ClearinghouseProvider.Disabled)]
    public void Production_environment_runs_the_selected_provider(string value, ClearinghouseProvider expected)
    {
        var selected = Resolve(Environments.Production, With(SimulatedOnRender, ("EmployerInterface006:ClearinghouseProvider", value)));
        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData(null, "is required")]
    [InlineData("", "is required")]
    [InlineData("Sandbox", "must be Simulated, Real, Mock or Disabled")]
    [InlineData("0", "must be Simulated, Real, Mock or Disabled")]
    [InlineData("Real", "no real clearinghouse integration")]
    public void Missing_unknown_or_unavailable_provider_stops_startup(string? value, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Resolve(Environments.Production, With(SimulatedOnRender, ("EmployerInterface006:ClearinghouseProvider", value))));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("Invalid configuration:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Simulator_requires_TEST_environment_code_and_a_root_directory()
    {
        Assert.Contains("EnvironmentCode=1", Assert.Throws<InvalidOperationException>(() =>
            Resolve(Environments.Production, With(SimulatedOnRender, ("EmployerInterface006:EnvironmentCode", "2")))).Message);
        Assert.Contains("RootDirectory", Assert.Throws<InvalidOperationException>(() =>
            Resolve(Environments.Production, With(SimulatedOnRender, ("EmployerInterface006:SimulatedVault:RootDirectory", " ")))).Message);
    }

    [Fact]
    public void Mock_is_refused_outside_Development_unless_explicitly_allowed()
    {
        var mock = With(SimulatedOnRender, ("EmployerInterface006:ClearinghouseProvider", "Mock"));
        Assert.Throws<InvalidOperationException>(() => Resolve(Environments.Production, mock));
        Assert.Equal(ClearinghouseProvider.Mock, Resolve(Environments.Development, mock));
        Assert.Equal(ClearinghouseProvider.Mock,
            Resolve(Environments.Production, With(mock, ("EmployerInterface006:AllowMockTransmission", "true"))));
    }

    [Theory]
    [InlineData("Simulated", "false")]
    [InlineData("Disabled", "true")]
    [InlineData("Mock", "true")]
    public void Former_simulator_switch_may_not_contradict_the_selection(string provider, string legacyEnabled)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(Environments.Development, With(SimulatedOnRender,
            ("EmployerInterface006:ClearinghouseProvider", provider), ("EmployerInterface006:SimulatedVault:Enabled", legacyEnabled))));
        Assert.Contains("SimulatedVault:Enabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manufacturer_vaults_require_the_simulator()
    {
        var withVault = With(SimulatedOnRender, ("EmployerInterface006:SimulatedVault:Manufacturers:Menora:Enabled", "true"));
        Assert.Equal(ClearinghouseProvider.Simulated, Resolve(Environments.Production, withVault));
        Assert.Throws<InvalidOperationException>(() =>
            Resolve(Environments.Production, With(withVault, ("EmployerInterface006:ClearinghouseProvider", "Disabled"))));
    }

    [Theory]
    [InlineData("Simulated", new[] { "SimulatedVault" })]
    [InlineData("Mock", new[] { "MockClearinghouse" })]
    [InlineData("Disabled", new[] { "UnconfiguredClearinghouse" })]
    public async Task The_application_registers_only_the_selected_delivery_path(string provider, string[] expected)
    {
        await AlphaApiTestHost.RunAsync(async (factory, _, _) =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var registered = scope.ServiceProvider.GetServices<IReportTransmissionProvider>().Select(x => x.Name).ToArray();
            Assert.Equal(expected, registered);
            Assert.Equal(provider, scope.ServiceProvider.GetRequiredService<SelectedClearinghouseProvider>().Provider.ToString());
            Assert.Equal(provider == "Simulated",
                scope.ServiceProvider.GetRequiredService<IOptions<SimulatedClearinghouseVaultOptions>>().Value.Enabled);
        }, new Dictionary<string, string?> { ["EmployerInterface006:ClearinghouseProvider"] = provider });
    }

    private static ClearinghouseProvider Resolve(string environment, IReadOnlyDictionary<string, string?> settings) =>
        ClearinghouseProviderSelection.Resolve(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new HostingEnvironment { EnvironmentName = environment });

    private static Dictionary<string, string?> With(IReadOnlyDictionary<string, string?> settings, params (string Key, string? Value)[] changes)
    {
        var copy = new Dictionary<string, string?>(settings);
        foreach (var (key, value) in changes) copy[key] = value;
        return copy;
    }
}
