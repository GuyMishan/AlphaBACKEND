using Alpha.Api.Services;
using Alpha.Application.Billing;
using Alpha.Application.Entitlements;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerTransferIntegrationTests
{
    [Fact]
    public async Task Transfers_employer_and_reports_without_stealing_shared_employee()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var target = new Organization("Target", OrganizationType.PayrollOffice);
            var moved = new Employer(source.Id, "Moved", "reg-moved", "file-moved");
            var remaining = new Employer(source.Id, "Remaining", "reg-remain", "file-remain");
            var shared = new Person(source.Id, "", "Test", "Employee");
            shared.SetProtectedIdentifier(PersonIdentifierType.IsraeliId, "encrypted-dummy", "hash-123");
            var movedEmployment = new Employment(source.Id, moved.Id, shared.Id,
                new DateOnly(2026, 1, 1), "mov-1", 5000);
            var remainingEmployment = new Employment(source.Id, remaining.Id, shared.Id,
                new DateOnly(2026, 1, 1), "remain-1", 5000);
            var report = new ManualReport(source.Id, moved.Id, new DateOnly(2026, 8, 1), null);
            var reportEmployee = new ManualReportEmployee(report.Id, source.Id, moved.Id,
                movedEmployment.Id, shared.Id, "dummy", "Test", "Employee", "mov-1");

            db.AddRange(source, target, moved, remaining, shared, movedEmployment,
                remainingEmployment, report, reportEmployee,
                new EmployerProfileSettings(moved.Id), new EmployerProfileSettings(remaining.Id));
            await db.SaveChangesAsync(ct);

            var result = await NewService(db).TransferAsync(
                source.Id, moved.Id, target.Id, Guid.NewGuid(), "integration-test", ct);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.Equal(target.Id, (await db.Employers.SingleAsync(x => x.Id == moved.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.Employers.SingleAsync(x => x.Id == remaining.Id, ct)).OrganizationId);
            var movedAfter = await db.Employments.SingleAsync(x => x.Id == movedEmployment.Id, ct);
            var remainingAfter = await db.Employments.SingleAsync(x => x.Id == remainingEmployment.Id, ct);
            Assert.Equal(target.Id, movedAfter.OrganizationId);
            Assert.NotEqual(shared.Id, movedAfter.PersonId);
            Assert.Equal(shared.Id, remainingAfter.PersonId);
            Assert.Equal(source.Id, (await db.People.SingleAsync(x => x.Id == shared.Id, ct)).OrganizationId);
            Assert.Equal(target.Id, (await db.People.SingleAsync(x => x.Id == movedAfter.PersonId, ct)).OrganizationId);
            Assert.Equal("hash-123", (await db.People.SingleAsync(x => x.Id == movedAfter.PersonId, ct)).NationalIdLookupHash);
            Assert.Equal(target.Id, (await db.ManualReports.SingleAsync(x => x.Id == report.Id, ct)).OrganizationId);
            var reportAfter = await db.ManualReportEmployees.SingleAsync(x => x.Id == reportEmployee.Id, ct);
            Assert.Equal(target.Id, reportAfter.OrganizationId);
            Assert.Equal(movedAfter.PersonId, reportAfter.PersonId);
        });
    }

    [Fact]
    public async Task Duplicate_identity_blocks_transfer_without_partial_changes()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var target = new Organization("Target", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved", "reg-unique", "file-unique");
            var sourcePerson = new Person(source.Id, "", "One", "Employee");
            sourcePerson.SetProtectedIdentifier(PersonIdentifierType.Passport, "encrypted-source", "shared-hash");
            var targetPerson = new Person(target.Id, "", "Two", "Employee");
            targetPerson.SetProtectedIdentifier(PersonIdentifierType.Passport, "encrypted-target", "shared-hash");
            var employment = new Employment(source.Id, employer.Id, sourcePerson.Id,
                new DateOnly(2026, 1, 1), "mov-1", 5000);
            db.AddRange(source, target, employer, sourcePerson, targetPerson, employment,
                new EmployerProfileSettings(employer.Id));
            await db.SaveChangesAsync(ct);

            var result = await NewService(db).TransferAsync(
                source.Id, employer.Id, target.Id, Guid.NewGuid(), "integration-test", ct);

            Assert.False(result.Success);
            Assert.Equal("duplicate_identity", result.Error);
            db.ChangeTracker.Clear();
            Assert.Equal(source.Id, (await db.Employers.SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.Employments.SingleAsync(x => x.Id == employment.Id, ct)).OrganizationId);
        });
    }

    private static EmployerTransferService NewService(AlphaDbContext db) =>
        new(db, new EntitlementService(db), new BillingInheritanceService(db),
            new OrganizationEntitlementLock(db));

    // Isolated databases ensure these integration tests never modify production
    // and do not race with the other PostgreSQL-backed tests in CI.
    private static async Task InIsolatedDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_transfer_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = database,
            Pooling = false
        };
        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(connection.ConnectionString).Options;
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
}
