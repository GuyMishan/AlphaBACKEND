using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class SimulatedClearinghouseVaultTests
{
    [Fact]
    public async Task Provider_writes_payload_and_attachments_to_employer_outbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "alpha-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new SimulatedVaultReportTransmissionProvider(
                Options.Create(new SimulatedClearinghouseVaultOptions
                {
                    Enabled = true,
                    RootDirectory = root
                }),
                Options.Create(new EmployerInterface006Options { EnvironmentCode = 1 }));

            var employerId = Guid.NewGuid();
            var payload = "<xml>payload</xml>"u8.ToArray();
            var result = await provider.SendAsync(
                new ReportTransmissionEnvelope(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    employerId,
                    payload,
                    "0123456789abcdef0123456789abcdef",
                    [new ReportTransmissionAttachment("proof.pdf", "application/pdf", "proof"u8.ToArray(), "hash")],
                    "003_TEST.DAT"),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("Queued", result.Status);
            Assert.True(File.Exists(Path.Combine(root, "outbox", employerId.ToString("N"), "003_TEST.DAT")));
            Assert.True(File.Exists(Path.Combine(root, "outbox", employerId.ToString("N"), "003_TEST.DAT.attachments", "proof.pdf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "success")]
    [InlineData("", "success")]
    [InlineData("SUCCESS", "success")]
    [InlineData("partial", "partial")]
    [InlineData("error", "error")]
    [InlineData("in-transit", "in-transit")]
    [InlineData("unknown", "success")]
    public void Scenario_normalization_is_safe(string? raw, string expected)
    {
        Assert.Equal(expected, SimulatedClearinghouseResponder.NormalizeScenario(raw));
    }

    [Fact]
    public void Provider_is_not_configured_in_production_environment()
    {
        var provider = new SimulatedVaultReportTransmissionProvider(
            Options.Create(new SimulatedClearinghouseVaultOptions
            {
                Enabled = true,
                RootDirectory = "vault"
            }),
            Options.Create(new EmployerInterface006Options { EnvironmentCode = 2 }));

        Assert.False(provider.IsConfigured);
    }
}
