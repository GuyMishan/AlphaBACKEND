using System.Text.RegularExpressions;
using Xunit;
using static Alpha.Architecture.Tests.Repository;

namespace Alpha.Architecture.Tests;

/// <summary>
/// Production-compatibility baselines. A failure here means a public route, table definition or schema
/// initializer changed. That is allowed only as a reviewed change: update the baseline in the same commit
/// and record why in docs/database.md (schema) or the API compatibility notes (routes).
/// </summary>
public sealed class CompatibilityBaselineTests
{
    [Fact]
    public void Api_route_declarations_match_the_production_baseline()
    {
        var routePattern = new Regex("Map(?<verb>Group|Get|Post|Put|Delete|Patch)\\s*\\(\\s*\"(?<route>[^\"]+)\"", RegexOptions.CultureInvariant);
        var routes = SourceFiles("src")
            .SelectMany(path => routePattern.Matches(File.ReadAllText(path)).Cast<Match>())
            .Select(match => $"{match.Groups["verb"].Value.ToUpperInvariant()}|{match.Groups["route"].Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(routes.Length == 246 && Fingerprint(routes) == RouteBaseline,
            $"Route baseline changed: {routes.Length} routes, fingerprint {Fingerprint(routes)}. Update only for a reviewed API change.");
    }

    [Fact]
    public void Database_table_definitions_match_the_baseline()
    {
        var toTable = new Regex("ToTable\\(\\s*\"(?<table>[^\"]+)\"\\s*,\\s*\"(?<schema>[^\"]+)\"\\s*\\)", RegexOptions.CultureInvariant);
        var createTable = new Regex(
            "CREATE\\s+TABLE\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?(?<schema>[a-zA-Z_][a-zA-Z0-9_]*)\\.(?<table>[a-zA-Z_][a-zA-Z0-9_]*)\\s*\\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tables = SourceFiles("src/Alpha.Infrastructure/Persistence")
            .SelectMany(path =>
            {
                var source = File.ReadAllText(path);
                return toTable.Matches(source).Cast<Match>().Concat(createTable.Matches(source).Cast<Match>());
            })
            .Select(match => $"{match.Groups["schema"].Value}.{match.Groups["table"].Value}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(tables.Length == 67 && Fingerprint(tables) == TableBaseline,
            $"Table baseline changed: {tables.Length} tables, fingerprint {Fingerprint(tables)}. Update only for a reviewed schema change.");
    }

    [Fact]
    public void Schema_initializers_run_from_one_owner_in_the_production_order()
    {
        var expected = new[]
        {
            "IdentitySchemaInitializer.EnsureUpdatedAsync", "ReferentSchemaInitializer.EnsureUpdatedAsync",
            "EmployeeIdentitySchemaInitializer.EnsureUpdatedAsync", "EmployerAccessSchemaInitializer.EnsureUpdatedAsync",
            "AccessPermissionsSchemaInitializer.EnsureUpdatedAsync", "EmployerProfileSchemaInitializer.EnsureCreatedAsync", "EmployerDocumentSchemaInitializer.EnsureUpdatedAsync",
            "OrganizationProfileSchemaInitializer.EnsureCreatedAsync", "BillingSchemaInitializer.EnsureCreatedAsync",
            "BillingInheritanceSchemaInitializer.EnsureUpdatedAsync", "BillingV2SchemaInitializer.EnsureUpdatedAsync",
            "OtpSchemaInitializer.EnsureCreatedAsync", "RegistrationOtpSchemaInitializer.EnsureCreatedAsync",
            "InvitationSchemaInitializer.EnsureCreatedAsync", "ReportingSchemaInitializer.EnsureCreatedAsync",
            "Section14SchemaInitializer.EnsureUpdatedAsync", "SalaryAllocationSchemaInitializer.EnsureUpdatedAsync",
            "PensionFundSnapshotSchemaInitializer.EnsureUpdatedAsync", "ReportLifecycleSchemaInitializer.EnsureUpdatedAsync",
            "PaymentConfirmationSchemaInitializer.EnsureUpdatedAsync", "ReportTransmissionSchemaInitializer.EnsureUpdatedAsync",
            "EmployerInterfaceFeedbackSchemaInitializer.EnsureCreatedAsync", "ReportFeedbackOperationsSchemaInitializer.EnsureUpdatedAsync",
            "EmployerInterface006SchemaInitializer.EnsureUpdatedAsync", "ReferenceDataSchemaInitializer.EnsureCreatedAsync",
            "SelectOptionsSchemaInitializer.EnsureCreatedAsync", "EmployerInterface006CodebookInitializer.EnsureUpdatedAsync", "EmployerInterface006FieldMapInitializer.EnsureUpdatedAsync",
            "EmployerInterface006ReferenceRulesInitializer.EnsureUpdatedAsync", "EmployerInterface006ErrorCodeInitializer.EnsureUpdatedAsync",
            "SalaryLayerSchemaInitializer.EnsureCreatedAsync"
        };
        var call = new Regex("await\\s+(?<name>[A-Za-z0-9_]+Initializer)\\.(?<method>Ensure(?:Created|Updated)Async)\\(db\\)", RegexOptions.CultureInvariant);
        var owner = Read("src/Alpha.Infrastructure/Persistence/AlphaDatabaseInitializer.cs");
        Assert.Equal(expected, call.Matches(owner).Select(m => $"{m.Groups["name"].Value}.{m.Groups["method"].Value}"));

        // No other production code invokes an initializer, so the order has exactly one source of truth.
        foreach (var file in SourceFiles("src").Where(f => !f.EndsWith("AlphaDatabaseInitializer.cs", StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"[A-Za-z]+Initializer\.Ensure(Created|Updated)Async\(", File.ReadAllText(file));
    }

    [Fact]
    public void Schema_initializer_sql_changes_are_reviewed()
    {
        // Content gate over all compatibility DDL. Update only together with docs/database.md.
        var files = Directory.EnumerateFiles(Path("src/Alpha.Infrastructure/Persistence"), "*Initializer.cs")
            .Where(f => !f.EndsWith("AlphaDatabaseInitializer.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => $"{System.IO.Path.GetFileName(f)}\n{File.ReadAllText(f).ReplaceLineEndings("\n")}");
        Assert.Equal(31, files.Count());
        Assert.True(Fingerprint(files) == SchemaInitializerBaseline,
            $"Schema initializer SQL changed, fingerprint {Fingerprint(files)}. Update only together with docs/database.md.");
    }

    [Fact]
    public void Blank_database_bootstrap_has_no_hidden_initializer_dependency()
    {
        var snapshot = Read("src/Alpha.Infrastructure/Persistence/PensionFundSnapshotSchemaInitializer.cs");
        Assert.Contains("to_regclass('reference_data.pension_products') IS NOT NULL", snapshot, StringComparison.Ordinal);

        var owner = Read("src/Alpha.Infrastructure/Persistence/AlphaDatabaseInitializer.cs");
        Assert.Contains("EnsureCreatedAsync(ct)", owner, StringComparison.Ordinal);
        foreach (var file in SourceFiles("src").Where(f => !f.EndsWith("AlphaDatabaseInitializer.cs", StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"Database\.(EnsureCreated|EnsureDeleted|Migrate)(Async)?\(", File.ReadAllText(file));
    }

    internal const string RouteBaseline = "5ea7fca4409c1a7bbf63bf373df964269e8cdad9fb9fea1c6ac79fc885bb65e6";
    internal const string TableBaseline = "cf1d402e5a2a8aa29b39821298684b2826e204535c97dea04d46735673994b53";
    internal const string SchemaInitializerBaseline = "a93d298edd90b2a7fe3171ee2b78c8a9d5393e63d546e624963e8633b40ff7b3";
}
