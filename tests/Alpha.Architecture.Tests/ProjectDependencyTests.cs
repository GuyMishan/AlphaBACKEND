using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using static Alpha.Architecture.Tests.Repository;

namespace Alpha.Architecture.Tests;

/// <summary>Compile-time structure: which project may depend on which.</summary>
public sealed class ProjectDependencyTests
{
    [Fact]
    public void Project_reference_graph_matches_the_approved_modular_monolith()
    {
        const string api = "src/Alpha.Api/Alpha.Api.csproj";
        const string domain = "src/Alpha.Domain/Alpha.Domain.csproj";
        const string infrastructure = "src/Alpha.Infrastructure/Alpha.Infrastructure.csproj";
        static string Module(string name) => $"src/Modules/{name}/Alpha.Modules.{name}/Alpha.Modules.{name}.csproj";
        static string Contracts(string name) => $"src/Modules/{name}/Alpha.Modules.{name}.Contracts/Alpha.Modules.{name}.Contracts.csproj";

        var expected = new[]
        {
            (api, infrastructure), (api, Module("Auditing")), (api, Contracts("Billing")), (api, Module("Billing")),
            (api, Contracts("IdentityAccess")), (api, Module("IdentityAccess")), (api, Module("Organizations")),
            (api, Module("ReferenceData")), (api, Contracts("Reporting")), (api, Module("Reporting")), (api, Module("Workforce")),
            (infrastructure, domain),
            (Module("Auditing"), infrastructure), (Module("Auditing"), Contracts("IdentityAccess")),
            (Module("Billing"), domain), (Module("Billing"), infrastructure), (Module("Billing"), Contracts("Billing")),
            (Module("Billing"), Contracts("IdentityAccess")), (Module("Billing"), Contracts("Reporting")),
            (Module("IdentityAccess"), domain), (Module("IdentityAccess"), infrastructure),
            (Module("IdentityAccess"), Contracts("Billing")), (Module("IdentityAccess"), Contracts("IdentityAccess")),
            (Module("Organizations"), domain), (Module("Organizations"), infrastructure),
            (Module("Organizations"), Contracts("Billing")), (Module("Organizations"), Contracts("IdentityAccess")),
            (Module("ReferenceData"), infrastructure), (Module("ReferenceData"), Contracts("IdentityAccess")),
            (Module("Reporting"), domain), (Module("Reporting"), infrastructure), (Module("Reporting"), Contracts("Billing")),
            (Module("Reporting"), Contracts("IdentityAccess")), (Module("Reporting"), Contracts("Reporting")),
            (Module("Workforce"), domain), (Module("Workforce"), infrastructure), (Module("Workforce"), Contracts("Billing")),
            (Module("Workforce"), Contracts("IdentityAccess")), (Module("Workforce"), Contracts("Reporting")),
            ("tests/Alpha.Api.Tests/Alpha.Api.Tests.csproj", api),
            ("tests/Alpha.Api.Tests/Alpha.Api.Tests.csproj", Contracts("Billing")),
            ("tests/Alpha.Api.Tests/Alpha.Api.Tests.csproj", Contracts("IdentityAccess")),
            ("tests/Alpha.Api.Tests/Alpha.Api.Tests.csproj", Contracts("Reporting")),
            ("tests/Alpha.Api.Tests/Alpha.Api.Tests.csproj", Module("Reporting")),
            ("tests/Alpha.Api.Tests/Alpha.Api.Tests.csproj", "tools/Alpha.SchemaVerifier/Alpha.SchemaVerifier.csproj"),
            ("tools/Alpha.SchemaVerifier/Alpha.SchemaVerifier.csproj", infrastructure),
            ("tests/Alpha.Domain.Tests/Alpha.Domain.Tests.csproj", domain)
        }.Select(edge => $"{edge.Item1} -> {edge.Item2}").Order(StringComparer.Ordinal);

        var actual = ProjectReferences().Select(edge => $"{edge.From} -> {edge.To}").Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Project_reference_graph_is_acyclic()
    {
        var graph = ProjectReferences().GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();

        void Visit(string project)
        {
            if (visited.Contains(project)) return;
            Assert.True(visiting.Add(project), $"Circular project reference detected at {project}");
            foreach (var dependency in graph.GetValueOrDefault(project, [])) Visit(dependency);
            visiting.Remove(project);
            visited.Add(project);
        }

        foreach (var project in ProjectFiles().Select(Relative)) Visit(project);
    }

    [Fact]
    public void Business_modules_depend_on_other_modules_only_through_contracts()
    {
        foreach (var (from, to) in ProjectReferences())
        {
            var fromModule = ModuleOf(from);
            var toModule = ModuleOf(to);
            if (fromModule is null || toModule is null || fromModule == toModule) continue;
            Assert.True(to.EndsWith(".Contracts.csproj", StringComparison.Ordinal),
                $"{from} references another module's implementation ({to}). Use that module's Contracts project.");
        }

        // Source-level: no `using`/qualified reference to another module's implementation namespace.
        var moduleNamespace = new Regex(@"\bAlpha\.Modules\.(?<module>[A-Za-z]+)(?<rest>(?:\.[A-Za-z0-9]+)*)", RegexOptions.CultureInvariant);
        foreach (var file in SourceFiles("src/Modules"))
        {
            var owner = ModuleOf(Relative(file))!;
            foreach (Match match in moduleNamespace.Matches(File.ReadAllText(file)))
            {
                var module = match.Groups["module"].Value;
                var isContract = match.Groups["rest"].Value == ".Contracts" ||
                                 match.Groups["rest"].Value.StartsWith(".Contracts.", StringComparison.Ordinal);
                Assert.True(module == owner || isContract,
                    $"{Relative(file)} uses {match.Value}, an implementation namespace of module {module}.");
            }
        }
    }

    [Fact]
    public void Contracts_projects_are_dependency_free()
    {
        var contracts = Directory.EnumerateFiles(Path("src/Modules"), "*.Contracts.csproj", SearchOption.AllDirectories).ToArray();
        Assert.Equal(3, contracts.Length);
        foreach (var project in contracts.Select(XDocument.Load))
        {
            Assert.Empty(project.Descendants("ProjectReference"));
            Assert.Empty(project.Descendants("PackageReference"));
            Assert.Empty(project.Descendants("FrameworkReference"));
        }
    }

    [Fact]
    public void Domain_is_a_dependency_free_model_and_infrastructure_depends_only_on_domain()
    {
        var domain = XDocument.Load(Path("src/Alpha.Domain/Alpha.Domain.csproj"));
        Assert.Empty(domain.Descendants("ProjectReference"));
        Assert.Empty(domain.Descendants("PackageReference"));
        Assert.Empty(domain.Descendants("FrameworkReference"));
        Assert.DoesNotContain(SourceFiles("src/Alpha.Domain").Select(File.ReadAllText), source =>
            source.Contains("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            source.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            source.Contains("Alpha.Infrastructure", StringComparison.Ordinal) ||
            source.Contains("Alpha.Modules", StringComparison.Ordinal));

        Assert.Equal(new[] { "src/Alpha.Domain/Alpha.Domain.csproj" },
            ProjectReferences().Where(e => e.From == "src/Alpha.Infrastructure/Alpha.Infrastructure.csproj").Select(e => e.To));
        Assert.DoesNotContain(SourceFiles("src/Alpha.Infrastructure").Select(File.ReadAllText), source =>
            source.Contains("Alpha.Modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Modules_never_depend_on_the_api_host()
    {
        Assert.DoesNotContain(ProjectReferences(), e => e.From.StartsWith("src/Modules/", StringComparison.Ordinal) &&
            e.To == "src/Alpha.Api/Alpha.Api.csproj");
        Assert.DoesNotContain(SourceFiles("src/Modules").Select(File.ReadAllText), source =>
            Regex.IsMatch(source, @"\bAlpha\.Api\b"));
    }

    [Fact]
    public void Removed_projects_and_abstractions_do_not_reappear()
    {
        foreach (var removed in new[] { "src/Alpha.Application", "src/Alpha.Worker", "src/Modules/Auditing/Alpha.Modules.Auditing.Api" })
            Assert.False(Directory.Exists(Path(removed)) && Directory.EnumerateFiles(Path(removed), "*.csproj").Any(),
                $"{removed} was consolidated and must not be recreated.");

        Assert.DoesNotContain(SourceFiles("src").Select(File.ReadAllText), source =>
            source.Contains("IAlphaDbContext", StringComparison.Ordinal) ||
            source.Contains("Alpha.Application", StringComparison.Ordinal) ||
            source.Contains("Alpha.Worker", StringComparison.Ordinal));
        Assert.DoesNotContain(SourceFiles("src").Select(File.ReadAllText), source =>
            source.Contains(": DbContext", StringComparison.Ordinal) && !source.Contains("class AlphaDbContext", StringComparison.Ordinal));
    }
}
