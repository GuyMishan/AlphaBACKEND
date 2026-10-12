using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Integration;

/// <summary>
/// Verifies the startup database path on an empty database and on an existing, populated database.
/// Runs only against disposable databases on the loopback test server.
/// </summary>
public sealed class DatabaseBootstrapIntegrationTests
{
    [Fact]
    public async Task Empty_database_bootstraps_with_the_production_initializer_order_and_no_manual_prerequisites()
    {
        await InEmptyDatabase(async (db, ct) =>
        {
            await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: true, ct);

            Assert.True(await TableExistsAsync(db, "reference_data.pension_products", ct));
            Assert.True(await TableExistsAsync(db, "identity.user_sessions", ct));
            Assert.True(await TableExistsAsync(db, "audit.audit_events", ct));
            Assert.Equal(0, await db.Organizations.CountAsync(ct));
        });
    }

    [Fact]
    public async Task Empty_database_without_explicit_bootstrap_fails_instead_of_creating_schema()
    {
        await InEmptyDatabase(async (db, ct) =>
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: false, ct));
            Assert.False(await TableExistsAsync(db, "identity.users", ct));
        });
    }

    [Fact]
    public async Task Rerunning_startup_on_a_populated_database_preserves_data_and_still_runs_the_catalog_backfill()
    {
        await InEmptyDatabase(async (db, ct) =>
        {
            await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: true, ct);

            var organization = new Organization("Upgrade Tenant", OrganizationType.RegularOrganization);
            organization.Activate();
            var employer = new Employer(organization.Id, "Upgrade Employer", "51234567", "912345678");
            var person = new Person(organization.Id, string.Empty, "Synthetic", "Employee");
            person.SetProtectedIdentifier(PersonIdentifierType.IsraeliId, "alpha:v1:synthetic", "synthetic-hash");
            var employment = new Employment(organization.Id, employer.Id, person.Id, new DateOnly(2025, 1, 1), "UPG-1", 12_345.67m);
            var product = new EmployeePensionProduct(employment.Id, PensionProductType.PensionFund, "POL-1", 12_345.67m,
                "", "", false, null, true, new DateOnly(2025, 1, 1), null, "", "", "catalog-key-1", "101", "Fund",
                "Company", SalaryAllocationType.Fixed, 12_345.67m, 0, 3, "");
            db.AddRange(organization, employer, person, employment, product);
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO reference_data.pension_products (external_key, source, domain, fund_name, classification, last_seen_at, updated_at)
                VALUES ('catalog-key-1', 'test', 'pension', 'Fund', 'Synthetic classification', now(), now());
                """, ct);
            var before = await CountsAsync(db, ct);

            // Existing database: EnsureCreated is a no-op, every initializer is re-applied idempotently.
            await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: true, ct);
            await AlphaDatabaseInitializer.RunSchemaInitializersAsync(db);

            Assert.Equal(before, await CountsAsync(db, ct));
            db.ChangeTracker.Clear();
            var stored = await db.EmployeePensionProducts.AsNoTracking().SingleAsync(x => x.Id == product.Id, ct);
            Assert.Equal("Synthetic classification", stored.FundClassification);
            Assert.Equal(12_345.67m, (await db.Employments.AsNoTracking().SingleAsync(x => x.Id == employment.Id, ct)).MonthlySalary);
        });
    }

    [Fact]
    public async Task Startup_never_rewrites_user_data_on_a_database_without_data_fix_markers()
    {
        // Simulates a restored backup or a copied database: users exist, identity.data_fixes is empty.
        await InEmptyDatabase(async (db, ct) =>
        {
            await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: true, ct);
            var user = new Alpha.Domain.Identity.User("restored-user", "restored.user@test.invalid", "Restored User");
            db.Add(user);
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM identity.data_fixes", ct);

            await AlphaDatabaseInitializer.RunSchemaInitializersAsync(db);

            db.ChangeTracker.Clear();
            Assert.Equal("restored.user@test.invalid", (await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, ct)).Email);
        });
    }

    private static async Task<string> CountsAsync(AlphaDbContext db, CancellationToken ct) => string.Join(",",
        await db.Organizations.CountAsync(ct), await db.Employers.CountAsync(ct), await db.People.CountAsync(ct),
        await db.Employments.CountAsync(ct), await db.EmployeePensionProducts.CountAsync(ct));

    private static async Task<bool> TableExistsAsync(AlphaDbContext db, string qualifiedName, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        command.Parameters.Add(new NpgsqlParameter("name", qualifiedName));
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task InEmptyDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_bootstrap_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };
        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using var db = new AlphaDbContext(options);
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
