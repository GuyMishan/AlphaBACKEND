using Alpha.Api.Services;
using Alpha.Application.Reporting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class SimulatedManufacturerVaultTests
{
    [Fact]
    public void Menora_vault_isolated_from_clearinghouse_root_and_provider()
    {
        var settings = new SimulatedClearinghouseVaultOptions
        {
            Enabled = true,
            RootDirectory = "alpha-test-vault",
            AutoRespond = true,
            DefaultScenario = "success"
        };

        var menora = SimulatedManufacturerVaultReportTransmissionProvider
            .CreateMenoraOptions(settings);
        Assert.Equal("SimulatedVault-Menora", menora.ProviderName);
        Assert.Equal(Path.Combine(settings.RootDirectory, "manufacturers", "menora"),
            menora.RootDirectory);
        Assert.NotEqual(settings.RootDirectory, menora.RootDirectory);
        Assert.Equal(settings.DefaultScenario, menora.DefaultScenario);
    }

    [Fact]
    public async Task Menora_and_clearinghouse_write_to_separate_immutable_test_outboxes()
    {
        var root = Path.Combine(Path.GetTempPath(), "alpha-menora-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vault = Options.Create(new SimulatedClearinghouseVaultOptions
            {
                Enabled = true,
                RootDirectory = root,
                AutoRespond = false
            });
            var testEnvironment = Options.Create(new EmployerInterface006Options { EnvironmentCode = 1 });
            var clearing = new SimulatedVaultReportTransmissionProvider(vault, testEnvironment);
            var menora = new SimulatedManufacturerVaultReportTransmissionProvider(vault, testEnvironment);
            var payloadName = EmployerInterface006FileNaming.Build("123456789", 6, false,
                new DateTimeOffset(2026, 10, 8, 11, 0, 0, TimeSpan.FromHours(3)),
                testFile: true).PayloadFileName;
            var employerId = Guid.NewGuid();
            var bytes = new byte[] { 1, 2, 3, 4 };
            var envelope = new ReportTransmissionEnvelope(Guid.NewGuid(), Guid.NewGuid(),
                employerId, bytes, "0123456789ABCDEF", [], payloadName);

            Assert.True(clearing.IsConfigured);
            Assert.True(menora.IsConfigured);
            Assert.Equal("SimulatedVault-Menora", menora.Name);

            var clearingResult = await clearing.SendAsync(envelope, CancellationToken.None);
            var menoraResult = await menora.SendAsync(envelope, CancellationToken.None);
            Assert.True(clearingResult.Success);
            Assert.True(menoraResult.Success);
            var folder = employerId.ToString("N");
            Assert.True(File.Exists(Path.Combine(root, "outbox", folder, payloadName)));
            Assert.True(File.Exists(Path.Combine(root, "manufacturers", "menora",
                "outbox", folder, payloadName)));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root,
                "manufacturers", "menora", "outbox", folder, payloadName)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Menora_vault_cannot_be_configured_in_production_environment()
    {
        var vault = Options.Create(new SimulatedClearinghouseVaultOptions { Enabled = true });
        var prod = Options.Create(new EmployerInterface006Options { EnvironmentCode = 2 });
        var menora = new SimulatedManufacturerVaultReportTransmissionProvider(vault, prod);

        Assert.False(menora.IsConfigured);
    }

    [Fact]
    public void Registration_and_ingestion_reject_other_provider_feedback()
    {
        var api = Read("src", "Alpha.Api", "Program.cs");
        var vault = Read("src", "Alpha.Api", "Services", "SimulatedClearinghouseVault.cs");
        var responder = Read("src", "Alpha.Api", "Services", "SimulatedClearinghouseResponder.cs");

        Assert.Contains("SimulatedVault:Manufacturers:Menora:Enabled", api);
        Assert.Contains("simulatedVaultEnabled", api);
        Assert.Contains("transmission.Provider == _options.ProviderName", vault);
        Assert.Contains("x.Provider == _options.ProviderName", responder);
        Assert.Contains("products = products.Where(product => allowed.Contains(product.Id)).ToList()", responder);
    }

    private static string Read(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlphaBackend.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName
            ?? throw new InvalidOperationException("Solution root missing."), Path.Combine(segments)));
    }
}
