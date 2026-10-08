using Alpha.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ManufacturerTransmissionRoutingTests
{
    private static IConfiguration Config(params (string Fund, string Provider)[] routes) =>
        new ConfigurationBuilder().AddInMemoryCollection(routes.ToDictionary(
            entry => $"Reporting:ManufacturerRouting:Funds:{entry.Fund}",
            entry => (string?)entry.Provider)).Build();

    [Fact]
    public void Unconfigured_routing_preserves_the_original_single_provider_behavior()
    {
        var result = ManufacturerTransmissionRouting.Resolve(
            ["100", "200", "100"], "clearinghouse", Config());

        Assert.Equal("clearinghouse", result.Provider);
        Assert.Equal(2, result.Destinations.Count);
    }

    [Fact]
    public void Homogeneous_direct_route_is_selected_without_splitting()
    {
        var result = ManufacturerTransmissionRouting.Resolve(
            ["100", "100"], "clearinghouse", Config(("100", "manufacturer-direct")));

        Assert.Equal("manufacturer-direct", result.Provider);
    }

    [Fact]
    public void Mixed_clearinghouse_and_direct_destinations_are_blocked_before_dispatch()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManufacturerTransmissionRouting.Resolve(
                ["100", "200"], "clearinghouse", Config(("100", "manufacturer-direct"))));

        Assert.Equal("manufacturer_route_split_not_implemented", error.Message);
    }

    [Fact]
    public void Mixed_direct_providers_are_blocked()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManufacturerTransmissionRouting.Resolve(
                ["100", "200"], "clearinghouse",
                Config(("100", "manufacturer-a"), ("200", "manufacturer-b"))));

        Assert.Equal("manufacturer_route_split_not_implemented", error.Message);
    }

    [Fact]
    public void Empty_and_missing_fund_codes_fail_closed()
    {
        Assert.Equal("manufacturer_route_no_products",
            Assert.Throws<InvalidOperationException>(() =>
                ManufacturerTransmissionRouting.Resolve([], "clearinghouse", Config())).Message);
        Assert.Equal("manufacturer_route_fund_code_missing",
            Assert.Throws<InvalidOperationException>(() =>
                ManufacturerTransmissionRouting.Resolve([""], "clearinghouse", Config())).Message);
    }

    [Fact]
    public void Provider_missing_in_explicit_rule_fails_closed()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManufacturerTransmissionRouting.Resolve(
                ["100"], "clearinghouse", Config(("100", ""))));
        Assert.Equal("manufacturer_route_provider_missing", error.Message);
    }
}
