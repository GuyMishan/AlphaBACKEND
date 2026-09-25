using Xunit;

namespace Alpha.Api.Tests;

public sealed class ReportingSchemaSqlGuardTests
{
    [Fact]
    public void Reporting_schema_initializer_has_valid_postgres_dollar_quoting()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "ReportingSchemaInitializer.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("DO $\n", source, StringComparison.Ordinal);
        Assert.Equal(
            Count(source, "DO $$"),
            Count(source, "END $$;"));
    }

    private static int Count(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
