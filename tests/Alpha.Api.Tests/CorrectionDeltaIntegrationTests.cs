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

public sealed class CorrectionDeltaIntegrationTests
{
    [Fact]
    public async Task New_report_revision_materializes_only_added_changed_removed_delta_and_becomes_revision_2()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_correction_delta_" + Guid.NewGuid().ToString("N");
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
            var protector = CreateProtector();

            var organization = new Organization("Delta Test", OrganizationType.PayrollOffice);
            var employer = new Employer(organization.Id, "Delta Employer", "123456789", "987654321",
                "Test", "Contact", "031234567", "employer@example.test", "0501234567");
            var person = new Person(organization.Id, "", "Delta", "Employee");
            person.SetProtectedIdentifier(PersonIdentifierType.IsraeliId, "encrypted-master", "master-hash");
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2025, 1, 1), "E-1", 10000m);

            var source = new ManualReport(organization.Id, employer.Id, new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 9));
            source.SetEmployerInterfaceSnapshot(employer.LegalName, employer.RegistrationNumber,
                employer.WithholdingFileNumber, employer.ContactFirstName, employer.ContactLastName,
                employer.ContactPhone, employer.ContactEmail, employer.ContactMobile, 1, 1);
            source.SetProtectedEmployerSnapshot(
                protector.Protect(employer.RegistrationNumber, $"report-employer-registration:{source.Id}"),
                protector.Protect(employer.WithholdingFileNumber, $"report-employer-withholding:{source.Id}"),
                protector.Protect(employer.ContactPhone, $"report-employer-phone:{source.Id}"),
                protector.Protect(employer.ContactEmail, $"report-employer-email:{source.Id}"),
                protector.Protect(employer.ContactMobile, $"report-employer-mobile:{source.Id}"));

            var reportEmployee = new ManualReportEmployee(source.Id, organization.Id, employer.Id,
                employment.Id, person.Id, "123456789", person.FirstName, person.LastName,
                employment.EmployeeNumber, employment.MonthlySalary);
            reportEmployee.SetInterfaceSnapshot(1, "123456789", new DateOnly(1990, 1, 1), 1,
                "employee@example.test", "0507654321", "Rehovot", "Herzl", "10", "2", "7610001", "",
                employment.StartDate);
            reportEmployee.SetProtectedIdentifiers(
                protector.Protect("123456789", $"report-employee-national-id:{reportEmployee.Id}"),
                protector.LookupHash("123456789", "report-employee-national-id-lookup"),
                protector.Protect("123456789", $"report-employee-interface-id:{reportEmployee.Id}"));
            reportEmployee.SetProtectedContactSnapshot(
                protector.Protect("employee@example.test", $"report-employee-email:{reportEmployee.Id}"),
                protector.Protect("0507654321", $"report-employee-mobile:{reportEmployee.Id}"));

            var p1 = Product(reportEmployee.Id, "P1", "fund-1", new string('1', 30), 0);
            var p2 = Product(reportEmployee.Id, "P2", "fund-2", new string('2', 30), 1);
            var p3 = Product(reportEmployee.Id, "P3", "fund-3", new string('3', 30), 2);

            db.AddRange(organization, employer, person, employment, source, reportEmployee, p1, p2, p3);
            foreach (var product in new[] { p1, p2, p3 })
            {
                var contribution = new ManualContribution(product.Id, ContributionParty.Employee,
                    ContributionComponent.Benefits, 100m, 10m, 0m);
                contribution.SetInterfaceRecordIdentifier(Guid.NewGuid().ToString("D"));
                var payment = Payment(product.Id, protector);
                var metadata = Metadata(product.Id, 1);
                metadata.SetInterfaceTransferIdentifier(Guid.NewGuid().ToString("D"));
                db.AddRange(contribution, payment, metadata);
            }

            source.MarkReadyForValidation();
            source.MarkValidated();
            source.MarkTransmissionStarted();
            source.MarkSent();
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            var workspaceResult = await CorrectionWorkflowService.EnsureWorkspaceAsync(
                organization.Id, employer.Id, source.Id, null, db, protector, ct);
            Assert.NotNull(workspaceResult);
            var workspaceId = workspaceResult!.ReportId;

            var workspaceEmployee = await db.ManualReportEmployees.SingleAsync(x => x.ReportId == workspaceId, ct);
            var workspaceProducts = await db.ManualReportProducts
                .Where(x => x.ReportEmployeeId == workspaceEmployee.Id).ToListAsync(ct);
            var changed = workspaceProducts.Single(x => x.SourceReportProductId == p2.Id);
            var removed = workspaceProducts.Single(x => x.SourceReportProductId == p3.Id);
            var changedContribution = await db.ManualContributions.SingleAsync(x =>
                x.ReportProductId == changed.Id && x.Party == ContributionParty.Employee
                && x.Component == ContributionComponent.Benefits, ct);
            changedContribution.Update(125m, 12.5m, 0m);
            changed.MarkCorrectionChanged(2);
            db.ManualReportProducts.Remove(removed);

            var added = Product(workspaceEmployee.Id, "P4", "fund-4", new string('4', 30), 3);
            var addedContribution = new ManualContribution(added.Id, ContributionParty.Employee,
                ContributionComponent.Benefits, 80m, 8m, 0m);
            var addedPayment = Payment(added.Id, protector);
            var addedMetadata = Metadata(added.Id, 1);
            db.AddRange(added, addedContribution, addedPayment, addedMetadata);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            var materialized = await CorrectionWorkflowService.MaterializeAsync(
                organization.Id, employer.Id, workspaceId, db, protector, ct);
            Assert.NotNull(materialized);
            Assert.Equal(2, materialized!.RevisionNumber);
            Assert.NotNull(materialized.NegativeReportId);
            Assert.NotNull(materialized.CurrentReportId);
            Assert.Equal(3, materialized.PendingChanges);

            var negativeEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.ReportId == materialized.NegativeReportId).Select(x => x.Id).ToArrayAsync(ct);
            var negativeProducts = await db.ManualReportProducts.AsNoTracking()
                .Where(x => negativeEmployeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
            Assert.Equal(2, negativeProducts.Count);
            Assert.Equal([p2.Id, p3.Id], negativeProducts.Select(x => x.SourceReportProductId!.Value).Order().ToArray());

            var currentEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.ReportId == materialized.CurrentReportId).Select(x => x.Id).ToArrayAsync(ct);
            var currentProducts = await db.ManualReportProducts.AsNoTracking()
                .Where(x => currentEmployeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
            Assert.Equal(2, currentProducts.Count);
            var currentMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
                .Where(x => currentProducts.Select(p => p.Id).Contains(x.ReportProductId))
                .Select(x => x.OperationCode).Order().ToArrayAsync(ct);
            Assert.Equal([1, 2], currentMetadata);

            var negativeMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
                .Where(x => negativeProducts.Select(p => p.Id).Contains(x.ReportProductId))
                .Select(x => x.OperationCode).ToArrayAsync(ct);
            Assert.All(negativeMetadata, operation => Assert.Equal(6, operation));

            var technicalReports = await db.ManualReports
                .Where(x => x.CorrectionWorkspaceId == workspaceId && x.IsTechnicalCorrectionDocument)
                .ToListAsync(ct);
            Assert.Equal(2, technicalReports.Count);
            foreach (var report in technicalReports)
            {
                report.MarkReadyForValidation();
                report.MarkValidated();
                report.MarkTransmissionStarted();
                report.MarkSent();
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            Assert.True(await CorrectionWorkflowService.FinalizeRevisionIfCompleteAsync(
                materialized.CurrentReportId!.Value, db, ct));
            db.ChangeTracker.Clear();

            var revision2 = await db.ManualReports.AsNoTracking().SingleAsync(x => x.Id == workspaceId, ct);
            Assert.True(revision2.IsRevisionSnapshot);
            Assert.False(revision2.IsCorrectionWorkspace);
            Assert.Equal(2, revision2.RevisionNumber);
            Assert.Equal(ManualReportStatus.Completed, revision2.Status);

            Assert.Null(await CorrectionWorkflowService.EnsureWorkspaceAsync(
                organization.Id, employer.Id, source.Id, null, db, protector, ct));
            var revision3Workspace = await CorrectionWorkflowService.EnsureWorkspaceAsync(
                organization.Id, employer.Id, revision2.Id, null, db, protector, ct);
            Assert.NotNull(revision3Workspace);
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

    private static ManualReportProduct Product(Guid employeeId, string policy, string externalKey, string fundCode, int order) =>
        new(employeeId, PensionProductType.PensionFund, policy, new DateOnly(2026, 9, 1), 1000m,
            "1", "1", false, null, externalKey, fundCode, $"Fund {policy}", $"Company {policy}",
            SalaryAllocationType.Fixed, 1000m, order, 3);

    private static ManualReportPayment Payment(Guid productId, IDataProtectionService protector)
    {
        var payment = new ManualReportPayment(productId);
        payment.Update("Fund", "10 - 123 - 987654", "העברה בנקאית", new DateOnly(2026, 9, 10), null,
            "REF-1", "Bank", "10", "123",
            protector.Protect("123456", $"report-payment-account:{productId}"), "");
        return payment;
    }

    private static EmployerInterfaceReportProductData Metadata(Guid productId, int operation)
    {
        var metadata = new EmployerInterfaceReportProductData(productId);
        metadata.Update(operation, 1, 1, new DateOnly(2026, 9, 1), null, null, 2, null,
            1, 1, 1);
        return metadata;
    }

    private static IDataProtectionService CreateProtector()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:DataProtectionKey"] = key })
            .Build();
        return new AesDataProtectionService(configuration);
    }
}
