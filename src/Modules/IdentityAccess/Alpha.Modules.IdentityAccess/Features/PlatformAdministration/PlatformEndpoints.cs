using Alpha.Domain.Common;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Identity;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.IdentityAccess.Features.PlatformAdministration;

public sealed record CreateUserRequest(string Email, string DisplayName, string NationalId, string Phone);
public sealed record UpdatePlatformUserRequest(string Email, string DisplayName, bool IsActive, bool IsPlatformAdmin);

/// <summary>
/// Platform view of a user. Encrypted identifiers, lookup hashes and the external subject (which embeds
/// the national-ID hash) are persistence details and are never serialized.
/// </summary>
public sealed record PlatformUserResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Email,
    string DisplayName, string Appearance, bool IsPlatformAdmin, bool IsReferent, bool IsActive)
{
    public static PlatformUserResponse From(User user) => new(user.Id, user.CreatedAt, user.UpdatedAt, user.Email,
        user.DisplayName, user.Appearance, user.IsPlatformAdmin, user.IsReferent, user.IsActive);
}

public static class PlatformEndpoints
{
    /// <summary>Maps platform user administration onto the host-owned <c>/api/platform</c> group.</summary>
    public static RouteGroupBuilder MapPlatformUserEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/users", async (AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            return Results.Ok(await db.Users.AsNoTracking().OrderBy(x => x.DisplayName)
                .Select(x => new PlatformUserResponse(x.Id, x.CreatedAt, x.UpdatedAt, x.Email, x.DisplayName,
                    x.Appearance, x.IsPlatformAdmin, x.IsReferent, x.IsActive))
                .ToListAsync(ct));
        });

        group.MapPost("/users", async (CreateUserRequest request, AlphaDbContext db, ICurrentUser currentUser, IDataProtectionService protector, CancellationToken ct) =>
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
            if (!IsraeliNationalId.IsValid(nationalId))
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
            return Results.Created($"/api/platform/users/{user.Id}", PlatformUserResponse.From(user));
        });

        group.MapPut("/users/{userId:guid}", async (Guid userId, UpdatePlatformUserRequest request,
            AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
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
            if (request.IsPlatformAdmin && user.IsReferent)
                return Results.Conflict(new { error = "referent_cannot_be_platform_admin" });
            user.SetPlatformAdmin(request.IsPlatformAdmin);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        return group;
    }
}
