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
            .Where(route => route is not ("POST|/api/auth/legal/accept" or "PUT|/marketing" or "POST|/abandon" or "GET|/status"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("org.MapGet(\"/status\", GetOrganizationSetupStatusAsync)", Read("src/Modules/Billing/Alpha.Modules.Billing/Features/PaymentMethods/PaymentProviderEndpoints.cs"));
        Assert.Contains("employer.MapGet(\"/status\", GetEmployerSetupStatusAsync)", Read("src/Modules/Billing/Alpha.Modules.Billing/Features/PaymentMethods/PaymentProviderEndpoints.cs"));
        Assert.Contains("org.MapPost(\"/abandon\", AbandonOrganizationSetupAsync)", Read("src/Modules/Billing/Alpha.Modules.Billing/Features/PaymentMethods/PaymentProviderEndpoints.cs"));
        Assert.Contains("employer.MapPost(\"/abandon\", AbandonEmployerSetupAsync)", Read("src/Modules/Billing/Alpha.Modules.Billing/Features/PaymentMethods/PaymentProviderEndpoints.cs"));
        Assert.True(routes.Length == 263 && Fingerprint(routes) == RouteBaseline,
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
            .Where(table => table is not (
                "reference_data.manufacturer_company_groups" or
                "reference_data.manufacturer_contact_emails" or
                "reference_data.product_contact_email_overrides" or
                "reference_data.manufacturer_error_email_templates" or
                "reporting.feedback_manufacturer_email_dispatches"))
            .Where(table => table != "reference_data.legal_document_versions" && table != "identity.registration_consent_intents" && table != "identity.user_consent_events" && table != "billing.pending_payment_setup_details")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(tables.Length == 69 && Fingerprint(tables) == TableBaseline,
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

    internal const string RouteBaseline = "685e036e9300b77b1ab74e9aaa86480d6b453441dde1b04565268d1698882f5b";
    internal const string TableBaseline = "34391c48ed17c2638b1451ae694ed4a4b8449aaf5db75e8320894e7582d1bc2c";
    internal const string SchemaInitializerBaseline = "470d8b190dd323b6435e84862124962b80d0509d480acff55c437e7e43509c28";
}
