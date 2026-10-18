using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.IdentityAccess.Features.UserPreferences;

public static class UserPreferencesEndpoints
{
    public static IEndpointRouteBuilder MapUserPreferencesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/me/preferences").RequireAuthorization().WithTags("User preferences");
        group.MapGet("/", GetAsync);
        group.MapPut("/appearance", UpdateAppearanceAsync);
        group.MapPut("/marketing", UpdateMarketingAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == currentUser.UserId, ct);
        return user is null ? Results.NotFound() : Results.Ok(new { appearance = user.Appearance, marketingEmailOptIn = await db.Database.SqlQueryRaw<bool>(
            "SELECT \"MarketingEmailOptIn\" AS \"Value\" FROM identity.users WHERE \"Id\" = {0}", user.Id).SingleAsync(ct) });
    }

    private static async Task<IResult> UpdateAppearanceAsync(UpdateAppearanceRequest request, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == currentUser.UserId, ct);
        if (user is null) return Results.NotFound();
        try
        {
            user.SetAppearance(request.Appearance);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "העדפת תצוגה לא תקינה.", field = "appearance" });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { appearance = user.Appearance });
    }

    private sealed record UpdateMarketingRequest(bool MarketingEmailOptIn);

    private static async Task<IResult> UpdateMarketingAsync(UpdateMarketingRequest request,
        AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.users SET "MarketingEmailOptIn" = {request.MarketingEmailOptIn}
            WHERE "Id" = {currentUser.UserId}
            """, ct);
        return affected == 0 ? Results.NotFound() : Results.Ok(new { request.MarketingEmailOptIn });
    }

    private sealed record UpdateAppearanceRequest(string Appearance);
}
