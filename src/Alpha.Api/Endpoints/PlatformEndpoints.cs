using Alpha.Application.Abstractions;
using Alpha.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record CreateUserRequest(string Email, string DisplayName, string NationalId, string Phone);

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

            var displayName = request.DisplayName.Trim();
            var email = request.Email.Trim().ToLowerInvariant();
            var nationalId = request.NationalId.Trim();
            var phone = request.Phone.Trim();

            if (displayName.Length is < 2 or > 120)
                return Results.BadRequest(new { error = "Display name must contain 2-120 characters." });
            if (!email.Contains('@') || email.Length > 320)
                return Results.BadRequest(new { error = "Email is invalid." });
            if (!IsIsraeliId(nationalId))
                return Results.BadRequest(new { error = "National ID is invalid." });
            if (phone.Length != 10 || !phone.StartsWith("05", StringComparison.Ordinal) || !phone.All(char.IsDigit))
                return Results.BadRequest(new { error = "Phone must be a 10-digit Israeli mobile number." });

            var externalSubject = $"national-id:{nationalId}";
            if (await db.Users.AnyAsync(x =>
                    x.ExternalSubject == externalSubject ||
                    x.Email == email ||
                    x.NationalId == nationalId ||
                    x.Phone == phone,
                ct))
                return Results.Conflict(new { error = "A user with this email, national ID or phone already exists." });

            var user = new User(externalSubject, email, displayName, nationalId, phone);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/platform/users/{user.Id}", user);
        });
        return endpoints;
    }

    private static bool IsIsraeliId(string value)
    {
        if (value.Length is < 1 or > 9 || !value.All(char.IsDigit)) return false;
        var id = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < id.Length; i++)
        {
            var number = (id[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += number > 9 ? number - 9 : number;
        }
        return sum % 10 == 0;
    }
}
