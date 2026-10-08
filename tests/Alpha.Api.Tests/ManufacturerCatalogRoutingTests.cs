using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ManufacturerCatalogRoutingTests
{
    private static readonly Guid MenoraFund = Guid.NewGuid();
    private static readonly Guid HarelFund = Guid.NewGuid();

    private static ManufacturerCatalogRouting.CatalogEntry Entry(
        string code, string company, string manufacturer, string status = "verified_name") =>
        new() { FundCode = code, CompanyName = company,
            ManufacturerName = manufacturer, MappingStatus = status };

    [Fact]
    public void Same_fund_code_can_route_to_different_manufacturers_by_legal_company()
    {
        var products = new[]
        {
            new ManufacturerCatalogRouting.Product(MenoraFund, "101", "מנורה חברה"),
            new ManufacturerCatalogRouting.Product(HarelFund, "101", "הראל חברה")
        };
        var catalog = new Dictionary<(string Code, string Company), ManufacturerCatalogRouting.CatalogEntry[]>
        {
            [("101", "מנורה חברה")] = [Entry("101", "מנורה חברה", "מנורה")],
            [("101", "הראל חברה")] = [Entry("101", "הראל חברה", "הראל")]
        };
        var routes = ManufacturerCatalogRouting.ResolveProducts(products, "SimulatedVault",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Menora"] = "SimulatedVault-Menora" },
            new Dictionary<string, string> { ["מנורה"] = "Menora" }, catalog);

        Assert.Equal("SimulatedVault-Menora", routes[MenoraFund]);
        Assert.Equal("SimulatedVault", routes[HarelFund]);
    }

    [Fact]
    public void Historical_unverified_and_missing_companies_never_inherit_direct_route()
    {
        var products = new[]
        {
            new ManufacturerCatalogRouting.Product(MenoraFund, "10", "מבטחים ותיקה"),
            new ManufacturerCatalogRouting.Product(HarelFund, "11", "")
        };
        var catalog = new Dictionary<(string Code, string Company), ManufacturerCatalogRouting.CatalogEntry[]>
        {
            [("10", "מבטחים ותיקה")] = [Entry("10", "מבטחים ותיקה", "מנורה", "historical_review")]
        };
        var routes = ManufacturerCatalogRouting.ResolveProducts(products, "SimulatedVault",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Menora"] = "SimulatedVault-Menora" },
            new Dictionary<string, string> { ["מנורה"] = "Menora" }, catalog);

        Assert.All(routes.Values, provider => Assert.Equal("SimulatedVault", provider));
    }

    [Fact]
    public void Specific_exception_takes_precedence_over_parent_manufacturer()
    {
        var catalog = new Dictionary<(string Code, string Company), ManufacturerCatalogRouting.CatalogEntry[]>
        {
            [("665", "מנורה חברה")] = [Entry("665", "מנורה חברה", "מנורה")]
        };
        var product = new ManufacturerCatalogRouting.Product(MenoraFund, "665", "מנורה חברה");
        var routes = ManufacturerCatalogRouting.ResolveProducts([product], "SimulatedVault",
            new Dictionary<string, string> { ["665"] = "OtherExplicitVault" },
            new Dictionary<string, string> { ["Menora"] = "SimulatedVault-Menora" },
            new Dictionary<string, string> { ["מנורה"] = "Menora" }, catalog);
        Assert.Equal("OtherExplicitVault", routes[MenoraFund]);

        // Clearing a legacy code override restores parent-level routing.
        var reset = ManufacturerCatalogRouting.ResolveProducts([product], "SimulatedVault",
            new Dictionary<string, string> { ["665"] = "" },
            new Dictionary<string, string> { ["Menora"] = "SimulatedVault-Menora" },
            new Dictionary<string, string> { ["מנורה"] = "Menora" }, catalog);
        Assert.Equal("SimulatedVault-Menora", reset[MenoraFund]);
    }
}
