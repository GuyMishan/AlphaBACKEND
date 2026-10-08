using Alpha.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

/// <summary>
/// Resolves the operational destination using an exact (fund code, managing
/// legal company) match against the reviewed manufacturer-parent catalog.
/// A fund code alone is not globally unique in the pension catalog.
/// </summary>
public static class ManufacturerCatalogRouting
{
    public sealed class CatalogEntry
    {
        public string FundCode { get; set; } = "";
        public string CompanyName { get; set; } = "";
        public string ManufacturerName { get; set; } = "";
        public string MappingStatus { get; set; } = "";
    }

    public sealed record Product(Guid Id, string FundCode, string FundCompanyName);

    public static async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        IAlphaDbContext db, IReadOnlyCollection<Product> products,
        string defaultProvider, IConfiguration configuration, CancellationToken ct)
    {
        if (products.Count == 0) return new Dictionary<Guid, string>();
        var explicitFunds = configuration.GetSection("Reporting:ManufacturerRouting:Funds")
            .GetChildren().ToDictionary(item => item.Key.Trim(),
                item => item.Value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
        var manufacturerProviders = configuration.GetSection("Reporting:ManufacturerRouting:Manufacturers")
            .GetChildren().ToDictionary(item => item.Key.Trim(),
                item => item.Value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
        // ASCII configuration keys (suitable for environment variables) can
        // map to Hebrew parent labels from the catalog.
        var aliases = configuration.GetSection("Reporting:ManufacturerRouting:Aliases")
            .GetChildren().ToDictionary(item => item.Value?.Trim() ?? "",
                item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase);
        var byIdentity = new Dictionary<(string Code, string Company), CatalogEntry[]>();
        if (manufacturerProviders.Any(entry => !string.IsNullOrWhiteSpace(entry.Value)))
        {
            var dbContext = db as DbContext
                ?? throw new InvalidOperationException("manufacturer_route_database_context_unavailable");
            // One catalog read for the report. A bad/missing reference table
            // cannot silently redirect financial data to the wrong provider.
            var catalog = await dbContext.Database.SqlQueryRaw<CatalogEntry>(
                """
                SELECT DISTINCT p.fund_code AS "FundCode",
                       p.company_name AS "CompanyName",
                       g.manufacturer_name AS "ManufacturerName",
                       g.mapping_status AS "MappingStatus"
                FROM reference_data.pension_products p
                JOIN reference_data.manufacturer_company_groups g
                  ON g.company_name = p.company_name
                WHERE p.fund_code IS NOT NULL
                """).ToListAsync(ct);
            byIdentity = catalog.GroupBy(row =>
                (Code: row.FundCode.Trim(), Company: row.CompanyName.Trim()))
                .ToDictionary(group => group.Key, group => group.ToArray());
        }
        var routes = new Dictionary<Guid, string>();
        foreach (var product in products)
        {
            var code = product.FundCode?.Trim() ?? "";
            var company = product.FundCompanyName?.Trim() ?? "";
            var provider = defaultProvider;

            // Deliberate per-fund exception takes precedence; an empty value
            // explicitly disables a formerly configured exception.
            if (explicitFunds.TryGetValue(code, out var exception)
                && !string.IsNullOrWhiteSpace(exception))
            {
                provider = exception;
            }
            else if (manufacturerProviders.Count > 0
                && byIdentity.TryGetValue((code, company), out var matches))
            {
                var verified = matches.Where(item =>
                    string.Equals(item.MappingStatus, "verified_name", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ManufacturerName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (verified.Length > 1)
                    throw new InvalidOperationException("manufacturer_route_catalog_ambiguous");
                if (verified.Length == 1)
                {
                    var key = aliases.TryGetValue(verified[0], out var alias)
                        ? alias : verified[0];
                    if (manufacturerProviders.TryGetValue(key, out var destination)
                        && !string.IsNullOrWhiteSpace(destination))
                        provider = destination;
                }
                // Historical/unknown legal entities do not inherit a brand
                // destination until operational ownership is explicitly reviewed.
            }
            if (string.IsNullOrWhiteSpace(provider))
                throw new InvalidOperationException("manufacturer_route_provider_missing");
            routes[product.Id] = provider;
        }
        return routes;
    }
}
