using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Pension_fund_snapshot_initializer_executes_on_postgres()
    {
        var cs = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var ct = TestContext.Current.CancellationToken;
        await using var db = CreateDb(cs);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS employees;
            CREATE SCHEMA IF NOT EXISTS reporting;
            CREATE SCHEMA IF NOT EXISTS reference_data;
            CREATE TABLE IF NOT EXISTS employees.employee_pension_products (
                "Id" uuid PRIMARY KEY, "EmploymentId" uuid NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
            CREATE TABLE IF NOT EXISTS reporting.manual_reports ("Id" uuid PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS reporting.manual_report_employees (
                "Id" uuid PRIMARY KEY, "MonthlySalary" numeric(18,2) NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS reporting.manual_report_products (
                "Id" uuid PRIMARY KEY, "ReportEmployeeId" uuid NOT NULL, "Salary" numeric(18,2) NOT NULL DEFAULT 0,
                "SalaryAllocationType" varchar(30) NOT NULL DEFAULT 'Fixed', "SalaryAllocationValue" numeric(18,4) NULL,
                "AllocationOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS reporting.manual_contributions (
                "Id" uuid PRIMARY KEY, "ReportProductId" uuid NOT NULL, "Amount" numeric(18,2) NOT NULL DEFAULT 0,
                "Percentage" numeric(9,4) NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS reference_data.pension_products (
                external_key varchar(500) PRIMARY KEY, classification varchar(200));
            """, ct);

        await PensionFundSnapshotSchemaInitializer.EnsureUpdatedAsync(db, ct);

        var paths = await db.Database.SqlQueryRaw<string>("""
            SELECT p.proconfig[1]
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'reporting'
              AND p.proname IN ('recalculate_report_product_salaries','recalculate_report_product_salaries_trigger','sync_manual_contribution_amount')
            ORDER BY p.proname
            """).ToListAsync(ct);
        Assert.Equal(3, paths.Count);
        Assert.All(paths, value => Assert.Contains("search_path=reporting, pg_temp", value, StringComparison.Ordinal));
    }

    [Fact]
    public void Feedback_schema_keeps_idempotency_and_product_lookup_indexes()
    {
        var root = FindRepoRoot();
        var feedbackPath = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "EmployerInterfaceFeedbackSchemaInitializer.cs");
        var operationsPath = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "ReportFeedbackOperationsSchemaInitializer.cs");
        var feedbackSource = File.ReadAllText(feedbackPath);
        var operationsSource = File.ReadAllText(operationsPath);

        Assert.Contains("IX_employer_interface_feedback_EmployerId_PayloadHash", feedbackSource, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS", feedbackSource, StringComparison.Ordinal);
        Assert.Contains("IX_contribution_feedback_Product_ReceivedAt", operationsSource, StringComparison.Ordinal);
        Assert.Contains("\"ReportProductId\", \"ReceivedAt\"", operationsSource, StringComparison.Ordinal);
        Assert.Contains("IX_contribution_feedback_Feedback_Record_Sequence", operationsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Employer_interface_006_initializer_backfills_required_identifier_strings()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "EmployerInterface006SchemaInitializer.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("SET \"InterfaceTransferIdentifier\" = COALESCE(\"InterfaceTransferIdentifier\", '')", source, StringComparison.Ordinal);
        Assert.Contains("\"PreviousClearingIdentifier\" = COALESCE(\"PreviousClearingIdentifier\", '')", source, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"InterfaceTransferIdentifier\" SET NOT NULL", source, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"ClearingIdentifier\" SET NOT NULL", source, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"PreviousIdentifier\" SET NOT NULL", source, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"PreviousClearingIdentifier\" SET NOT NULL", source, StringComparison.Ordinal);
    }


    [Fact]
    public void Reporting_schema_adds_correction_workspace_and_product_version_guards()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "ReportingSchemaInitializer.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("ADD COLUMN IF NOT EXISTS \"IsCorrectionWorkspace\"", source, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"HasCorrectionChanges\"", source, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"SourceReportProductId\"", source, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"IsCorrectionChanged\"", source, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"CorrectionOperationCode\"", source, StringComparison.Ordinal);
        Assert.Contains("CK_manual_report_products_correction_operation", source, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX IF EXISTS reporting.\"UX_manual_reports_open_correction_workspace\"", source, StringComparison.Ordinal);
        Assert.Contains("'Processing'", source, StringComparison.Ordinal);
        Assert.Contains("RevisionRootReportId", source, StringComparison.Ordinal);
        Assert.Contains("RevisionNumber", source, StringComparison.Ordinal);
        Assert.Contains("IsRevisionSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("IsTechnicalCorrectionDocument", source, StringComparison.Ordinal);
        Assert.Contains("CorrectionWorkspaceId", source, StringComparison.Ordinal);
        Assert.Contains("UX_manual_reports_business_revision", source, StringComparison.Ordinal);
        Assert.Contains("UX_manual_reports_open_correction_workspace", source, StringComparison.Ordinal);
        Assert.Contains("FK_manual_report_products_source_product", source, StringComparison.Ordinal);
    }

    private static AlphaDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql(connectionString).Options;
        return new AlphaDbContext(options);
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
