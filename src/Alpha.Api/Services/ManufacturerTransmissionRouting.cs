using Microsoft.Extensions.Configuration;

namespace Alpha.Api.Services;

/// <summary>
/// Immutable preflight routing plan. Does not dispatch or split financial files:
/// mixed destinations are blocked until per-destination 006 packages and feedback
/// correlation can be generated safely.
/// </summary>
public static class ManufacturerTransmissionRouting
{
    public sealed record Destination(string FundCode, string Provider);
    public sealed record Plan(string Provider, IReadOnlyList<Destination> Destinations);

    public static IReadOnlyList<Destination> Destinations(
        IEnumerable<string> fundCodes,
        string defaultProvider,
        IConfiguration configuration)
    {
        var rules = configuration.GetSection("Reporting:ManufacturerRouting:Funds")
            .GetChildren().ToDictionary(x => x.Key.Trim(), x => x.Value?.Trim() ?? "",
                StringComparer.OrdinalIgnoreCase);

        return fundCodes
            .Select(code => code?.Trim() ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code =>
            {
                if (string.IsNullOrWhiteSpace(code))
                    throw new InvalidOperationException("manufacturer_route_fund_code_missing");
                var provider = rules.TryGetValue(code, out var configured)
                    ? configured : defaultProvider;
                if (string.IsNullOrWhiteSpace(provider))
                    throw new InvalidOperationException("manufacturer_route_provider_missing");
                return new Destination(code, provider);
            }).ToArray();
    }

    public static Plan Resolve(
        IEnumerable<string> fundCodes,
        string defaultProvider,
        IConfiguration configuration)
    {
        var destinations = Destinations(fundCodes, defaultProvider, configuration);
        if (destinations.Count == 0)
            throw new InvalidOperationException("manufacturer_route_no_products");
        var providers = destinations.Select(x => x.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (providers.Length != 1)
            throw new InvalidOperationException("manufacturer_route_split_not_implemented");
        return new Plan(providers[0], destinations);
    }
}
