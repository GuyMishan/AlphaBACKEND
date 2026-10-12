using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Reporting.Features.EmployerInterface006;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Reporting.Features.PublicReferenceData;

public static class ReportingReferenceDataEndpoints
{
    public static RouteGroupBuilder MapReportingReferenceDataEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/employer-interface-006/options-bundle", async (string categories, AlphaDbContext db, CancellationToken ct) =>
        {
            var requested = categories
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Take(25)
                .ToArray();
            if (requested.Length == 0)
                return Results.BadRequest(new { error = "categories is required." });

            var result = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var category in requested)
                result[category] = [];

            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT category, code, name, scope
                FROM reference_data.employer_interface_006_options
                WHERE is_active = true
                  AND category = ANY(@categories)
                ORDER BY category, CASE WHEN scope = 'all' THEN 0 ELSE 1 END, scope, sort_order, code
                """;
            AddParameter(command, "categories", requested);
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var category = reader.GetString(0);
                if (!result.TryGetValue(category, out var items)) continue;
                items.Add(new
                {
                    code = reader.GetInt32(1),
                    name = reader.GetString(2),
                    scope = reader.GetString(3)
                });
            }

            return Results.Ok(result);
        });

        group.MapGet("/employer-interface-006/options", async (string category, string? scope, int? operationCode, AlphaDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(category)) return Results.BadRequest(new { error = "category is required." });
            var normalizedCategory = category.Trim().ToLowerInvariant();
            var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
            var result = new List<object>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT code, name, scope
                FROM reference_data.employer_interface_006_options
                WHERE category = @category
                  AND is_active = true
                  AND (scope = 'all' OR scope = @scope)
                ORDER BY CASE WHEN scope = @scope THEN 0 ELSE 1 END, sort_order, code
                """;
            AddParameter(command, "category", normalizedCategory);
            AddParameter(command, "scope", normalizedScope);
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            IReadOnlyCollection<int>? allowedPaymentMethods = null;
            if (normalizedCategory == "payment-method" && operationCode.HasValue)
                allowedPaymentMethods = EmployerInterface006WorkbookRules.AllowedPaymentMethods(operationCode.Value);

            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetInt32(0);
                if (allowedPaymentMethods is not null && !allowedPaymentMethods.Contains(code)) continue;
                result.Add(new { code, name = reader.GetString(1), scope = reader.GetString(2) });
            }
            return Results.Ok(result);
        });

        group.MapGet("/pension-editor-options", async (AlphaDbContext db, CancellationToken ct) =>
        {
            var referenceCategories = new[]
            {
                "product-active-status",
                "pension-product-type",
                "salary-allocation-type"
            };
            var interfaceCategories = new[]
            {
                "receipt-type",
                "section14-code"
            };

            var referenceOptions = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var category in referenceCategories)
                referenceOptions[category] = [];

            var interfaceOptions = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var category in interfaceCategories)
                interfaceOptions[category] = [];

            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT category, value, label, scope
                FROM reference_data.select_options
                WHERE is_active = true
                  AND scope = 'all'
                  AND category = ANY(@reference_categories)
                ORDER BY category, sort_order, value;

                SELECT category, code, name, scope
                FROM reference_data.employer_interface_006_options
                WHERE is_active = true
                  AND scope = 'all'
                  AND category = ANY(@interface_categories)
                ORDER BY category, sort_order, code;

                SELECT code, name
                FROM reference_data.salary_layers
                WHERE is_active = true
                ORDER BY sort_order, code;
                """;
            AddParameter(command, "reference_categories", referenceCategories);
            AddParameter(command, "interface_categories", interfaceCategories);

            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(ct);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var category = reader.GetString(0);
                if (!referenceOptions.TryGetValue(category, out var items)) continue;
                items.Add(new
                {
                    value = reader.GetString(1),
                    label = reader.GetString(2),
                    scope = reader.GetString(3)
                });
            }

            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var category = reader.GetString(0);
                if (!interfaceOptions.TryGetValue(category, out var items)) continue;
                items.Add(new
                {
                    code = reader.GetInt32(1),
                    name = reader.GetString(2),
                    scope = reader.GetString(3)
                });
            }

            var salaryLayers = new List<object>();
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
                salaryLayers.Add(new { code = reader.GetInt32(0), name = reader.GetString(1) });
            await reader.DisposeAsync();

            // These enums are stored as strings. Cast only after materialization so EF does not
            // emit invalid PostgreSQL casts such as CAST('PensionFund' AS integer).
            var contributionLimitRows = await db.ContributionPercentageLimits.AsNoTracking()
                .OrderBy(x => x.Year).ThenBy(x => x.ProductType).ThenBy(x => x.Party).ThenBy(x => x.Component)
                .ToListAsync(ct);
            var contributionLimits = contributionLimitRows.Select(x => new
            {
                year = x.Year,
                productType = (int)x.ProductType,
                party = (int)x.Party,
                component = (int)x.Component,
                maxPercentage = x.MaxPercentage
            }).ToArray();

            return Results.Ok(new { referenceOptions, interfaceOptions, salaryLayers, contributionLimits });
        });

        return group;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
