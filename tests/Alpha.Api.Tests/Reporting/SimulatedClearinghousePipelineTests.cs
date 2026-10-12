using Alpha.Api.Tests.TestInfrastructure;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Modules.Reporting.Contracts;
using Alpha.Modules.Reporting.Features.EmployerInterface006;
using Alpha.Modules.Reporting.Infrastructure.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

/// <summary>
/// The whole simulator chain with its real file storage: the selected provider writes the outbox, the responder
/// answers, the inbox worker ingests the feedback into the database and archives the instruction.
/// </summary>
public sealed class SimulatedClearinghousePipelineTests
{
    private const string PayloadFileName = "006000123456789EMPONG000006202610040900000001.TST";

    [Fact]
    public async Task Simulated_transmission_receives_simulated_feedback_through_the_vault()
    {
        var root = Path.Combine(Path.GetTempPath(), "alpha-simulator-" + Guid.NewGuid().ToString("N"));
        try
        {
            await AlphaApiTestHost.RunAsync(async (factory, _, ct) =>
            {
                var (employerId, reportId, transmissionId) = await SeedSentReportAsync(factory);

                // 1. The registered provider is the simulator, and it reports a simulated, queued submission.
                ReportTransmissionProviderResult sent;
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var provider = Assert.Single(scope.ServiceProvider.GetServices<IReportTransmissionProvider>());
                    Assert.Equal("SimulatedVault", provider.Name);
                    sent = await provider.SendAsync(new ReportTransmissionEnvelope(reportId, Guid.Empty, employerId,
                        "<demo />"u8.ToArray(), "0123456789abcdef0123", PayloadFileName: PayloadFileName), ct);
                }
                Assert.True(sent.Success);
                Assert.Equal("Queued", sent.Status);
                Assert.StartsWith("SIMVAULT-", sent.ExternalId, StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(root, "outbox", employerId.ToString("N"), PayloadFileName)));

                // 2. Responder and inbox worker, exactly as the host runs them.
                var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
                var vault = factory.Services.GetRequiredService<IOptions<SimulatedClearinghouseVaultOptions>>();
                var interface006 = factory.Services.GetRequiredService<IOptions<EmployerInterface006Options>>();
                using var responder = new SimulatedClearinghouseResponder(scopes, vault, interface006,
                    NullLogger<SimulatedClearinghouseResponder>.Instance);
                using var worker = new SimulatedClearinghouseVaultWorker(scopes, vault, interface006,
                    NullLogger<SimulatedClearinghouseVaultWorker>.Instance);
                await responder.StartAsync(ct);
                await worker.StartAsync(ct);
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    var ingested = false;
                    while (!ingested && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(250, ct);
                        await factory.WithDbAsync(async db => ingested =
                            await db.EmployerInterfaceFeedback.AnyAsync(x => x.TransmissionId == transmissionId, ct));
                    }
                    Assert.True(ingested, "Simulated feedback was not ingested within 30 seconds.");
                }
                finally
                {
                    await responder.StopAsync(CancellationToken.None);
                    await worker.StopAsync(CancellationToken.None);
                }

                // 3. Files stay inside the simulator's own tree; nothing failed.
                var employerFolder = employerId.ToString("N");
                Assert.True(File.Exists(Path.Combine(root, "responded", employerFolder, PayloadFileName + ".done")));
                Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "processed", employerFolder), "*.simulation.json"));
                Assert.False(Directory.Exists(Path.Combine(root, "failed", employerFolder)));
            }, new Dictionary<string, string?>
            {
                ["EmployerInterface006:ClearinghouseProvider"] = "Simulated",
                ["EmployerInterface006:EnvironmentCode"] = "1",
                ["EmployerInterface006:SimulatedVault:RootDirectory"] = root,
                ["EmployerInterface006:SimulatedVault:DefaultScenario"] = "success",
                ["EmployerInterface006:SimulatedVault:ResponseDelaySeconds"] = "0",
                ["EmployerInterface006:SimulatedVault:PollIntervalSeconds"] = "1"
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A report already marked sent, with its accepted transmission recorded under the simulator's name.</summary>
    private static async Task<(Guid EmployerId, Guid ReportId, Guid TransmissionId)> SeedSentReportAsync(AlphaApiTestHost.Factory factory)
    {
        var organization = new Organization("Simulator Pipeline", OrganizationType.PayrollOffice);
        var employer = new Employer(organization.Id, "Simulator Employer", "512345678", "987654321",
            "Test", "Contact", "031234567", "test@example.test", "0501234567");
        var person = new Person(organization.Id, "123456782", "Test", "Employee",
            new DateOnly(1990, 1, 1), PersonGender.Female, "employee@example.test", "0501111111",
            "תל אביב", "הרצל", "1", "1", "6100000", null);
        var employment = new Employment(organization.Id, employer.Id, person.Id, new DateOnly(2025, 1, 1), "E-1", 10_000m);
        var report = new ManualReport(organization.Id, employer.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        report.MarkReadyForValidation();
        report.MarkValidated();
        report.MarkTransmissionStarted();
        report.MarkSent();
        var reportEmployee = new ManualReportEmployee(report.Id, organization.Id, employer.Id,
            employment.Id, person.Id, "123456782", "Test", "Employee", "E-1", 10_000m);
        var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund,
            "123456", new DateOnly(2026, 9, 1), 10_000m, "שוטף", "", false, null,
            fundExternalKey: "demo-fund", fundCode: "1234", fundName: "Demo Fund");
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee,
            ContributionComponent.Benefits, 600m, 6m, 0m);
        contribution.SetInterfaceRecordIdentifier(Guid.NewGuid().ToString("D"));
        var transmission = new ReportTransmission(report.Id, organization.Id, employer.Id, "SimulatedVault", 1);
        transmission.Start("0123456789abcdef0123", PayloadFileName, "<demo />"u8.ToArray());
        transmission.Complete(ReportTransmissionStatus.Accepted, "SIMVAULT-0123456789ABCDEF", "queued", null);

        await factory.WithDbAsync(async db =>
        {
            db.AddRange(organization, employer, person, employment, report, reportEmployee, product, contribution, transmission);
            await db.SaveChangesAsync();
        });
        return (employer.Id, report.Id, transmission.Id);
    }
}
