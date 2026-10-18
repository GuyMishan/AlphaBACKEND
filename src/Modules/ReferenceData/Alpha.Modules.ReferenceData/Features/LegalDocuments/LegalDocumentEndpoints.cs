using System.Data;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Alpha.Modules.ReferenceData.Features.LegalDocuments;

/// <summary>One platform-wide legal document with versioned Markdown and immutable published text.</summary>
public static class LegalDocumentEndpoints
{
    public static IEndpointRouteBuilder MapLegalDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var publicGroup = endpoints.MapGroup("/api/legal-document").WithTags("Legal Document");
        publicGroup.MapGet("/active", async (AlphaDbContext db, CancellationToken ct) =>
        {
            var versions = await ReadAsync(db, "status = 'published' AND effective_at <= now()", ct);
            return versions.Count == 0 ? Results.NotFound() : Results.Ok(versions[0]);
        }).AllowAnonymous();
        publicGroup.MapGet("/versions/{version:int}", async (int version, AlphaDbContext db, CancellationToken ct) =>
        {
            var versions = await ReadAsync(db, "version = @version AND status IN ('published', 'archived')", ct, version);
            return versions.Count == 0 ? Results.NotFound() : Results.Ok(versions[0]);
        }).AllowAnonymous();

        var admin = endpoints.MapGroup("/api/platform/legal-document").RequireAuthorization().WithTags("Platform Legal Document");
        admin.MapGet("/versions", async (ICurrentUser user, AlphaDbContext db, CancellationToken ct) =>
            !user.IsPlatformAdmin ? Results.Forbid() : Results.Ok(await ReadAsync(db, "TRUE", ct)));
        admin.MapPost("/draft", async (DraftRequest request, ICurrentUser user, AlphaDbContext db, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            if (!Valid(request.Title, request.Markdown)) return Results.BadRequest(new { error = "invalid_legal_document" });
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(74123, 1)", connection, transaction))
                await lockCommand.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO reference_data.legal_document_versions
                    (id, version, title, markdown, status, requires_reacceptance, created_at)
                SELECT @id, COALESCE(MAX(version), 0) + 1, @title, @markdown, 'draft', false, now()
                FROM reference_data.legal_document_versions
                WHERE NOT EXISTS (SELECT 1 FROM reference_data.legal_document_versions WHERE status = 'draft')
                RETURNING version
                """, connection, transaction);
            var id = Guid.NewGuid();
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("title", request.Title.Trim());
            cmd.Parameters.AddWithValue("markdown", request.Markdown);
            var version = await cmd.ExecuteScalarAsync(ct);
            if (version is null) return Results.Conflict(new { error = "legal_draft_exists" });
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/platform/legal-document/versions/{version}", new { id, version });
        });
        admin.MapPut("/draft", async (DraftRequest request, ICurrentUser user, AlphaDbContext db, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            if (!Valid(request.Title, request.Markdown)) return Results.BadRequest(new { error = "invalid_legal_document" });
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reference_data.legal_document_versions
                SET title = {request.Title.Trim()}, markdown = {request.Markdown}
                WHERE status = 'draft'
                """, ct);
            return updated == 0 ? Results.NotFound() : Results.NoContent();
        });
        admin.MapPost("/draft/publish", async (PublishRequest request, ICurrentUser user, AlphaDbContext db, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            if (request.EffectiveAt < now.AddMinutes(-1)) return Results.BadRequest(new { error = "invalid_effective_date" });
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reference_data.legal_document_versions
                SET status = 'published', published_at = {now}, effective_at = {request.EffectiveAt},
                    requires_reacceptance = {request.RequiresReacceptance}
                WHERE status = 'draft'
                """, ct);
            return updated == 0 ? Results.NotFound() : Results.NoContent();
        });
        admin.MapPost("/versions/{version:int}/archive", async (int version, ICurrentUser user, AlphaDbContext db, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reference_data.legal_document_versions SET status = 'archived', archived_at = {DateTimeOffset.UtcNow}
                WHERE version = {version} AND status = 'published'
                AND EXISTS (SELECT 1 FROM reference_data.legal_document_versions newer
                            WHERE newer.status = 'published' AND newer.version > {version} AND newer.effective_at <= now())
                """, ct);
            return updated == 0 ? Results.Conflict(new { error = "cannot_archive_active_version" }) : Results.NoContent();
        });
        return endpoints;
    }

    private static bool Valid(string? title, string? markdown) =>
        !string.IsNullOrWhiteSpace(title) && title.Length <= 200 &&
        !string.IsNullOrWhiteSpace(markdown) && markdown.Length <= 200_000 &&
        !markdown.Contains('<') && !markdown.Contains('>') &&
        !markdown.Contains("javascript:", StringComparison.OrdinalIgnoreCase) &&
        !markdown.Contains("data:", StringComparison.OrdinalIgnoreCase);

    private static async Task<List<LegalDocumentResponse>> ReadAsync(AlphaDbContext db, string predicate, CancellationToken ct, int? version = null)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT id, version, title, markdown, status, requires_reacceptance, created_at, published_at, effective_at, archived_at
            FROM reference_data.legal_document_versions WHERE {predicate}
            ORDER BY version DESC
            """, connection);
        if (version.HasValue) cmd.Parameters.AddWithValue("version", version.Value);
        var results = new List<LegalDocumentResponse>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new LegalDocumentResponse(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
        return results;
    }

    public sealed record DraftRequest(string Title, string Markdown);
    public sealed record PublishRequest(DateTimeOffset EffectiveAt, bool RequiresReacceptance);
    public sealed record LegalDocumentResponse(Guid Id, int Version, string Title, string Markdown, string Status,
        bool RequiresReacceptance, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt,
        DateTimeOffset? EffectiveAt, DateTimeOffset? ArchivedAt);
}
