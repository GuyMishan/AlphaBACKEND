using System.Text.RegularExpressions;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.ReferenceData.Features.Administration;

/// <summary>Editable, disabled-by-default message templates. No sends occur here.</summary>
public static class ManufacturerEmailTemplateEndpoints
{
    private static readonly HashSet<string> AllowedTokens = new(StringComparer.Ordinal)
    {
        "errorCode", "errorDescription", "manufacturerName", "productName",
        "employerName", "reportMonth", "reportReference", "paymentReference"
    };
    private static readonly Regex TokenPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    public sealed record TemplateUpdate(string? Subject, string? Body, bool IsEnabled);

    public static RouteGroupBuilder MapManufacturerEmailTemplateEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/manufacturer-email-templates", async (
            string? search, int? take, AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var result = new List<object>();
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = """
                SELECT codes.code, codes.description, COALESCE(t.subject, ''),
                       COALESCE(t.body, ''), COALESCE(t.is_enabled, false)
                FROM reference_data.employer_interface_006_error_codes codes
                LEFT JOIN reference_data.manufacturer_error_email_templates t ON t.error_code = codes.code
                WHERE codes.is_active = true AND
                      (@search = '' OR codes.description ILIKE '%' || @search || '%'
                          OR codes.code::text = @search)
                ORDER BY codes.code LIMIT @take
                """;
            Add(cmd, "search", (search ?? "").Trim());
            Add(cmd, "take", Math.Clamp(take ?? 150, 1, 250));
            if (cmd.Connection!.State != System.Data.ConnectionState.Open) await cmd.Connection.OpenAsync(ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new { errorCode = reader.GetInt32(0), description = reader.GetString(1),
                    subject = reader.GetString(2), body = reader.GetString(3), isEnabled = reader.GetBoolean(4) });
            return Results.Ok(new { items = result, allowedTokens = AllowedTokens.OrderBy(x => x).ToArray() });
        });

        group.MapPut("/manufacturer-email-templates/{errorCode:int}", async (
            int errorCode, TemplateUpdate request, AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var subject = request.Subject?.Trim() ?? "";
            var body = request.Body?.Trim() ?? "";
            if (subject.Length > 200 || body.Length > 8000 ||
                (request.IsEnabled && (subject.Length == 0 || body.Length == 0)))
                return Results.BadRequest(new { error = "invalid_email_template" });
            var unknown = TokenPattern.Matches(subject + "\n" + body)
                .Select(match => match.Groups[1].Value).Where(token => !AllowedTokens.Contains(token))
                .Distinct().ToArray();
            if (unknown.Length > 0 || subject.Contains('\r') || subject.Contains('\n'))
                return Results.BadRequest(new { error = "invalid_email_template_token" });
            if ((subject.Count(ch => ch == '{') != subject.Count(ch => ch == '}')) ||
                (body.Count(ch => ch == '{') != body.Count(ch => ch == '}')))
                return Results.BadRequest(new { error = "invalid_email_template_braces" });

            var exists = await db.Database.SqlQueryRaw<int>(
                "SELECT 1 AS \"Value\" FROM reference_data.employer_interface_006_error_codes WHERE code = {0} AND is_active = true LIMIT 1",
                errorCode).AnyAsync(ct);
            if (!exists) return Results.NotFound();

            if (subject.Length == 0 && body.Length == 0 && !request.IsEnabled)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM reference_data.manufacturer_error_email_templates WHERE error_code = {errorCode}", ct);
            else if (subject.Length == 0 || body.Length == 0)
                return Results.BadRequest(new { error = "invalid_email_template" });
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO reference_data.manufacturer_error_email_templates
                        (error_code, subject, body, is_enabled, updated_by)
                    VALUES ({errorCode}, {subject}, {body}, {request.IsEnabled}, {user.UserId})
                    ON CONFLICT (error_code) DO UPDATE
                    SET subject = EXCLUDED.subject, body = EXCLUDED.body,
                        is_enabled = EXCLUDED.is_enabled, updated_by = EXCLUDED.updated_by, updated_at = now()
                    """, ct);
            return Results.Ok(new { errorCode, subject, body, isEnabled = request.IsEnabled });
        });
        return group;
    }

    private static void Add(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
