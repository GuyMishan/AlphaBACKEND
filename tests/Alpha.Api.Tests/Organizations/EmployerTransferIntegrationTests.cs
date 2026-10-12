using Alpha.Modules.Billing.Contracts;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Modules.Organizations.Features.EmployerTransfers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Organizations;

public sealed class EmployerTransferIntegrationTests
{
    [Fact]
    public async Task Application_boundary_rejects_non_platform_admin_without_database_changes()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var target = new Organization("Target", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved", "reg-forbidden", "file-forbidden");
            db.AddRange(source, target, employer, new EmployerProfileSettings(employer.Id));
            await db.SaveChangesAsync(ct);

            var actor = Guid.NewGuid();
            var result = await NewService(db, new FakeCurrentUser(actor, false)).TransferAsync(
                source.Id, employer.Id, target.Id, "authorization-integration-test", ct);

            Assert.False(result.Success);
            Assert.Equal("forbidden", result.Error);
            db.ChangeTracker.Clear();
            Assert.Equal(source.Id, (await db.Employers.SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            Assert.False(await db.AuditEvents.AnyAsync(x => x.EntityId == employer.Id, ct));
        });
    }

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
            var paymentAccount = new EmployerPaymentAccount(source.Id, moved.Id, 12, 345,
                "123456", "Moved HTTP", "123456789");
            paymentAccount.SetProtectedValues("encrypted-account", "account-hash", "encrypted-holder");
            var feedback = new EmployerInterfaceFeedback(source.Id, moved.Id,
                EmployerInterfaceDocumentType.SummaryFeedback, "006", "feedback.xml",
                "feedback-hash", "<feedback />");
            feedback.Correlate(report.Id, null);
            var employerBilling = new BillingAccount(null, moved.Id);
            var invitationCreator = new User("invite-creator", "creator@example.test", "Creator");
            var invitation = new UserInvitation("pending@example.test", source.Id, moved.Id,
                null, EmployerRole.User, "invite-hash", DateTimeOffset.UtcNow.AddDays(1), invitationCreator.Id);

            db.AddRange(source, target, moved, remaining, shared, movedEmployment,
                remainingEmployment, report, reportEmployee, paymentAccount, feedback,
                employerBilling, invitationCreator, invitation,
                new EmployerProfileSettings(moved.Id), new EmployerProfileSettings(remaining.Id));
            await db.SaveChangesAsync(ct);

            var actorId = Guid.NewGuid();
            var result = await NewService(db, new FakeCurrentUser(actorId, true)).TransferAsync(
                source.Id, moved.Id, target.Id, "integration-test", ct);

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
            Assert.Equal(target.Id, (await db.EmployerPaymentAccounts.SingleAsync(x =>
                x.Id == paymentAccount.Id, ct)).OrganizationId);
            Assert.Equal(target.Id, (await db.EmployerInterfaceFeedback.SingleAsync(x =>
                x.Id == feedback.Id, ct)).OrganizationId);
            var billingAfter = await db.BillingAccounts.SingleAsync(x => x.Id == employerBilling.Id, ct);
            Assert.Null(billingAfter.OrganizationId);
            Assert.Equal(moved.Id, billingAfter.EmployerId);
            var invitationAfter = await db.UserInvitations.SingleAsync(x => x.Id == invitation.Id, ct);
            Assert.Equal(source.Id, invitationAfter.OrganizationId);
            Assert.Equal(UserInvitationStatus.Cancelled, invitationAfter.Status);
            var audit = await db.AuditEvents.SingleAsync(x =>
                x.EntityId == moved.Id && x.Action == "employer.transferred", ct);
            Assert.Equal(actorId, audit.ActorUserId);
            Assert.Equal("integration-test", audit.CorrelationId);
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
                source.Id, employer.Id, target.Id, "integration-test", ct);

            Assert.False(result.Success);
            Assert.Equal("duplicate_identity", result.Error);
            db.ChangeTracker.Clear();
            Assert.Equal(source.Id, (await db.Employers.SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.Employments.SingleAsync(x => x.Id == employment.Id, ct)).OrganizationId);
        });
    }

    [Fact]
    public async Task Pending_transmission_blocks_transfer_without_partial_changes()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var target = new Organization("Target", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved", "reg-pending", "file-pending");
            var report = new ManualReport(source.Id, employer.Id, new DateOnly(2026, 8, 1), null);
            var transmission = new ReportTransmission(report.Id, source.Id, employer.Id, "test-provider", 1);
            db.AddRange(source, target, employer, report, transmission,
                new EmployerProfileSettings(employer.Id));
            await db.SaveChangesAsync(ct);

            var result = await NewService(db).TransferAsync(
                source.Id, employer.Id, target.Id, "integration-test", ct);

            Assert.False(result.Success);
            Assert.Equal("transmission_in_progress", result.Error);
            db.ChangeTracker.Clear();
            Assert.Equal(source.Id, (await db.Employers.SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.ManualReports.SingleAsync(x => x.Id == report.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.ReportTransmissions.SingleAsync(x => x.Id == transmission.Id, ct)).OrganizationId);
        });
    }

    [Fact]
    public async Task Database_failure_after_dependent_updates_rolls_back_entire_transfer()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var target = new Organization("Target", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved", "reg-rollback", "file-rollback");
            var person = new Person(source.Id, "", "Rollback", "Employee");
            person.SetProtectedIdentifier(PersonIdentifierType.IsraeliId, "encrypted-dummy", "rollback-hash");
            var employment = new Employment(source.Id, employer.Id, person.Id,
                new DateOnly(2026, 1, 1), "rollback-1", 5000);
            var report = new ManualReport(source.Id, employer.Id, new DateOnly(2026, 8, 1), null);
            db.AddRange(source, target, employer, person, employment, report,
                new EmployerProfileSettings(employer.Id));
            await db.SaveChangesAsync(ct);

            // Fail the final employer ownership update after dependent rows were updated.
            // The trigger exists only inside this test's disposable database.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION public.fail_employer_transfer_for_test()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'injected employer transfer failure';
                END;
                $$;
                CREATE TRIGGER fail_employer_transfer_for_test
                BEFORE UPDATE OF "OrganizationId" ON employers.employers
                FOR EACH ROW EXECUTE FUNCTION public.fail_employer_transfer_for_test();
                """, ct);

            var exception = await Assert.ThrowsAsync<PostgresException>(() => NewService(db).TransferAsync(
                source.Id, employer.Id, target.Id, "rollback-integration-test", ct));
            Assert.Contains("injected employer transfer failure", exception.MessageText, StringComparison.Ordinal);

            db.ChangeTracker.Clear();
            Assert.Equal(source.Id, (await db.Employers.SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.People.SingleAsync(x => x.Id == person.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.Employments.SingleAsync(x => x.Id == employment.Id, ct)).OrganizationId);
            Assert.Equal(source.Id, (await db.ManualReports.SingleAsync(x => x.Id == report.Id, ct)).OrganizationId);
            Assert.False(await db.AuditEvents.AnyAsync(x => x.EntityId == employer.Id &&
                x.Action == "employer.transferred", ct));
        });
    }

    [Fact]
    public async Task Removes_old_organization_grants_but_retains_standalone_employer_owner()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var target = new Organization("Target", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved", "reg-unique", "file-unique");
            var orgAdmin = new User("subject-org", "org@example.test", "Org Admin");
            var owner = new User("subject-owner", "owner@example.test", "Owner");
            var membership = new OrganizationMembership(orgAdmin.Id, source.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, orgAdmin.Id);
            var sourceGrant = new EmployerUserAccess(orgAdmin.Id, source.Id, employer.Id, EmployerRole.Admin);
            var ownerGrant = new EmployerUserAccess(owner.Id, source.Id, employer.Id, EmployerRole.Owner);
            db.AddRange(source, target, employer, orgAdmin, owner, membership,
                sourceGrant, ownerGrant, new EmployerProfileSettings(employer.Id));
            await db.SaveChangesAsync(ct);

            var result = await NewService(db).TransferAsync(
                source.Id, employer.Id, target.Id, "integration-test", ct);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.EmployerUserAccesses.AnyAsync(x =>
                x.Id == sourceGrant.Id, ct));
            Assert.Equal(target.Id, (await db.EmployerUserAccesses.SingleAsync(x =>
                x.Id == ownerGrant.Id, ct)).OrganizationId);
            Assert.False(await new OrganizationAccessService(db,
                new FakeCurrentUser(orgAdmin.Id)).CanAccessEmployerAsync(source.Id, employer.Id, ct));
            Assert.True(await new OrganizationAccessService(db,
                new FakeCurrentUser(owner.Id)).CanAccessEmployerAsync(target.Id, employer.Id, ct));
        });
    }

    [Fact]
    public async Task Concurrent_transfers_are_serialized_and_exactly_one_target_wins()
    {
        await InIsolatedDatabase(async (seedDb, ct) =>
        {
            var source = new Organization("Source", OrganizationType.PayrollOffice);
            var firstTarget = new Organization("First target", OrganizationType.PayrollOffice);
            var secondTarget = new Organization("Second target", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved", "reg-concurrent", "file-concurrent");
            seedDb.AddRange(source, firstTarget, secondTarget, employer,
                new EmployerProfileSettings(employer.Id));
            await seedDb.SaveChangesAsync(ct);

            var connectionString = new NpgsqlConnectionStringBuilder(
                PostgresTestEnvironment.RequireConnectionString())
            {
                Database = seedDb.Database.GetDbConnection().Database,
                Pooling = false
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(connectionString).Options;
            await using var firstDb = new AlphaDbContext(options);
            await using var secondDb = new AlphaDbContext(options);
            var first = NewService(firstDb).TransferAsync(
                source.Id, employer.Id, firstTarget.Id, "concurrency-first", ct);
            var second = NewService(secondDb).TransferAsync(
                source.Id, employer.Id, secondTarget.Id, "concurrency-second", ct);

            var results = await Task.WhenAll(first, second);
            Assert.Single(results, result => result.Success);
            Assert.Single(results, result => !result.Success && result.Error == "employer_not_found");

            seedDb.ChangeTracker.Clear();
            var finalOrganizationId = (await seedDb.Employers.AsNoTracking()
                .SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId;
            Assert.Contains(finalOrganizationId, new[] { firstTarget.Id, secondTarget.Id });
            Assert.Equal(1, await seedDb.AuditEvents.CountAsync(x =>
                x.EntityId == employer.Id && x.Action == "employer.transferred", ct));
        });
    }

    private sealed class FakeCurrentUser(Guid userId, bool isPlatformAdmin = true) : ICurrentUser
    {
        public Guid UserId => userId;
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isPlatformAdmin;
    }

    private static EmployerTransferService NewService(AlphaDbContext db, ICurrentUser? currentUser = null) =>
        new(db, new EntitlementService(db), new BillingInheritanceService(db),
            new OrganizationEntitlementLock(db), currentUser ?? new FakeCurrentUser(Guid.NewGuid()));

    // Isolated databases ensure these integration tests never modify production
    // and do not race with the other PostgreSQL-backed tests in CI.
    private static async Task InIsolatedDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
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
