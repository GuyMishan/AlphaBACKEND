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

        group.MapGet("/pension-funds", async (int productType, string? search, int? take, AlphaDbContext db, CancellationToken ct) =>
        {
            var normalizedType = productType switch
            {
                1 => "קרן פנסיה",
                2 => "קרן השתלמות",
                3 => "ביטוח מנהלים / פוליסה",
                4 => "קופת גמל",
                _ => null
            };

            if (normalizedType is null)
                return Results.Ok(Array.Empty<object>());

            var limit = Math.Clamp(take ?? 500, 1, 1000);
            var result = new List<object>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"""
                SELECT DISTINCT ON (COALESCE(NULLIF(fund_code, ''), fund_name), fund_name)
                       external_key, fund_code, fund_name, company_name, domain, product_type
                FROM reference_data.pension_products
                WHERE is_active = true
                  AND product_type = @product_type
                  AND (@search IS NULL
                       OR fund_name ILIKE '%' || @search || '%'
                       OR company_name ILIKE '%' || @search || '%'
                       OR fund_code ILIKE '%' || @search || '%')
                ORDER BY COALESCE(NULLIF(fund_code, ''), fund_name), fund_name, company_name NULLS LAST, external_key
                LIMIT {limit}
                """;

            var productTypeParameter = command.CreateParameter();
            productTypeParameter.ParameterName = "product_type";
            productTypeParameter.Value = normalizedType;
            command.Parameters.Add(productTypeParameter);

            var searchParameter = command.CreateParameter();
            searchParameter.ParameterName = "search";
            searchParameter.Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim();
            command.Parameters.Add(searchParameter);

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
                    domain = reader.GetString(4),
                    productType = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                });
            }

            return Results.Ok(result);
        });

        return endpoints;
    }
}
