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

    [Fact]
    public void Reporting_schema_adds_national_id_lookup_hash_before_creating_its_index()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "ReportingSchemaInitializer.cs");
        var source = File.ReadAllText(path);

        var column = source.IndexOf("ADD COLUMN IF NOT EXISTS \"NationalIdLookupHash\"", StringComparison.Ordinal);
        var index = source.IndexOf("IX_manual_report_employees_report_national_id_hash", StringComparison.Ordinal);
        Assert.True(column >= 0, "NationalIdLookupHash migration is missing.");
        Assert.True(index > column, "The lookup-hash index must be created only after the column exists.");
    }

    [Fact]
    public void Reporting_database_functions_pin_their_search_path()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "PensionFundSnapshotSchemaInitializer.cs");
        var source = File.ReadAllText(path);

        Assert.Equal(3, Count(source, "SET search_path = reporting, pg_temp"));
        Assert.Contains("reporting.recalculate_report_product_salaries", source, StringComparison.Ordinal);
        Assert.Contains("reporting.recalculate_report_product_salaries_trigger", source, StringComparison.Ordinal);
        Assert.Contains("reporting.sync_manual_contribution_amount", source, StringComparison.Ordinal);
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
