using System.Net.Mail;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.ReferenceData.Features.Administration;

/// <summary>Platform-maintained delivery destinations; never sends messages.</summary>
public static class ManufacturerContactEndpoints
{
    public sealed record ContactUpdate(string? Email, bool IsVerified);

    public static RouteGroupBuilder MapManufacturerContactEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/manufacturer-contacts", async (AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var items = new List<object>();
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = """
                SELECT g.manufacturer_name, c.email, COALESCE(c.is_verified, false),
                       COUNT(DISTINCT g.company_name)::integer
                FROM reference_data.manufacturer_company_groups g
                LEFT JOIN reference_data.manufacturer_contact_emails c
                  ON c.manufacturer_name = g.manufacturer_name
                WHERE g.mapping_status = 'verified_name'
                GROUP BY g.manufacturer_name, c.email, c.is_verified
                ORDER BY g.manufacturer_name
                """;
            if (cmd.Connection!.State != System.Data.ConnectionState.Open) await cmd.Connection.OpenAsync(ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(new { manufacturerName = reader.GetString(0),
                    email = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    isVerified = reader.GetBoolean(2), companyCount = reader.GetInt32(3) });
            return Results.Ok(items);
        });

        group.MapPut("/manufacturer-contacts/{manufacturerName}", async (
            string manufacturerName, ContactUpdate request, AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var name = manufacturerName.Trim();
            if (name.Length is < 1 or > 300) return Results.BadRequest(new { error = "invalid_manufacturer" });
            var email = request.Email?.Trim() ?? "";
            if (!ValidEmail(email)) return Results.BadRequest(new { error = "invalid_contact_email" });
            var exists = await db.Database.SqlQueryRaw<int>(
                "SELECT 1 AS \"Value\" FROM reference_data.manufacturer_company_groups WHERE manufacturer_name = {0} AND mapping_status = 'verified_name' LIMIT 1",
                name).AnyAsync(ct);
            if (!exists) return Results.NotFound();
            if (email.Length == 0)
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM reference_data.manufacturer_contact_emails WHERE manufacturer_name = {name}", ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO reference_data.manufacturer_contact_emails (manufacturer_name, email, is_verified, updated_by)
                    VALUES ({name}, {email}, {request.IsVerified}, {user.UserId})
                    ON CONFLICT (manufacturer_name) DO UPDATE
                    SET email = EXCLUDED.email, is_verified = EXCLUDED.is_verified,
                        updated_by = EXCLUDED.updated_by, updated_at = now()
                    """, ct);
            return Results.Ok(new { manufacturerName = name, email, isVerified = email.Length > 0 && request.IsVerified });
        });

        group.MapGet("/product-contact-overrides", async (
            string? search, int? take, AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var items = new List<object>();
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = """
                SELECT p.external_key, p.fund_name, COALESCE(p.company_name, ''),
                       COALESCE(g.manufacturer_name, ''), COALESCE(o.email, ''),
                       COALESCE(o.is_verified, false), COALESCE(m.email, ''),
                       COALESCE(m.is_verified, false)
                FROM reference_data.pension_products p
                LEFT JOIN reference_data.manufacturer_company_groups g
                    ON g.company_name = p.company_name AND g.mapping_status = 'verified_name'
                LEFT JOIN reference_data.manufacturer_contact_emails m
                    ON m.manufacturer_name = g.manufacturer_name
                LEFT JOIN reference_data.product_contact_email_overrides o
                    ON o.external_key = p.external_key
                WHERE (@search = '' OR p.fund_name ILIKE '%' || @search || '%'
                    OR p.company_name ILIKE '%' || @search || '%'
                    OR p.external_key ILIKE '%' || @search || '%')
                ORDER BY p.fund_name, p.external_key
                LIMIT @take
                """;
            Add(cmd, "search", (search ?? "").Trim());
            Add(cmd, "take", Math.Clamp(take ?? 100, 1, 300));
            if (cmd.Connection!.State != System.Data.ConnectionState.Open) await cmd.Connection.OpenAsync(ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var overrideEmail = reader.GetString(4);
                var overrideVerified = reader.GetBoolean(5);
                var manufacturerEmail = reader.GetString(6);
                var manufacturerVerified = reader.GetBoolean(7);
                items.Add(new {
                    externalKey = reader.GetString(0), fundName = reader.GetString(1),
                    companyName = reader.GetString(2), manufacturerName = reader.GetString(3),
                    overrideEmail, overrideVerified, manufacturerEmail, manufacturerVerified,
                    effectiveEmail = overrideVerified ? overrideEmail : manufacturerVerified ? manufacturerEmail : "",
                    source = overrideVerified ? "product" : manufacturerVerified ? "manufacturer" : "none"
                });
            }
            return Results.Ok(items);
        });

        group.MapPut("/product-contact-overrides/{externalKey}", async (
            string externalKey, ContactUpdate request, AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var key = externalKey.Trim();
            if (key.Length is < 1 or > 500) return Results.BadRequest(new { error = "invalid_product" });
            var email = request.Email?.Trim() ?? "";
            if (!ValidEmail(email)) return Results.BadRequest(new { error = "invalid_contact_email" });
            var exists = await db.Database.SqlQueryRaw<int>(
                "SELECT 1 AS \"Value\" FROM reference_data.pension_products WHERE external_key = {0} LIMIT 1",
                key).AnyAsync(ct);
            if (!exists) return Results.NotFound();
            if (email.Length == 0)
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM reference_data.product_contact_email_overrides WHERE external_key = {key}", ct);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO reference_data.product_contact_email_overrides (external_key, email, is_verified, updated_by)
                    VALUES ({key}, {email}, {request.IsVerified}, {user.UserId})
                    ON CONFLICT (external_key) DO UPDATE
                    SET email = EXCLUDED.email, is_verified = EXCLUDED.is_verified,
                        updated_by = EXCLUDED.updated_by, updated_at = now()
                    """, ct);
            return Results.Ok(new { externalKey = key, email, isVerified = email.Length > 0 && request.IsVerified });
        });
        return group;
    }

    private static void Add(System.Data.Common.DbCommand cmd, string key, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = key;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static bool ValidEmail(string email)
    {
        if (email.Length == 0) return true; // Remove the override.
        if (email.Length > 254 || email.Contains('\n') || email.Contains('\r')
            || email.Contains(',') || email.Contains(';')) return false;
        try
        {
            var addr = new MailAddress(email);
            return string.Equals(addr.Address, email, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException) { return false; }
    }
}
