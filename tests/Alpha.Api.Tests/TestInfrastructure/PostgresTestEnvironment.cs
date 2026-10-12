using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.TestInfrastructure;

internal static class PostgresTestEnvironment
{
    private const string VariableName = "ALPHA_TEST_POSTGRES";
    private const string RequiredDatabase = "alpha_tests";

    public static string RequireConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Skip($"PostgreSQL integration test skipped because {VariableName} is not configured.");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"{VariableName} is not a valid PostgreSQL connection string.", exception);
        }

        var isLoopback = string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(builder.Host, "::1", StringComparison.Ordinal);
        if (!isLoopback || !string.Equals(builder.Database, RequiredDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Safety guard rejected {VariableName}. Integration tests require a loopback host and database '{RequiredDatabase}'.");
        }

        return builder.ConnectionString;
    }
}
