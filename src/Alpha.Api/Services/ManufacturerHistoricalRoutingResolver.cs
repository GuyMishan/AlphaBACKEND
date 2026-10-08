using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

/// <summary>
/// A corrective negative/current file must use the same transmission recipient
/// as its original immutable financial movement. Newly introduced products use
/// the current policy, while existing products inherit stored transmission routes.
/// </summary>
public static class ManufacturerHistoricalRoutingResolver
{
    public sealed record Product(Guid Id, string FundCode, string FundCompanyName, Guid? SourceReportProductId);
    public sealed record Assignment(Guid ProductId, string Provider);

    public static async Task<IReadOnlyList<Assignment>> ResolveAsync(
        ManualReport report, IReadOnlyCollection<Product> products,
        IAlphaDbContext db, string defaultProvider, IConfiguration configuration,
        CancellationToken ct)
    {
        var historical = new Dictionary<Guid, string>();
        if (report.IsTechnicalCorrectionDocument && report.CorrectionWorkspaceId.HasValue)
        {
            var workspace = await db.ManualReports.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == report.CorrectionWorkspaceId.Value
                    && item.OrganizationId == report.OrganizationId
                    && item.EmployerId == report.EmployerId, ct);
            if (workspace?.SourceReportId is not Guid sourceReportId)
                throw new InvalidOperationException("manufacturer_route_correction_source_missing");

            var sourceProductIds = await (
                from product in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking()
                    on product.ReportEmployeeId equals employee.Id
                where employee.ReportId == sourceReportId
                select product.Id).ToArrayAsync(ct);
            var originalProducts = sourceProductIds.ToHashSet();
            var originalTransmissions = await db.ReportTransmissions.AsNoTracking()
                .Where(item => item.ReportId == sourceReportId)
                .OrderByDescending(item => item.AttemptNumber).ToListAsync(ct);
            var latestByRoute = originalTransmissions
                .GroupBy(item => string.IsNullOrWhiteSpace(item.RoutingKey)
                    ? "legacy" : item.RoutingKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();

            var originalProviders = new Dictionary<Guid, string>();
            foreach (var original in latestByRoute)
            {
                Guid[] included;
                if (string.IsNullOrWhiteSpace(original.RoutingKey))
                    included = sourceProductIds;
                else
                {
                    try
                    {
                        included = JsonSerializer.Deserialize<Guid[]>(
                            original.RoutedProductIdsJson) ?? [];
                    }
                    catch (JsonException)
                    {
                        throw new InvalidOperationException("manufacturer_route_original_scope_invalid");
                    }
                }
                foreach (var id in included)
                {
                    if (!originalProducts.Contains(id))
                        throw new InvalidOperationException("manufacturer_route_original_scope_invalid");
                    if (originalProviders.TryGetValue(id, out var existing)
                        && !string.Equals(existing, original.Provider, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("manufacturer_route_original_scope_conflict");
                    originalProviders[id] = original.Provider;
                }
            }

            var workspaceProducts = await (
                from product in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking()
                    on product.ReportEmployeeId equals employee.Id
                where employee.ReportId == workspace.Id
                select new { product.Id, product.SourceReportProductId })
                .ToDictionaryAsync(item => item.Id, item => item.SourceReportProductId, ct);

            foreach (var product in products)
            {
                if (!product.SourceReportProductId.HasValue) continue;
                var previous = product.SourceReportProductId.Value;
                if (originalProviders.TryGetValue(previous, out var provider))
                    historical[product.Id] = provider;
                else if (workspaceProducts.TryGetValue(previous, out var ancestor)
                    && ancestor.HasValue && originalProviders.TryGetValue(ancestor.Value, out provider))
                    historical[product.Id] = provider;
            }
        }

        var withoutHistory = products.Where(product => !historical.ContainsKey(product.Id)).ToArray();
        var currentByProduct = await ManufacturerCatalogRouting.ResolveAsync(
            db, withoutHistory.Select(product => new ManufacturerCatalogRouting.Product(
                product.Id, product.FundCode, product.FundCompanyName)).ToArray(),
            defaultProvider, configuration, ct);

        return products.OrderBy(product => product.Id)
            .Select(product => new Assignment(product.Id,
                historical.TryGetValue(product.Id, out var provider)
                    ? provider : currentByProduct[product.Id]))
            .ToArray();
    }
}
