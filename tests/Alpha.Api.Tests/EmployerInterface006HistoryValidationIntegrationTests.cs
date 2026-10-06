using System.Collections;
using System.Reflection;
using Alpha.Api.Endpoints;
using Alpha.Application.Abstractions;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterface006HistoryValidationIntegrationTests
{
    [Fact]
    public async Task Ordinary_report_detects_historical_errors_28_43_and_50()
    {
        await WithDatabase(async (db, ct) =>
        {
            var graph = await CreateBaseGraphAsync(db, ct);
            var transferId = Guid.NewGuid().ToString("D").ToUpperInvariant();

            var historical = Report(graph.Organization.Id, graph.Employer.Id);
            var historicalEmployee = ReportEmployee(historical.Id, graph, "123456782");
            var historicalProduct = Product(historicalEmployee.Id, "P-1");
            var historicalContribution = Contribution(historicalProduct.Id);
            var historicalMetadata = Metadata(historicalProduct.Id, 1, transferId);
            historical.MarkReadyForValidation();
            historical.MarkValidated();
            historical.MarkTransmissionStarted();
            historical.MarkSent();

            var current = Report(graph.Organization.Id, graph.Employer.Id);
            var currentEmployee = ReportEmployee(current.Id, graph, "123456782");
            var currentProduct = Product(currentEmployee.Id, "P-1");
            var currentContribution = Contribution(currentProduct.Id);
            var currentMetadata = Metadata(currentProduct.Id, 1, transferId);

            db.AddRange(historical, historicalEmployee, historicalProduct, historicalContribution, historicalMetadata,
                current, currentEmployee, currentProduct, currentContribution, currentMetadata);
            await db.SaveChangesAsync(ct);

            var codes = await InvokeHistoryValidationAsync(current, [currentEmployee], [currentProduct],
                [currentContribution], db, ct);

            Assert.Contains("SUG_SHGIHA_28", codes);
            Assert.Contains("SUG_SHGIHA_43", codes);
            Assert.Contains("SUG_SHGIHA_50", codes);
        });
    }

    [Fact]
    public async Task Same_fund_and_month_twice_in_one_report_is_errors_28_and_43_even_without_policy_number()
    {
        await WithDatabase(async (db, ct) =>
        {
            var graph = await CreateBaseGraphAsync(db, ct);
            var report = Report(graph.Organization.Id, graph.Employer.Id);
            var employee = ReportEmployee(report.Id, graph, "123456782");
            var first = Product(employee.Id, "", "same-fund");
            var second = Product(employee.Id, "", "same-fund");
            var firstContribution = Contribution(first.Id);
            var secondContribution = Contribution(second.Id);
            db.AddRange(report, employee, first, second, firstContribution, secondContribution);
            await db.SaveChangesAsync(ct);

            var codes = await InvokeHistoryValidationAsync(report, [employee], [first, second],
                [firstContribution, secondContribution], db, ct);
            Assert.Contains("SUG_SHGIHA_28", codes);
            Assert.Contains("SUG_SHGIHA_43", codes);
        });
    }

    [Fact]
    public async Task Current_operation_2_without_matching_sent_negative_is_error_100()
    {
        await WithDatabase(async (db, ct) =>
        {
            var graph = await CreateBaseGraphAsync(db, ct);
            var report = Report(graph.Organization.Id, graph.Employer.Id);
            var employee = ReportEmployee(report.Id, graph, "123456782");
            var product = Product(employee.Id, "P-2");
            var contribution = Contribution(product.Id);
            var metadata = Metadata(product.Id, 2, Guid.NewGuid().ToString("D").ToUpperInvariant());

            db.AddRange(report, employee, product, contribution, metadata);
            await db.SaveChangesAsync(ct);

            var codes = await InvokeHistoryValidationAsync(report, [employee], [product], [contribution], db, ct);
            Assert.Contains("SUG_SHGIHA_100", codes);
        });
    }

    [Fact]
    public async Task Technical_negative_change_without_current_pair_is_error_101_but_removed_product_is_not()
    {
        await WithDatabase(async (db, ct) =>
        {
            var graph = await CreateBaseGraphAsync(db, ct);

            var source = Report(graph.Organization.Id, graph.Employer.Id);
            var sourceEmployee = ReportEmployee(source.Id, graph, "123456782");
            var sourceProduct = Product(sourceEmployee.Id, "P-3");
            source.MarkReadyForValidation();
            source.MarkValidated();
            source.MarkTransmissionStarted();
            source.MarkSent();

            var workspace = new ManualReport(graph.Organization.Id, graph.Employer.Id,
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 9), ManualReportKind.Differences, source.Id);
            workspace.MarkCorrectionWorkspace(source.Id, 2);
            var workspaceEmployee = ReportEmployee(workspace.Id, graph, "123456782");
            var workspaceProduct = Product(workspaceEmployee.Id, "P-3");
            workspaceProduct.SetSourceVersion(sourceProduct.Id);

            var negative = new ManualReport(graph.Organization.Id, graph.Employer.Id,
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 9), ManualReportKind.Negative, source.Id);
            negative.MarkTechnicalCorrectionDocument(workspace.Id, source.Id, 2);
            var negativeEmployee = ReportEmployee(negative.Id, graph, "123456782");
            var negativeProduct = Product(negativeEmployee.Id, "P-3");
            negativeProduct.SetSourceVersion(sourceProduct.Id);
            var negativeContribution = Contribution(negativeProduct.Id);
            var negativeMetadata = Metadata(negativeProduct.Id, 6, Guid.NewGuid().ToString("D").ToUpperInvariant());

            db.AddRange(source, sourceEmployee, sourceProduct,
                workspace, workspaceEmployee, workspaceProduct,
                negative, negativeEmployee, negativeProduct, negativeContribution, negativeMetadata);
            await db.SaveChangesAsync(ct);

            var changedCodes = await InvokeHistoryValidationAsync(negative, [negativeEmployee], [negativeProduct],
                [negativeContribution], db, ct);
            Assert.Contains("SUG_SHGIHA_101", changedCodes);

            db.ManualReportProducts.Remove(workspaceProduct);
            await db.SaveChangesAsync(ct);

            var removedCodes = await InvokeHistoryValidationAsync(negative, [negativeEmployee], [negativeProduct],
                [negativeContribution], db, ct);
            Assert.DoesNotContain("SUG_SHGIHA_101", removedCodes);
        });
    }

    private static async Task<string[]> InvokeHistoryValidationAsync(
        ManualReport report,
        IReadOnlyCollection<ManualReportEmployee> employees,
        IReadOnlyCollection<ManualReportProduct> products,
        IReadOnlyCollection<ManualContribution> contributions,
        IAlphaDbContext db,
        CancellationToken ct)
    {
        var method = typeof(ReportValidationEndpoints).GetMethod(
            "AppendPreventableHistoryAndCorrectionIssuesAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Validation method not found.");
        var issueType = typeof(ReportValidationEndpoints).GetNestedType(
            "ValidationIssue", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Validation issue type not found.");
        var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(issueType))
            ?? throw new InvalidOperationException("Could not create validation issue list.");

        var task = (Task?)method.Invoke(null, [report, employees, products, contributions, db, list, ct])
            ?? throw new InvalidOperationException("Validation invocation failed.");
        await task;

        var codeProperty = issueType.GetProperty("Code")
            ?? throw new InvalidOperationException("Validation issue code property not found.");
        return ((IEnumerable)list).Cast<object>()
            .Select(item => (string?)codeProperty.GetValue(item))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToArray();
    }

    private static ManualReport Report(Guid organizationId, Guid employerId) =>
        new(organizationId, employerId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 9));

    private static ManualReportEmployee ReportEmployee(Guid reportId, BaseGraph graph, string identifier) =>
        new(reportId, graph.Organization.Id, graph.Employer.Id, graph.Employment.Id, graph.Person.Id,
            identifier, "Audit", "Employee", "E-1", 1000m);

    private static ManualReportProduct Product(Guid employeeId, string policy, string externalKey = "fund-key") =>
        new(employeeId, PensionProductType.PensionFund, policy, new DateOnly(2026, 9, 1), 1000m,
            "1", "1", false, null, externalKey, "111", "Fund", "Company",
            SalaryAllocationType.Fixed, 1000m, 0, 3, "חדשה");

    private static ManualContribution Contribution(Guid productId) =>
        new(productId, ContributionParty.Employee, ContributionComponent.Benefits, 60m, 6m, 0m);

    private static EmployerInterfaceReportProductData Metadata(Guid productId, int operationCode, string transferId)
    {
        var metadata = new EmployerInterfaceReportProductData(productId);
        metadata.Update(operationCode, 1, 1, new DateOnly(2026, 9, 1), null, null, 2, null,
            operationCode == 6 ? null : 1, 1, 1);
        metadata.SetInterfaceTransferIdentifier(transferId);
        return metadata;
    }

    private static async Task<BaseGraph> CreateBaseGraphAsync(AlphaDbContext db, CancellationToken ct)
    {
        var organization = new Organization("V006 history audit", OrganizationType.PayrollOffice);
        var employer = new Employer(organization.Id, "Audit Employer", "123456789", "987654321",
            "Audit", "Contact", "031234567", "audit@example.test", "0501234567");
        var person = new Person(organization.Id, "", "Audit", "Employee");
        person.SetProtectedIdentifier(PersonIdentifierType.IsraeliId, "encrypted-id", "lookup-hash");
        var employment = new Employment(organization.Id, employer.Id, person.Id,
            new DateOnly(2025, 1, 1), "E-1", 1000m);
        db.AddRange(organization, employer, person, employment);
        await db.SaveChangesAsync(ct);
        return new BaseGraph(organization, employer, person, employment);
    }

    private static async Task WithDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_v006_history_" + Guid.NewGuid().ToString("N");
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

    private sealed record BaseGraph(Organization Organization, Employer Employer, Person Person, Employment Employment);
}
