using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class PublicReferenceDataEndpoints
{
    public static IEndpointRouteBuilder MapPublicReferenceDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/reference-data")
            .RequireAuthorization()
            .WithTags("Reference Data");

        group.MapGet("/options", async (string category, string? scope, AlphaDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(category)) return Results.BadRequest(new { error = "category is required." });
            var normalizedCategory = category.Trim().ToLowerInvariant();
            var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
            var result = new List<object>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT value, label, scope
                FROM reference_data.select_options
                WHERE category = @category
                  AND is_active = true
                  AND (scope = 'all' OR scope = @scope)
                ORDER BY CASE WHEN scope = @scope THEN 0 ELSE 1 END, sort_order, value
                """;
            AddParameter(command, "category", normalizedCategory);
            AddParameter(command, "scope", normalizedScope);
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new { value = reader.GetString(0), label = reader.GetString(1), scope = reader.GetString(2) });
            return Results.Ok(result);
        });

        group.MapGet("/salary-layers", async (AlphaDbContext db, CancellationToken ct) =>
        {
            var result = new List<object>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT code, name
                FROM reference_data.salary_layers
                WHERE is_active = true
                ORDER BY sort_order, code
                """;
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new { code = reader.GetInt32(0), name = reader.GetString(1) });
            return Results.Ok(result);
        });

        group.MapGet("/employer-interface-006/options", async (string category, string? scope, AlphaDbContext db, CancellationToken ct) =>
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
            while (await reader.ReadAsync(ct))
                result.Add(new { code = reader.GetInt32(0), name = reader.GetString(1), scope = reader.GetString(2) });
            return Results.Ok(result);
        });

        group.MapGet("/pension-funds", async (int productType, string? search, int? take, AlphaDbContext db, CancellationToken ct) =>
        {
            var normalizedType = await NormalizeProductTypeAsync(productType.ToString(), db, ct);
            return normalizedType is null
                ? Results.Ok(Array.Empty<object>())
                : await QueryPensionProductsAsync(normalizedType, search, take, db, ct);
        });

        group.MapGet("/pension-products", async (string productType, string? search, int? take, AlphaDbContext db, CancellationToken ct) =>
        {
            var normalizedType = await NormalizeProductTypeAsync(productType, db, ct);
            return normalizedType is null
                ? Results.Ok(Array.Empty<object>())
                : await QueryPensionProductsAsync(normalizedType, search, take, db, ct);
        });

        return endpoints;
    }

    private static async Task<string?> NormalizeProductTypeAsync(string? productType, AlphaDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productType)) return null;
        var value = productType.Trim();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT label
            FROM reference_data.select_options
            WHERE category = 'pension-product-type'
              AND scope = 'all'
              AND is_active = true
              AND (value = @value OR lower(label) = lower(@value))
            LIMIT 1
            """;
        AddParameter(command, "value", value);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not string label) return null;
        return label switch
        {
            "ביטוח מנהלים" => "ביטוח מנהלים / פוליסה",
            _ => label
        };
    }

    private static async Task<IResult> QueryPensionProductsAsync(string normalizedType, string? search, int? take,
        AlphaDbContext db, CancellationToken ct)
    {
        var limit = Math.Clamp(take ?? 30, 1, 100);
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var result = new List<object>();

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT ON (COALESCE(NULLIF(fund_code, ''), fund_name), fund_name)
                   external_key, fund_code, fund_name, company_name, domain, product_type
            FROM reference_data.pension_products
            WHERE is_active = true
              AND product_type = @product_type
              {(hasSearch ? "AND (fund_name ILIKE '%' || @search || '%' OR company_name ILIKE '%' || @search || '%' OR fund_code ILIKE '%' || @search || '%' OR external_key ILIKE '%' || @search || '%')" : string.Empty)}
            ORDER BY COALESCE(NULLIF(fund_code, ''), fund_name), fund_name, company_name NULLS LAST, external_key
            LIMIT {limit}
            """;

        AddParameter(command, "product_type", normalizedType);
        if (hasSearch) AddParameter(command, "search", search!.Trim());

        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new
            {
                externalKey = reader.GetString(0),
                fundCode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                fundName = reader.GetString(2),
                companyName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                domain = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                productType = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
            });
        }

        return Results.Ok(result);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
