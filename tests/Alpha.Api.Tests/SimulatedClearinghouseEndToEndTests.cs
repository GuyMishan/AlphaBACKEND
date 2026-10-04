using Alpha.Api.Security;
using Alpha.Api.Services;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class SimulatedClearinghouseEndToEndTests
{
    [Fact]
    public async Task Demo_clearinghouse_ingests_success_partial_error_and_in_transit_feedback()
    {
        await WithDatabase(async (db, ct) =>
        {
            var fixture = await CreateReportFixtureAsync(db, ct);
            var ingestor = NewIngestor(db);

            var scenarios = new[]
            {
                new SimulatedVaultFeedbackInstruction(fixture.Report.Id, fixture.Transmission.Id,
                    fixture.Organization.Id, fixture.Employer.Id, "success", fixture.Transmission.PayloadFileName,
                    DateTimeOffset.UtcNow),
                new SimulatedVaultFeedbackInstruction(fixture.Report.Id, fixture.Transmission.Id,
                    fixture.Organization.Id, fixture.Employer.Id, "partial", fixture.Transmission.PayloadFileName,
                    DateTimeOffset.UtcNow.AddMilliseconds(1), 116),
                new SimulatedVaultFeedbackInstruction(fixture.Report.Id, fixture.Transmission.Id,
                    fixture.Organization.Id, fixture.Employer.Id, "error", fixture.Transmission.PayloadFileName,
                    DateTimeOffset.UtcNow.AddMilliseconds(2), 53),
                new SimulatedVaultFeedbackInstruction(fixture.Report.Id, fixture.Transmission.Id,
                    fixture.Organization.Id, fixture.Employer.Id, "in-transit", fixture.Transmission.PayloadFileName,
                    DateTimeOffset.UtcNow.AddMilliseconds(3))
            };

            var feedbackIds = new List<Guid>();
            for (var i = 0; i < scenarios.Length; i++)
            {
                var id = await ingestor.IngestAsync(
                    fixture.Employer.Id, scenarios[i], $"scenario-{i}.simulation.json", ct);
                feedbackIds.Add(Assert.IsType<Guid>(id));
            }

            var contributions = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
                .Where(x => feedbackIds.Contains(x.FeedbackId))
                .OrderBy(x => x.ReceivedAt)
                .ToListAsync(ct);
            var transfers = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
                .Where(x => feedbackIds.Contains(x.FeedbackId))
                .OrderBy(x => x.ReceivedAt)
                .ToListAsync(ct);

            Assert.Equal(4, contributions.Count);
            Assert.Equal([1, 116, 53, 1], contributions.Select(x => x.ErrorCode).ToArray());

            Assert.Equal("allocated", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[0].ReportedDepositAmount, transfers[0].ActualReceivedAmount,
                transfers[0].AllocatedAmount, transfers[0].InTransitAmount));
            Assert.Equal("received-partial", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[1].ReportedDepositAmount, transfers[1].ActualReceivedAmount,
                transfers[1].AllocatedAmount, transfers[1].InTransitAmount));
            Assert.Equal("unresolved", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[2].ReportedDepositAmount, transfers[2].ActualReceivedAmount,
                transfers[2].AllocatedAmount, transfers[2].InTransitAmount));
            Assert.Equal("in-transit", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[3].ReportedDepositAmount, transfers[3].ActualReceivedAmount,
                transfers[3].AllocatedAmount, transfers[3].InTransitAmount));
        });
    }

    [Fact]
    public async Task Demo_clearinghouse_can_ingest_every_official_v006_feedback_code()
    {
        await WithDatabase(async (db, ct) =>
        {
            var fixture = await CreateReportFixtureAsync(db, ct);
            var ingestor = NewIngestor(db);
            var feedbackIds = new List<Guid>();

            foreach (var code in EmployerInterfaceLineFeedbackParser.OfficialErrorCodes)
            {
                var mode = code == 1 ? "success" : "error";
                var instruction = new SimulatedVaultFeedbackInstruction(
                    fixture.Report.Id,
                    fixture.Transmission.Id,
                    fixture.Organization.Id,
                    fixture.Employer.Id,
                    mode,
                    fixture.Transmission.PayloadFileName,
                    DateTimeOffset.UtcNow.AddTicks(code),
                    code == 1 ? null : code);

                var id = await ingestor.IngestAsync(
                    fixture.Employer.Id,
                    instruction,
                    $"official-error-{code:000}.simulation.json",
                    ct);
                feedbackIds.Add(Assert.IsType<Guid>(id));
            }

            var stored = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
                .Where(x => feedbackIds.Contains(x.FeedbackId))
                .Select(x => new { x.ErrorCode, x.ErrorDescription })
                .ToListAsync(ct);

            Assert.Equal(EmployerInterfaceLineFeedbackParser.OfficialErrorCodes.Count, stored.Count);
            Assert.Equal(
                EmployerInterfaceLineFeedbackParser.OfficialErrorCodes.OrderBy(x => x),
                stored.Select(x => x.ErrorCode!.Value).OrderBy(x => x));

            foreach (var item in stored)
            {
                Assert.Equal(
                    EmployerInterfaceLineFeedbackParser.Description(item.ErrorCode),
                    item.ErrorDescription);
            }
        });
    }

    private static SimulatedClearinghouseFeedbackIngestor NewIngestor(AlphaDbContext db)
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:DataProtectionKey"] = key
            })
            .Build();
        return new SimulatedClearinghouseFeedbackIngestor(db, new AesDataProtectionService(configuration));
    }

    private static async Task<Fixture> CreateReportFixtureAsync(AlphaDbContext db, CancellationToken ct)
    {
        var organization = new Organization("Demo Clearinghouse", OrganizationType.PayrollOffice);
        var employer = new Employer(organization.Id, "Demo Employer", "512345678", "987654321",
            "Test", "Contact", "031234567", "test@example.test", "0501234567");
        var person = new Person(organization.Id, "123456782", "Test", "Employee",
            new DateOnly(1990, 1, 1), PersonGender.Female, "employee@example.test", "0501111111",
            "תל אביב", "הרצל", "1", "1", "6100000", null);
        var employment = new Employment(organization.Id, employer.Id, person.Id,
            new DateOnly(2025, 1, 1), "E-1", 10_000m);
        var report = new ManualReport(organization.Id, employer.Id,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        var reportEmployee = new ManualReportEmployee(report.Id, organization.Id, employer.Id,
            employment.Id, person.Id, "123456782", "Test", "Employee", "E-1", 10_000m);
        var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund,
            "123456", new DateOnly(2026, 9, 1), 10_000m, "שוטף", "", false, null,
            fundExternalKey: "demo-fund", fundCode: "1234", fundName: "Demo Fund");
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee,
            ContributionComponent.Benefits, 600m, 6m, 0m);
        contribution.SetInterfaceRecordIdentifier(Guid.NewGuid().ToString("D"));

        var transmission = new ReportTransmission(report.Id, organization.Id, employer.Id, "SimulatedVault", 1);
        transmission.Start("demo-hash",
            "006000123456789EMPONG000006202610040900000001.TST",
            "<demo />"u8.ToArray());
        transmission.Complete(ReportTransmissionStatus.Accepted, "SIM-DEMO", "queued", null);

        db.AddRange(organization, employer, person, employment, report, reportEmployee, product, contribution, transmission);
        await db.SaveChangesAsync(ct);

        return new Fixture(organization, employer, report, transmission);
    }

    private static async Task WithDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_simulated_clearinghouse_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql(cs.ConnectionString).Options;
            await using var db = new AlphaDbContext(options);
            await db.Database.EnsureCreatedAsync(ct);
            await test(db, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(baseConnection);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private sealed record Fixture(
        Organization Organization,
        Employer Employer,
        ManualReport Report,
        ReportTransmission Transmission);
}
