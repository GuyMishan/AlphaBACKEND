using Alpha.Infrastructure.Persistence;
using Alpha.SchemaVerifier;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Integration;

/// <summary>
/// The read-only schema verifier used before a release against the production database.
/// Every test runs on disposable databases on the loopback test server.
/// </summary>
public sealed class SchemaVerifierTests
{
    [Fact]
    public async Task Two_fresh_bootstraps_produce_identical_catalogs()
    {
        var server = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var first = await ExpectedSchema.BuildAsync(server, ct);
        var second = await ExpectedSchema.BuildAsync(server, ct);

        Assert.Empty(SchemaComparer.Compare(first, second));
        Assert.Equal(SchemaCatalog.ApplicationSchemas.Order(), first.Schemas.Order());
        Assert.Equal(77, first.Objects.Keys.Count(k => k.StartsWith("table ", StringComparison.Ordinal)));
        Assert.Contains("table reference_data.legal_document_versions", first.Objects.Keys);
    }

    [Fact]
    public async Task Historical_drift_is_classified_by_its_impact_on_the_application()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = await ExpectedSchema.BuildAsync(PostgresTestEnvironment.RequireConnectionString(), ct);

        await InBootstrappedDatabase(async (connectionString, ct2) =>
        {
            await Execute(connectionString, """
                -- Historical initializer-created FK that a fresh EF bootstrap does not have.
                ALTER TABLE identity.user_sessions ADD CONSTRAINT user_sessions_legacy_user_fk
                    FOREIGN KEY ("UserId") REFERENCES identity.users("Id") ON DELETE CASCADE;
                -- Drift that would break the application.
                ALTER TABLE organizations.organizations DROP COLUMN "UpdatedAt";
                ALTER TABLE identity.users ALTER COLUMN "Appearance" TYPE varchar(8);
                ALTER TABLE employers.employers ADD COLUMN legacy_required text NOT NULL DEFAULT 'x';
                ALTER TABLE employers.employers ALTER COLUMN legacy_required DROP DEFAULT;
                -- Harmless or performance-only drift.
                ALTER TABLE employers.employers ADD COLUMN legacy_note text NULL;
                DROP INDEX identity."IX_user_sessions_ExpiresAt";
                """, ct2);

            await using var connection = await SchemaCatalog.OpenReadOnlyAsync(connectionString, ct2);
            var differences = SchemaComparer.Compare(expected, await SchemaCatalog.CaptureAsync(connection, null, ct2));

            Severity Of(string fragment) => Assert.Single(differences, d => d.Object.Contains(fragment, StringComparison.Ordinal)).Severity;
            Assert.Equal(Severity.Review, Of("identity.user_sessions FOREIGN KEY (\"UserId\") REFERENCES identity.users(\"Id\") ON DELETE CASCADE"));
            Assert.Equal(Severity.Blocking, Of("organizations.organizations.UpdatedAt"));
            Assert.Equal(Severity.Blocking, Of("identity.users.Appearance"));
            Assert.Equal(Severity.Blocking, Of("employers.employers.legacy_required"));
            Assert.Equal(Severity.Info, Of("employers.employers.legacy_note"));
            Assert.Equal(Severity.Review, Of("\"ExpiresAt\""));
        });
    }

    [Fact]
    public async Task Capture_needs_only_connect_privilege_and_cannot_write_or_read_data()
    {
        var ct = TestContext.Current.CancellationToken;
        await InBootstrappedDatabase(async (connectionString, ct2) =>
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var role = "alpha_catalog_reader_" + Guid.NewGuid().ToString("N")[..8];
            await Execute(connectionString, $"""
                CREATE ROLE {role} LOGIN PASSWORD 'catalog-only-test';
                GRANT CONNECT ON DATABASE "{builder.Database}" TO {role};
                """, ct2);
            try
            {
                var reader = new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = "catalog-only-test" };
                await using var owner = await SchemaCatalog.OpenReadOnlyAsync(connectionString, ct2);
                var asOwner = await SchemaCatalog.CaptureAsync(owner, null, ct2);
                await using var restricted = await SchemaCatalog.OpenReadOnlyAsync(reader.ConnectionString, ct2);
                var asReader = await SchemaCatalog.CaptureAsync(restricted, null, ct2);

                Assert.Empty(SchemaComparer.Compare(asOwner, asReader));

                // The capture role cannot read customer data.
                await using (var select = new NpgsqlCommand("SELECT count(*) FROM identity.users", restricted))
                    Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync(ct2))).SqlState);
                // And the verifier session refuses writes even for the owner.
                await using (var write = new NpgsqlCommand("CREATE TABLE public.verifier_write_probe (id int)", owner))
                    Assert.Equal("25006", (await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(ct2))).SqlState);
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await Execute(connectionString, $"""
                    REVOKE CONNECT ON DATABASE "{builder.Database}" FROM {role};
                    DROP ROLE {role};
                    """, CancellationToken.None);
            }
        });
    }

    private static async Task InBootstrappedDatabase(Func<string, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_verifier_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };
        await Execute(baseConnection, $"CREATE DATABASE \"{database}\"", ct);
        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using (var db = new AlphaDbContext(options))
                await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: true, ct);
            await test(connection.ConnectionString, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(baseConnection, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", CancellationToken.None);
        }
    }

    private static async Task Execute(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
