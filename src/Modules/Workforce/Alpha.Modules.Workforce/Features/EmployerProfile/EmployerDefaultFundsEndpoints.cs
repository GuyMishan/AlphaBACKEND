using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Alpha.Modules.Workforce.Features.EmployerProfile;

public static class EmployerDefaultFundsEndpoints
{
    public sealed record UpdateDefaultFundsRequest(string[] FundExternalKeys);
    private sealed record Fund(string Key, string Name, string Company, bool IsSystemDefault);

    public static RouteGroupBuilder MapEmployerDefaultFundsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/default-funds", GetAsync);
        group.MapPut("/default-funds", UpdateAsync);
        group.MapDelete("/default-funds", ResetAsync);
        return group;
    }

    private static async Task<List<Fund>> CatalogAsync(AlphaDbContext db, CancellationToken ct)
    {
        var result = new List<Fund>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT external_key, fund_name, COALESCE(company_name, ''), is_system_default_fund
            FROM reference_data.pension_products
            WHERE is_active = true AND product_type = 'קרן פנסיה'
            ORDER BY fund_name, external_key
            """;
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new Fund(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return result;
    }

    private static async Task<string[]?> OverrideAsync(Guid employerId, AlphaDbContext db, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT fund_external_keys FROM employers.employer_default_funds WHERE employer_id = @employer_id";
        command.Parameters.Add(new NpgsqlParameter("employer_id", employerId));
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var result = await command.ExecuteScalarAsync(ct);
        return result is string[] keys ? keys : null;
    }

    private static async Task<IResult> GetAsync(Guid organizationId, Guid employerId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();
        var catalog = await CatalogAsync(db, ct);
        var saved = await OverrideAsync(employerId, db, ct);
        var defaults = catalog.Where(x => x.IsSystemDefault).Select(x => x.Key).ToArray();
        var selection = saved ?? defaults;
        var chosen = selection.ToHashSet(StringComparer.Ordinal);
        return Results.Ok(new
        {
            overridden = saved is not null,
            systemDefaultKeys = defaults,
            selectedKeys = selection,
            selectedFunds = catalog.Where(x => chosen.Contains(x.Key)).Select(x => new { externalKey = x.Key, fundName = x.Name, companyName = x.Company }),
            systemDefaultFunds = catalog.Where(x => x.IsSystemDefault).Select(x => new { externalKey = x.Key, fundName = x.Name, companyName = x.Company })
        });
    }

    private static async Task<IResult> UpdateAsync(Guid organizationId, Guid employerId, UpdateDefaultFundsRequest request,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();
        var selected = (request.FundExternalKeys ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length > 100 || selected.Any(string.IsNullOrWhiteSpace))
            return Results.BadRequest(new { error = "Invalid pension fund selection." });
        var catalog = await CatalogAsync(db, ct);
        var allowed = catalog.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(x => !allowed.Contains(x)))
            return Results.BadRequest(new { error = "One or more pension funds are not in the active catalog." });
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO employers.employer_default_funds (employer_id, fund_external_keys, updated_at)
            VALUES ({0}, {1}, now())
            ON CONFLICT (employer_id) DO UPDATE
            SET fund_external_keys = EXCLUDED.fund_external_keys, updated_at = now()
            """, new object[] { employerId, selected }, ct);
        return Results.Ok(new { overridden = true, selectedKeys = selected });
    }

    private static async Task<IResult> ResetAsync(Guid organizationId, Guid employerId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM employers.employer_default_funds WHERE employer_id = {0}", new object[] { employerId }, ct);
        return Results.NoContent();
    }
}
