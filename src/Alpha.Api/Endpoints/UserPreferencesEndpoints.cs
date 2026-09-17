using Alpha.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class UserPreferencesEndpoints
{
    public static IEndpointRouteBuilder MapUserPreferencesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/me/preferences").RequireAuthorization().WithTags("User preferences");
        group.MapGet("/", GetAsync);
        group.MapPut("/appearance", UpdateAppearanceAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == currentUser.UserId, ct);
        return user is null ? Results.NotFound() : Results.Ok(new { appearance = user.Appearance });
    }

    private static async Task<IResult> UpdateAppearanceAsync(UpdateAppearanceRequest request, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == currentUser.UserId, ct);
        if (user is null) return Results.NotFound();
        try
        {
            user.SetAppearance(request.Appearance);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "העדפת תצוגה לא תקינה." });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { appearance = user.Appearance });
    }

    private sealed record UpdateAppearanceRequest(string Appearance);
}
