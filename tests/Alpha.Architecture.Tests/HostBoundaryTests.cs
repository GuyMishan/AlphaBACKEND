using System.Text.RegularExpressions;
using Xunit;
using static Alpha.Architecture.Tests.Repository;

namespace Alpha.Architecture.Tests;

/// <summary>Alpha.Api is the composition root and process host; business features live in modules.</summary>
public sealed class HostBoundaryTests
{
    private static readonly string Program = Read("src/Alpha.Api/Program.cs");

    [Fact]
    public void Host_declares_no_business_routes()
    {
        Assert.DoesNotContain(Directory.EnumerateFiles(Path("src/Alpha.Api"), "*Endpoints*.cs", SearchOption.AllDirectories),
            p => !IsBuildOutput(p));

        var routes = SourceFiles("src/Alpha.Api")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file),
                "Map(?<verb>Group|Get|Post|Put|Delete|Patch)\\s*\\(\\s*\"(?<route>[^\"]+)\"").Cast<Match>())
            .Select(m => $"{m.Groups["verb"].Value.ToUpperInvariant()}|{m.Groups["route"].Value}")
            .Order(StringComparer.Ordinal);

        // Health probes plus the two route groups shared by more than one module.
        Assert.Equal(new[] { "GET|/health/db", "GROUP|/api/platform", "GROUP|/api/reference-data" }, routes);
    }

    [Fact]
    public void Host_uses_module_facades_not_module_features()
    {
        foreach (var file in SourceFiles("src/Alpha.Api"))
        {
            var source = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(source, @"\bAlpha\.Modules\.[A-Za-z]+\.(Features|Application)\b"),
                $"{Relative(file)} reaches into a module's features; expose a facade on the module instead.");
        }
    }

    [Fact]
    public void Every_module_is_registered_and_mapped_exactly_once()
    {
        foreach (var registration in new[]
                 {
                     "AddIdentityAccessModule", "AddOrganizationsModule", "AddBillingModule", "AddReportingModule",
                     "AddReferenceDataModule", "MapIdentityAccessModule", "MapOrganizationsModule", "MapWorkforceModule",
                     "MapBillingModule", "MapReportingModule", "MapReferenceDataModule", "MapAuditingPlatformEndpoints",
                     "MapIdentityAccessPlatformEndpoints", "MapGeneralReferenceData", "MapReportingReferenceData",
                     "UseReportingModule", "InitializeReportingAsync", "AlphaDatabaseInitializer.InitializeAsync"
                 })
            Assert.Single(Regex.Matches(Program, $@"\b{Regex.Escape(registration)}\s*\(").Cast<Match>());

        // Feature-level registrations belong to the owning module, never to the host.
        Assert.DoesNotMatch(@"\.Map(?!AuditingPlatform|IdentityAccessPlatform)[A-Za-z]+Endpoints\s*\(", Program);
        Assert.DoesNotMatch(@"AddScoped<(EmployerTransferService|BillingGateService|OrganizationAccessService|InvitationService|OtpDelivery|EmployerInterfaceService|ReportPaymentAccountService)>", Program);
        Assert.DoesNotMatch(@"EnsureCreatedAsync|EnsureUpdatedAsync|MigrateAsync|EnsureDeleted", Program);
    }

    [Fact]
    public void Each_module_has_one_registration_facade_at_its_root()
    {
        foreach (var module in BusinessModules)
        {
            var facade = Path($"src/Modules/{module}/Alpha.Modules.{module}/{module}Module.cs");
            Assert.True(File.Exists(facade), $"Module {module} must expose {module}Module.cs.");
        }
    }

    [Fact]
    public void Security_middleware_order_is_preserved()
    {
        var orderedMarkers = new[]
        {
            "app.UseAlphaReverseProxy(configuration, app.Logger)",
            "app.UseExceptionHandler()",
            "app.UseStatusCodePages()",
            "app.UseMiddleware<SecurityHeadersMiddleware>()",
            "app.UseAuthentication()",
            "app.UseMiddleware<SessionActivityMiddleware>()",
            "app.UseRateLimiter()",
            "app.UseAuthorization()",
            "app.UseMiddleware<SecurityAuditMiddleware>()",
            "app.UseReportingModule()"
        };

        var previous = -1;
        foreach (var marker in orderedMarkers)
        {
            var current = Program.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after the preceding middleware.");
            previous = current;
        }
    }

    [Fact]
    public void Dockerfile_restore_layer_copies_every_project_the_api_depends_on()
    {
        var graph = ProjectReferences().GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());
        var required = new HashSet<string>(StringComparer.Ordinal);
        void Collect(string project)
        {
            if (!required.Add(project)) return;
            foreach (var dependency in graph.GetValueOrDefault(project, [])) Collect(dependency);
        }
        Collect("src/Alpha.Api/Alpha.Api.csproj");

        var dockerfile = Read("Dockerfile");
        foreach (var project in required)
            Assert.True(Regex.IsMatch(dockerfile, $@"^COPY {Regex.Escape(project)} ", RegexOptions.Multiline),
                $"Dockerfile restore layer is missing {project}; the container build would fail.");
    }

    [Fact]
    public void Every_module_endpoint_goes_through_the_single_error_contract()
    {
        // Module routes are mapped on the root group that carries ApiErrorResultFilter.
        Assert.Matches(@"var api = app\.MapGroup\(string\.Empty\)\.AddEndpointFilter<ApiErrorResultFilter>\(\);", Program);
        foreach (Match map in Regex.Matches(Program, @"(?<target>\w+)\.Map(?<module>[A-Za-z]+)Module\(\)"))
            Assert.Equal("api", map.Groups["target"].Value);
        Assert.Matches(@"var platform = api\.MapGroup\(", Program);
        Assert.Matches(@"var referenceData = api\.MapGroup\(", Program);

        // Modules describe failures with Results.*; only the host serializes error bodies.
        foreach (var file in SourceFiles("src/Modules"))
            Assert.DoesNotMatch(@"WriteAsJsonAsync\(\s*new\s*\{\s*error", File.ReadAllText(file));
    }

    [Fact]
    public void Authorization_defaults_to_deny()
    {
        var setup = Read("src/Alpha.Api/Authentication/AuthenticationSetup.cs");
        Assert.Contains("FallbackPolicy", setup, StringComparison.Ordinal);
        Assert.Contains("RequireAuthenticatedUser()", setup, StringComparison.Ordinal);
    }
}
