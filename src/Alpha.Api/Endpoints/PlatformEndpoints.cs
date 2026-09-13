using Alpha.Application.Abstractions;
using Alpha.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record CreateUserRequest(string ExternalSubject, string Email, string DisplayName);

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform").RequireAuthorization().WithTags("Platform");

        group.MapGet("/users", async (IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            return Results.Ok(await db.Users.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct));
        });

        group.MapPost("/users", async (CreateUserRequest request, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            if (await db.Users.AnyAsync(x => x.ExternalSubject == request.ExternalSubject || x.Email == request.Email.ToLower(), ct))
                return Results.Conflict(new { error = "A user with this subject or email already exists." });
            var user = new User(request.ExternalSubject, request.Email, request.DisplayName);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/platform/users/{user.Id}", user);
        });
        return endpoints;
    }
}
