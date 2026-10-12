using Alpha.Infrastructure.Persistence;
using Alpha.SchemaVerifier;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Usage (connection strings are read from environment variables and never printed):
//   expected --server-env ALPHA_SCHEMA_EXPECTED_SERVER --out expected.json
//   capture  --connection-env ALPHA_SCHEMA_TARGET --out actual.json
//   compare  expected.json actual.json
// Exit codes: 0 no blocking or review differences, 1 review differences, 2 blocking differences, 64 usage error.

var ct = CancellationToken.None;
try
{
    switch (args.FirstOrDefault())
    {
        case "expected":
        {
            var server = Env(Option("--server-env"));
            var catalog = await ExpectedSchema.BuildAsync(server, ct);
            await File.WriteAllTextAsync(Option("--out"), catalog.ToJson(), ct);
            Console.WriteLine($"Expected schema captured: {catalog.Objects.Count} objects in {catalog.Schemas.Count} schemas.");
            return 0;
        }
        case "capture":
        {
            await using var connection = await SchemaCatalog.OpenReadOnlyAsync(Env(Option("--connection-env")), ct);
            var catalog = await SchemaCatalog.CaptureAsync(connection, null, ct);
            await File.WriteAllTextAsync(Option("--out"), catalog.ToJson(), ct);
            Console.WriteLine($"Schema captured read-only: {catalog.Objects.Count} objects in {catalog.Schemas.Count} schemas. No data was read.");
            return 0;
        }
        case "compare" when args.Length >= 3:
        {
            var expected = SchemaCatalog.FromJson(await File.ReadAllTextAsync(args[1], ct));
            var actual = SchemaCatalog.FromJson(await File.ReadAllTextAsync(args[2], ct));
            var differences = SchemaComparer.Compare(expected, actual);
            foreach (var group in differences.GroupBy(x => x.Severity).OrderByDescending(x => x.Key))
            {
                Console.WriteLine($"## {group.Key} ({group.Count()})");
                foreach (var difference in group)
                    Console.WriteLine($"- {difference.Object}\n    {difference.Reason}; expected: '{difference.Expected}' actual: '{difference.Actual}'");
            }
            var worst = differences.Count == 0 ? Severity.Info : differences.Max(x => x.Severity);
            Console.WriteLine(differences.Count == 0 ? "Schemas match." : $"Result: {worst}.");
            return worst switch { Severity.Blocking => 2, Severity.Review => 1, _ => 0 };
        }
        default:
            Console.Error.WriteLine("Usage: expected --server-env VAR --out FILE | capture --connection-env VAR --out FILE | compare EXPECTED ACTUAL");
            return 64;
    }
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NpgsqlException)
{
    // Exception messages from Npgsql can contain host names; print only the type and SQL state.
    Console.Error.WriteLine($"Failed: {ex.GetType().Name}{(ex is PostgresException pg ? $" {pg.SqlState}" : "")}. {(ex is NpgsqlException ? "" : ex.Message)}");
    return 2;
}

string Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {name}.");
}

static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new ArgumentException($"Environment variable {name} is not set.");

/// <summary>Builds the schema the current code expects on a throwaway database on a loopback server.</summary>
public static class ExpectedSchema
{
    public static async Task<SchemaCatalog> BuildAsync(string serverConnectionString, CancellationToken ct)
    {
        var server = new NpgsqlConnectionStringBuilder(serverConnectionString);
        if (server.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("The expected schema is built only on a loopback PostgreSQL server.");

        var database = "alpha_schema_expected_" + Guid.NewGuid().ToString("N");
        await Admin(server.ConnectionString, $"CREATE DATABASE \"{database}\"", ct);
        try
        {
            var target = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = database, Pooling = false };
            var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql(target.ConnectionString).Options;
            await using (var db = new AlphaDbContext(options))
                await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase: true, ct);

            await using var connection = await SchemaCatalog.OpenReadOnlyAsync(target.ConnectionString, ct);
            return await SchemaCatalog.CaptureAsync(connection, null, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Admin(server.ConnectionString, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", CancellationToken.None);
        }
    }

    private static async Task Admin(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
