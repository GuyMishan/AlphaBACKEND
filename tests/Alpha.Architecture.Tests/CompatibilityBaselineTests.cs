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
        var routes = SourceFiles("src").Where(path => !path.EndsWith("LegalDocumentEndpoints.cs", StringComparison.Ordinal))
            .SelectMany(path => routePattern.Matches(File.ReadAllText(path)).Cast<Match>())
            .Select(match => $"{match.Groups["verb"].Value.ToUpperInvariant()}|{match.Groups["route"].Value}")
            .Where(route => route is not ("POST|/api/auth/legal/accept" or "PUT|/marketing"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(routes.Length == 248 && Fingerprint(routes) == RouteBaseline,
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
            .Where(table => table != "reference_data.legal_document_versions" && table != "identity.registration_consent_intents" && table != "identity.user_consent_events" && table != "billing.pending_payment_setup_details")
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
            "OrganizationProfileSchemaInitializer.EnsureCreatedAsync", "BillingSchemaInitializer.EnsureCreatedAsync", "BillingPendingSetupSchemaInitializer.EnsureCreatedAsync",
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
            "SalaryLayerSchemaInitializer.EnsureCreatedAsync", "LegalDocumentSchemaInitializer.EnsureCreatedAsync", "RegistrationConsentSchemaInitializer.EnsureCreatedAsync"
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
            .Where(f => !f.EndsWith("AlphaDatabaseInitializer.cs", StringComparison.Ordinal) && !f.EndsWith("LegalDocumentSchemaInitializer.cs", StringComparison.Ordinal) && !f.EndsWith("RegistrationConsentSchemaInitializer.cs", StringComparison.Ordinal) && !f.EndsWith("BillingPendingSetupSchemaInitializer.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => $"{System.IO.Path.GetFileName(f)}\n{File.ReadAllText(f).ReplaceLineEndings("\n")}");
        Assert.Equal(31, files.Count());
        Assert.Contains("LegalDocumentSchemaInitializer.EnsureCreatedAsync", Read("src/Alpha.Infrastructure/Persistence/AlphaDatabaseInitializer.cs"));
        Assert.Contains("BillingPendingSetupSchemaInitializer.EnsureCreatedAsync", Read("src/Alpha.Infrastructure/Persistence/AlphaDatabaseInitializer.cs"));
        Assert.Contains("CREATE TABLE IF NOT EXISTS billing.pending_payment_setup_details", Read("src/Alpha.Infrastructure/Persistence/BillingPendingSetupSchemaInitializer.cs"));
        Assert.True(Fingerprint(files) == SchemaInitializerBaseline,
            $"Schema initializer SQL changed, fingerprint {Fingerprint(files)}. Update only together with docs/database.md.");
    }

    [Fact]
    public void Docker_context_includes_embedded_legal_policy()
    {
        var dockerIgnore = Read(".dockerignore");
        Assert.Contains("!docs/legal/", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains("!docs/legal/ALPHA_LEGAL_POLICY_V1.md", dockerIgnore, StringComparison.Ordinal);
        var infrastructureProject = Read("src/Alpha.Infrastructure/Alpha.Infrastructure.csproj");
        Assert.Contains("../../docs/legal/ALPHA_LEGAL_POLICY_V1.md", infrastructureProject, StringComparison.Ordinal);
        Assert.True(File.Exists(Path("docs/legal/ALPHA_LEGAL_POLICY_V1.md")));
        Assert.Contains("LegalDocumentSchemaInitializer.PublishVersionOneIfEmptyAsync",
            Read("src/Alpha.Infrastructure/Persistence/AlphaDatabaseInitializer.cs"), StringComparison.Ordinal);
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

    internal const string RouteBaseline = "3bc933694debf60d48eff4f5beb39beb1a43f67fac2ae84b66c73b42d422a952";
    internal const string TableBaseline = "cf1d402e5a2a8aa29b39821298684b2826e204535c97dea04d46735673994b53";
    internal const string SchemaInitializerBaseline = "ec93b2da33d4e60afc7721fea09a6aa23a6f06c1ec92fd1d7eb5a439fe466fdd";
}
