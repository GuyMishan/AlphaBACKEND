using System.Text.Json;
using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReferenceDataEndpoints
{
    public static IEndpointRouteBuilder MapReferenceDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/reference-data")
            .RequireAuthorization()
            .WithTags("Platform Reference Data");

        group.MapGet("/interfaces", async (AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();

            var items = new List<object>();
            foreach (var integration in ReferenceDataSyncService.Integrations)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                    SELECT id, status, started_at, finished_at, records_received, records_inserted, records_updated,
                           records_deactivated, error_message
                    FROM reference_data.sync_runs
                    WHERE integration_key = @key
                    ORDER BY started_at DESC
                    LIMIT 1
                    """;
                var p = command.CreateParameter();
                p.ParameterName = "key";
                p.Value = integration.Key;
                command.Parameters.Add(p);
                if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
                await using var reader = await command.ExecuteReaderAsync(ct);
                object? lastRun = null;
                if (await reader.ReadAsync(ct))
                {
                    lastRun = new
                    {
                        id = reader.GetGuid(0),
                        status = reader.GetString(1),
                        startedAt = reader.GetFieldValue<DateTimeOffset>(2),
                        finishedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3),
                        recordsReceived = reader.GetInt32(4),
                        recordsInserted = reader.GetInt32(5),
                        recordsUpdated = reader.GetInt32(6),
                        recordsDeactivated = reader.GetInt32(7),
                        errorMessage = reader.IsDBNull(8) ? null : reader.GetString(8)
                    };
                }
                items.Add(new { key = integration.Key, name = integration.Value, lastRun });
            }
            return Results.Ok(items);
        });

        group.MapGet("/interfaces/{key}/runs", async (string key, int? take, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            if (!ReferenceDataSyncService.Integrations.ContainsKey(key)) return Results.NotFound();
            var limit = Math.Clamp(take ?? 20, 1, 100);
            var result = new List<object>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"""
                SELECT id, integration_key, integration_name, status, started_at, finished_at, records_received,
                       records_inserted, records_updated, records_deactivated, error_message, details_json
                FROM reference_data.sync_runs
                WHERE integration_key = @key
                ORDER BY started_at DESC
                LIMIT {limit}
                """;
            var p = command.CreateParameter();
            p.ParameterName = "key";
            p.Value = key;
            command.Parameters.Add(p);
            if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(ReadRun(reader));
            return Results.Ok(result);
        });

        group.MapGet("/runs/{id:guid}", async (Guid id, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT id, integration_key, integration_name, status, started_at, finished_at, records_received,
                       records_inserted, records_updated, records_deactivated, error_message, details_json
                FROM reference_data.sync_runs WHERE id = @id
                """;
            var p = command.CreateParameter();
            p.ParameterName = "id";
            p.Value = id;
            command.Parameters.Add(p);
            if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Results.Ok(ReadRun(reader)) : Results.NotFound();
        });

        group.MapPost("/interfaces/{key}/run", async (string key, ReferenceDataSyncService sync, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            if (!ReferenceDataSyncService.Integrations.ContainsKey(key)) return Results.NotFound();
            var result = await sync.RunAsync(key, currentUser.UserId, ct);
            return result.Status == "Success" ? Results.Ok(result) : Results.Json(result, statusCode: 502);
        });

        group.MapGet("/pension-products", async (string? productType, string? search, int? take, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            var limit = Math.Clamp(take ?? 100, 1, 500);
            var result = new List<object>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"""
                SELECT external_key, domain, product_type, fund_code, fund_name, short_name, company_name,
                       investment_track_code, investment_track_name, classification, is_active
                FROM reference_data.pension_products
                WHERE (@product_type IS NULL OR product_type = @product_type)
                  AND (@search IS NULL OR fund_name ILIKE '%' || @search || '%' OR company_name ILIKE '%' || @search || '%' OR fund_code ILIKE '%' || @search || '%')
                ORDER BY product_type, company_name, fund_name
                LIMIT {limit}
                """;
            foreach (var (name, value) in new[] { ("product_type", productType), ("search", search) })
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
                command.Parameters.Add(p);
            }
            if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new
                {
                    externalKey = reader.GetString(0),
                    domain = reader.GetString(1),
                    productType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    fundCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                    fundName = reader.GetString(4),
                    shortName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    companyName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    investmentTrackCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                    investmentTrackName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    classification = reader.IsDBNull(9) ? null : reader.GetString(9),
                    isActive = reader.GetBoolean(10)
                });
            }
            return Results.Ok(result);
        });

        return endpoints;
    }

    private static object ReadRun(System.Data.Common.DbDataReader reader)
    {
        object? details = null;
        if (!reader.IsDBNull(11))
        {
            using var doc = JsonDocument.Parse(reader.GetString(11));
            details = doc.RootElement.Clone();
        }
        return new
        {
            id = reader.GetGuid(0),
            integrationKey = reader.GetString(1),
            integrationName = reader.GetString(2),
            status = reader.GetString(3),
            startedAt = reader.GetFieldValue<DateTimeOffset>(4),
            finishedAt = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5),
            recordsReceived = reader.GetInt32(6),
            recordsInserted = reader.GetInt32(7),
            recordsUpdated = reader.GetInt32(8),
            recordsDeactivated = reader.GetInt32(9),
            errorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
            details
        };
    }
}
