using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ReferentAuthorizationTests
{
    [Fact]
    public async Task Referent_can_operate_selected_employers_across_organizations_without_becoming_platform_admin()
    {
        await WithDatabase(async (db, ct) =>
        {
            var one = new Organization("One", OrganizationType.PayrollOffice);
            var two = new Organization("Two", OrganizationType.PayrollOffice);
            var three = new Organization("Three", OrganizationType.PayrollOffice);
            var orgEmployer = new Employer(one.Id, "Org Employer", "reg-1", "file-1");
            var selectedEmployer = new Employer(two.Id, "Selected Employer", "reg-2", "file-2");
            var forbiddenEmployer = new Employer(two.Id, "Other Employer", "reg-3", "file-3");
            var unrelatedEmployer = new Employer(three.Id, "Unrelated", "reg-4", "file-4");
            var user = new User("referent-subject", "referent@example.test", "Test Referent");
            user.SetReferent(true);
            db.AddRange(one, two, three, orgEmployer, selectedEmployer,
                forbiddenEmployer, unrelatedEmployer, user,
                new ReferentOrganizationAssignment(user.Id, one.Id),
                new ReferentEmployerAssignment(user.Id, selectedEmployer.Id));
            await db.SaveChangesAsync(ct);

            var access = new OrganizationAccessService(db, new TestCurrentUser(user.Id));
            Assert.True(await access.CanViewOrganizationAsync(one.Id, ct));
            Assert.True(await access.CanEditOrganizationGeneralAsync(one.Id, ct));
            Assert.False(await access.CanManageOrganizationAsync(one.Id, ct));
            Assert.True(await access.CanManageEmployerAsync(one.Id, orgEmployer.Id, ct));
            Assert.True(await access.CanManageEmployerAsync(two.Id, selectedEmployer.Id, ct));
            Assert.True(await access.CanCreateReportAsync(two.Id, selectedEmployer.Id, ct));
            Assert.True(await access.CanAccessOrganizationScopeAsync(two.Id, ct));
            Assert.False(await access.CanViewOrganizationAsync(two.Id, ct));
            Assert.False(await access.CanManageEmployerAsync(two.Id, forbiddenEmployer.Id, ct));
            Assert.False(await access.CanAccessEmployerAsync(three.Id, unrelatedEmployer.Id, ct));
            Assert.False(await access.CanAccessEmployerAsync(one.Id, selectedEmployer.Id, ct));
        });
    }

    [Fact]
    public async Task Disabling_referent_revokes_all_assignment_based_permissions()
    {
        await WithDatabase(async (db, ct) =>
        {
            var organization = new Organization("Source", OrganizationType.PayrollOffice);
            var employer = new Employer(organization.Id, "Employer", "reg-1", "file-1");
            var user = new User("referent-subject", "referent@example.test", "Test Referent");
            user.SetReferent(true);
            db.AddRange(organization, employer, user,
                new ReferentOrganizationAssignment(user.Id, organization.Id),
                new ReferentEmployerAssignment(user.Id, employer.Id));
            await db.SaveChangesAsync(ct);
            var granted = new OrganizationAccessService(db, new TestCurrentUser(user.Id));
            Assert.True(await granted.CanAccessEmployerAsync(organization.Id, employer.Id, ct));
            user.SetReferent(false);
            await db.SaveChangesAsync(ct);
            var revoked = new OrganizationAccessService(db, new TestCurrentUser(user.Id));
            Assert.False(await revoked.CanAccessEmployerAsync(organization.Id, employer.Id, ct));
            Assert.False(await revoked.CanViewOrganizationAsync(organization.Id, ct));
        });
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public Guid UserId => id;
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
    }

    private static async Task WithDatabase(Func<AlphaDbContext, CancellationToken, Task> execute)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_referent_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false }.ConnectionString)
                .Options;
            await using var db = new AlphaDbContext(options);
            await db.Database.EnsureCreatedAsync(ct);
            await execute(db, ct);
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
