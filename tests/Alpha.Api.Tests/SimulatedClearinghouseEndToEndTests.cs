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
            Assert.Equal("in-transit", ReportFeedbackStatusResolver.ResolveMoneyState(
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

    [Fact]
    public async Task One_report_can_return_mixed_results_per_manufacturer_without_cross_contamination()
    {
        await WithDatabase(async (db, ct) =>
        {
            var fixture = await CreateMultiManufacturerReportFixtureAsync(db, ct);
            var ingestor = NewIngestor(db);

            var instruction = new SimulatedVaultFeedbackInstruction(
                fixture.Report.Id,
                fixture.Transmission.Id,
                fixture.Organization.Id,
                fixture.Employer.Id,
                "success",
                fixture.Transmission.PayloadFileName,
                DateTimeOffset.UtcNow,
                TransferOutcomes:
                [
                    new(fixture.TransferA, "success"),
                    new(fixture.TransferB, "error", 53),
                    new(fixture.TransferC, "partial", 116)
                ]);

            var feedbackId = Assert.IsType<Guid>(await ingestor.IngestAsync(
                fixture.Employer.Id, instruction, "mixed-manufacturers.simulation.json", ct));

            var transfers = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
                .Where(x => x.FeedbackId == feedbackId)
                .ToDictionaryAsync(x => x.TransferIdentifier, StringComparer.OrdinalIgnoreCase, ct);
            Assert.Equal(3, transfers.Count);
            Assert.Equal("allocated", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[fixture.TransferA].ReportedDepositAmount,
                transfers[fixture.TransferA].ActualReceivedAmount,
                transfers[fixture.TransferA].AllocatedAmount,
                transfers[fixture.TransferA].InTransitAmount));
            Assert.Equal("unresolved", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[fixture.TransferB].ReportedDepositAmount,
                transfers[fixture.TransferB].ActualReceivedAmount,
                transfers[fixture.TransferB].AllocatedAmount,
                transfers[fixture.TransferB].InTransitAmount));
            Assert.Equal("in-transit", ReportFeedbackStatusResolver.ResolveMoneyState(
                transfers[fixture.TransferC].ReportedDepositAmount,
                transfers[fixture.TransferC].ActualReceivedAmount,
                transfers[fixture.TransferC].AllocatedAmount,
                transfers[fixture.TransferC].InTransitAmount));

            var rows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
                .Where(x => x.FeedbackId == feedbackId)
                .ToListAsync(ct);

            Assert.Equal(5, rows.Count);

            var byProduct = rows.GroupBy(x => x.ReportProductId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorCode).ToArray());

            Assert.All(fixture.TransferAProducts, productId =>
                Assert.All(byProduct[productId], code => Assert.Equal(1, code)));
            Assert.All(fixture.TransferBProducts, productId =>
                Assert.All(byProduct[productId], code => Assert.Equal(53, code)));
            Assert.All(fixture.TransferCProducts, productId =>
                Assert.All(byProduct[productId], code => Assert.Equal(116, code)));

            Assert.All(rows.Where(x => fixture.TransferAProducts.Contains(x.ReportProductId)), row =>
            {
                Assert.Equal("אין שגיאה", row.ErrorDescription);
                Assert.Equal(1, row.IntakeStatus);
            });
            Assert.All(rows.Where(x => fixture.TransferBProducts.Contains(x.ReportProductId)), row =>
            {
                Assert.Equal(EmployerInterfaceLineFeedbackParser.Description(53), row.ErrorDescription);
                Assert.Equal(2, row.IntakeStatus);
                Assert.NotNull(row.ErrorAmount);
            });
            Assert.All(rows.Where(x => fixture.TransferCProducts.Contains(x.ReportProductId)), row =>
            {
                Assert.Equal(EmployerInterfaceLineFeedbackParser.Description(116), row.ErrorDescription);
                Assert.Equal(2, row.IntakeStatus);
                Assert.NotNull(row.ErrorAmount);
            });

            Assert.Equal(
                fixture.TransferAProducts.Sum(id => 600m + fixture.ProductIndex[id]),
                transfers[fixture.TransferA].ReportedDepositAmount);
            Assert.Equal(transfers[fixture.TransferA].ReportedDepositAmount, transfers[fixture.TransferA].AllocatedAmount);
            Assert.Equal(0m, transfers[fixture.TransferA].InTransitAmount);

            Assert.Equal(
                fixture.TransferBProducts.Sum(id => 600m + fixture.ProductIndex[id]),
                transfers[fixture.TransferB].ReportedDepositAmount);
            Assert.Equal(0m, transfers[fixture.TransferB].ActualReceivedAmount);
            Assert.Equal(0m, transfers[fixture.TransferB].AllocatedAmount);

            Assert.Equal(
                fixture.TransferCProducts.Sum(id => 600m + fixture.ProductIndex[id]),
                transfers[fixture.TransferC].ReportedDepositAmount);
            Assert.Equal(transfers[fixture.TransferC].ReportedDepositAmount, transfers[fixture.TransferC].ActualReceivedAmount);
            Assert.True(transfers[fixture.TransferC].AllocatedAmount > 0m);
            Assert.True(transfers[fixture.TransferC].InTransitAmount > 0m);

            var errorCount = rows.Count(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode));
            var state = ReportFeedbackStatusResolver.ResolveReportState(
                ReportTransmissionStatus.Accepted,
                officialFeedbackCount: 1,
                expectedContributionCount: 5,
                receivedContributionCount: rows.Count,
                errorContributionCount: errorCount);

            Assert.Equal("attention", state);
        });
    }

    [Fact]
    public async Task Same_manufacturer_can_have_multiple_employees_and_keep_each_product_result_scoped()
    {
        await WithDatabase(async (db, ct) =>
        {
            var fixture = await CreateMultiManufacturerReportFixtureAsync(db, ct);
            var ingestor = NewIngestor(db);

            var instruction = new SimulatedVaultFeedbackInstruction(
                fixture.Report.Id,
                fixture.Transmission.Id,
                fixture.Organization.Id,
                fixture.Employer.Id,
                "success",
                fixture.Transmission.PayloadFileName,
                DateTimeOffset.UtcNow,
                TransferOutcomes:
                [
                    new(fixture.TransferA, "error", 4),
                    new(fixture.TransferB, "success"),
                    new(fixture.TransferC, "success")
                ]);

            var feedbackId = Assert.IsType<Guid>(await ingestor.IngestAsync(
                fixture.Employer.Id, instruction, "same-manufacturer-multi-employee.simulation.json", ct));

            var rows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
                .Where(x => x.FeedbackId == feedbackId)
                .ToListAsync(ct);

            Assert.Equal(2, fixture.TransferAProducts.Count);
            Assert.All(fixture.TransferAProducts, productId =>
                Assert.All(rows.Where(x => x.ReportProductId == productId),
                    row => Assert.Equal(4, row.ErrorCode)));

            Assert.All(fixture.TransferBProducts.Concat(fixture.TransferCProducts), productId =>
                Assert.All(rows.Where(x => x.ReportProductId == productId),
                    row => Assert.Equal(1, row.ErrorCode)));
        });
    }

    [Fact]
    public async Task Fedbka_technical_rejection_marks_transmission_and_report_as_error()
    {
        await WithDatabase(async (db, ct) =>
        {
            var fixture = await CreateReportFixtureAsync(db, ct);
            var handler = new SimulatedClearinghouseTechnicalFeedbackHandler(db);
            var instruction = new SimulatedVaultFeedbackInstruction(
                fixture.Report.Id,
                fixture.Transmission.Id,
                fixture.Organization.Id,
                fixture.Employer.Id,
                "error",
                fixture.Transmission.PayloadFileName,
                DateTimeOffset.UtcNow,
                3,
                ClearinghouseInitialFeedbackCatalog.TechnicalInterface,
                ClearinghouseInitialFeedbackCatalog.StageADescription(3));

            Assert.True(await handler.HandleAsync(fixture.Employer.Id, instruction, ct));

            var transmission = await db.ReportTransmissions.AsNoTracking()
                .SingleAsync(x => x.Id == fixture.Transmission.Id, ct);
            var report = await db.ManualReports.AsNoTracking()
                .SingleAsync(x => x.Id == fixture.Report.Id, ct);

            Assert.Equal(ReportTransmissionStatus.Rejected, transmission.Status);
            Assert.Contains("FEDBKA 3", transmission.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(ManualReportStatus.Error, report.Status);
            Assert.Contains("FEDBKA 3", report.ValidationError, StringComparison.Ordinal);
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

    private static async Task<MultiManufacturerFixture> CreateMultiManufacturerReportFixtureAsync(
        AlphaDbContext db,
        CancellationToken ct)
    {
        var organization = new Organization("Demo Multi Manufacturer", OrganizationType.PayrollOffice);
        var employer = new Employer(organization.Id, "Demo Employer", "512345678", "987654321",
            "Test", "Contact", "031234567", "test@example.test", "0501234567");
        var report = new ManualReport(organization.Id, employer.Id,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        report.MarkReadyForValidation();
        report.MarkValidated();
        report.MarkTransmissionStarted();
        report.MarkSent();

        db.AddRange(organization, employer, report);

        var transferA = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var transferB = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var transferC = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var aProducts = new List<Guid>();
        var bProducts = new List<Guid>();
        var cProducts = new List<Guid>();
        var productIndex = new Dictionary<Guid, int>();

        void AddEmployee(
            int index,
            string fundKey,
            string fundCode,
            string transferId,
            List<Guid> bucket)
        {
            var nationalId = $"1234567{index}2";
            var person = new Person(organization.Id, nationalId, $"Employee{index}", "Test",
                new DateOnly(1990, 1, Math.Min(index, 28)), PersonGender.Female,
                $"employee{index}@example.test", $"05011111{index:00}",
                "תל אביב", "הרצל", index.ToString(), "1", "6100000", null);
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2025, 1, 1), $"E-{index}", 10_000m + index * 100m);
            var reportEmployee = new ManualReportEmployee(report.Id, organization.Id, employer.Id,
                employment.Id, person.Id, nationalId, $"Employee{index}", "Test", $"E-{index}", 10_000m + index * 100m);
            var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund,
                $"POL-{index}", new DateOnly(2026, 9, 1), 10_000m + index * 100m, "שוטף", "", false, null,
                fundExternalKey: fundKey, fundCode: fundCode, fundName: $"Fund {fundCode}");
            var contribution = new ManualContribution(product.Id, ContributionParty.Employee,
                ContributionComponent.Benefits, 600m + index, 6m, 0m);
            contribution.SetInterfaceRecordIdentifier(Guid.NewGuid().ToString("D"));
            var metadata = new EmployerInterfaceReportProductData(product.Id);
            metadata.SetInterfaceTransferIdentifier(transferId);

            db.AddRange(person, employment, reportEmployee, product, contribution, metadata);
            bucket.Add(product.Id);
            productIndex[product.Id] = index;
        }

        AddEmployee(1, "manufacturer-a", "1001", transferA, aProducts);
        AddEmployee(2, "manufacturer-a", "1001", transferA, aProducts);
        AddEmployee(3, "manufacturer-b", "2002", transferB, bProducts);
        AddEmployee(4, "manufacturer-c", "3003", transferC, cProducts);
        AddEmployee(5, "manufacturer-c", "3003", transferC, cProducts);

        var transmission = new ReportTransmission(report.Id, organization.Id, employer.Id, "SimulatedVault", 1);
        transmission.Start("demo-multi-hash",
            "006000123456789EMPONG000006202610040900000001.TST",
            "<demo />"u8.ToArray());
        transmission.Complete(ReportTransmissionStatus.Accepted, "SIM-MULTI", "queued", null);
        db.Add(transmission);

        await db.SaveChangesAsync(ct);

        return new MultiManufacturerFixture(
            organization, employer, report, transmission,
            transferA, transferB, transferC,
            aProducts, bProducts, cProducts,
            productIndex);
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

    private sealed record MultiManufacturerFixture(
        Organization Organization,
        Employer Employer,
        ManualReport Report,
        ReportTransmission Transmission,
        string TransferA,
        string TransferB,
        string TransferC,
        IReadOnlyList<Guid> TransferAProducts,
        IReadOnlyList<Guid> TransferBProducts,
        IReadOnlyList<Guid> TransferCProducts,
        IReadOnlyDictionary<Guid, int> ProductIndex);

    private sealed record Fixture(
        Organization Organization,
        Employer Employer,
        ManualReport Report,
        ReportTransmission Transmission);
}
