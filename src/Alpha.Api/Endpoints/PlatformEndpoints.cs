using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record CreateUserRequest(string Email, string DisplayName, string NationalId, string Phone);
public sealed record UpdatePlatformUserRequest(string Email, string DisplayName, bool IsActive, bool IsPlatformAdmin);

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform").RequireAuthorization().WithTags("Platform");

        group.MapGet("/security/events", async (IAlphaDbContext db, ICurrentUser currentUser, int? take, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            var limit = Math.Clamp(take ?? 100, 1, 500);
            var events = await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(limit)
                .Select(x => new { x.Id, x.CreatedAt, x.ActorUserId, x.Action, x.EntityType, x.EntityId, x.OrganizationId, x.EmployerId, x.CorrelationId, x.Data }).ToListAsync(ct);
            return Results.Ok(new { auditLogging = true, rateLimiting = true, securityHeaders = true, retention = true, items = events });
        });

        group.MapGet("/users", async (IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            return Results.Ok(await db.Users.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct));
        });

        group.MapPost("/users", async (CreateUserRequest request, IAlphaDbContext db, ICurrentUser currentUser, IDataProtectionService protector, CancellationToken ct) =>
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

            var nationalIdHash = protector.LookupHash(nationalId, "auth-national-id-lookup");
            var phoneHash = protector.LookupHash(phone, "auth-phone-lookup");
            var externalSubject = $"national-id-hash:{nationalIdHash}";
            if (await db.Users.AnyAsync(x =>
                    x.NationalIdLookupHash == nationalIdHash && x.PhoneLookupHash == phoneHash,
                ct))
                return Results.Conflict(new { error = "A user with this national ID and phone already exists." });

            var user = new User(externalSubject, email, displayName, nationalId, phone);
            user.SetProtectedIdentity(protector.Protect(nationalId, "auth-national-id"), nationalIdHash,
                protector.Protect(phone, "auth-phone"), phoneHash);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/platform/users/{user.Id}", user);
        });

        group.MapPut("/users/{userId:guid}", async (Guid userId, UpdatePlatformUserRequest request,
            IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null) return Results.NotFound();

            var displayName = request.DisplayName.Trim();
            var email = request.Email.Trim().ToLowerInvariant();
            if (displayName.Length is < 2 or > 120)
                return Results.BadRequest(new { error = "Display name must contain 2-120 characters." });
            if (!email.Contains('@') || email.Length > 320)
                return Results.BadRequest(new { error = "Email is invalid." });
            if (await db.Users.AnyAsync(x => x.Id != userId && x.Email == email, ct))
                return Results.Conflict(new { error = "A user with this email already exists." });
            if (currentUser.UserId == userId && (!request.IsActive || !request.IsPlatformAdmin))
                return Results.BadRequest(new { error = "You cannot remove your own platform admin access or deactivate yourself." });

            user.UpdateProfile(displayName, email);
            user.SetActive(request.IsActive);
            user.SetPlatformAdmin(request.IsPlatformAdmin);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
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
