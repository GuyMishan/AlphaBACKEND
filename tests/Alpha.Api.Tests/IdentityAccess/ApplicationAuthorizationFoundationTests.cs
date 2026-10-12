using Xunit;

namespace Alpha.Api.Tests.IdentityAccess;

public sealed class ApplicationAuthorizationFoundationTests
{
    [Fact]
    public void Existing_organization_access_service_is_the_contract_implementation()
    {
        Assert.True(typeof(IOrganizationAuthorization).IsAssignableFrom(typeof(OrganizationAccessService)));

        var expectedCapabilities = new[]
        {
            "CanAccessEmployerAsync",
            "CanAccessOrganizationScopeAsync",
            "CanCreateEmployeeAsync",
            "CanCreateEmployerAsync",
            "CanCreateReportAsync",
            "CanEditEmployeeAsync",
            "CanEditEmployerAsync",
            "CanEditOrganizationGeneralAsync",
            "CanManageEmployerAsync",
            "CanManageEmployerBillingAsync",
            "CanManageOrganizationAsync",
            "CanTransmitReportAsync",
            "CanViewOrganizationAsync",
            "HasFullOrganizationEmployerScopeAsync"
        };
        var actualCapabilities = typeof(IOrganizationAuthorization).GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedCapabilities, actualCapabilities);
    }

    [Fact]
    public void Dependency_injection_aliases_contract_to_the_existing_scoped_service()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Alpha.Api", "Program.cs"));
        var module = File.ReadAllText(Path.Combine(root, "src", "Modules", "IdentityAccess",
            "Alpha.Modules.IdentityAccess", "IdentityAccessModule.cs"));

        Assert.Contains("builder.Services.AddIdentityAccessModule()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddScoped<OrganizationAccessService>", program, StringComparison.Ordinal);
        Assert.Contains("services.AddScoped<OrganizationAccessService>()", module, StringComparison.Ordinal);
        Assert.Contains(
            "services.AddScoped<IOrganizationAuthorization>(provider =>",
            module,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AlphaBackend.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
