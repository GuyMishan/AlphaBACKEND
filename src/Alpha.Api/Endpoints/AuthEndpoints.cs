using Alpha.Api.Security;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Alpha.Api.Services;
using Alpha.Application.Entitlements;
using Alpha.Application.Identity;
using Alpha.Domain.Identity;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Alpha.Api.Endpoints;

public sealed record RequestOtp(string NationalId, string Phone, string Channel = "email");
public sealed record VerifyOtp(Guid ChallengeId, string Code);
public sealed record RequestRegistrationOtp(string DisplayName, string Email, string NationalId, string Phone, string? InvitationToken = null);
public sealed record VerifyRegistrationOtp(Guid ChallengeId, string Code);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/session", async (IConfiguration config, AlphaDbContext db,
            Alpha.Application.Abstractions.ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == currentUser.UserId && x.IsActive, ct);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new { userId = user.Id, platformAdmin = user.IsPlatformAdmin, displayName = user.DisplayName });
        }).RequireAuthorization().WithTags("Authentication");

        endpoints.MapPost("/api/auth/logout", async (AlphaDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var sidValue = principal.FindFirstValue("alpha:session_id");
            var uidValue = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
            if (!Guid.TryParse(sidValue, out var sid) || !Guid.TryParse(uidValue, out var uid))
                return Results.Unauthorized();

            var session = await db.UserSessions.SingleOrDefaultAsync(x => x.Id == sid && x.UserId == uid, ct);
            if (session is not null && session.RevokedAt is null)
            {
                session.Revoke(DateTime.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization().WithTags("Authentication");

        endpoints.MapPost("/api/auth/otp/request", async (RequestOtp request, IConfiguration config, AlphaDbContext db,
            OtpDelivery delivery, ILogger<OtpDelivery> logger, IDataProtectionService protector, CancellationToken ct) =>
        {
            var nationalId = request.NationalId?.Trim() ?? "";
            var phone = request.Phone?.Trim() ?? "";
            var channel = request.Channel?.ToLowerInvariant();
            if (nationalId.Length is < 1 or > 9 || !nationalId.All(char.IsAsciiDigit) || !IsIsraeliMobile(phone) || channel is not ("sms" or "email"))
                return Results.BadRequest(new { error = "invalid_input" });
            if (channel == "sms" && !config.GetValue<bool>("Otp:Sms:Enabled"))
                return Results.BadRequest(new { error = "channel_unavailable" });
            if ((config["PrototypeAuth:SigningKey"]?.Length ?? 0) < 32)
                return Results.Problem("Authentication is not configured.", statusCode: 503);

            var nationalIdHash = protector.LookupHash(nationalId, "auth-national-id-lookup");
            var phoneHash = protector.LookupHash(phone, "auth-phone-lookup");
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive && x.NationalIdLookupHash == nationalIdHash && x.PhoneLookupHash == phoneHash, ct);
            if (user is null) return Results.Ok(new { challengeId = (Guid?)null, channel, expiresInSeconds = 300, resendAfterSeconds = 60 });
            var userId = user.Id;
            var destination = channel == "sms" ? protector.Unprotect(user.PhoneEncrypted ?? throw new InvalidOperationException("Encrypted phone missing."), "auth-phone") : user.Email;
            if (string.IsNullOrWhiteSpace(destination)) return Results.BadRequest(new { error = "channel_unavailable" });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({userId.ToString()}))", ct);
            var now = DateTime.UtcNow;
            var last = await db.OtpChallenges.Where(x => x.UserId == userId && x.ExpiresAt > now).OrderByDescending(x => x.CreatedAt).Select(x => (DateTime?)x.CreatedAt).FirstOrDefaultAsync(ct);
            if (last is not null && last > now.AddSeconds(-60)) return Results.StatusCode(429);

            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            var challenge = new OtpChallenge
            {
                Id = Guid.NewGuid(), UserId = userId, Channel = channel!, CodeHash = HashCode(config, code),
                CreatedAt = now, ExpiresAt = now.AddMinutes(5)
            };
            db.OtpChallenges.Add(challenge);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            try { await delivery.SendAsync(channel!, destination, code, ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "OTP delivery failed for challenge {ChallengeId}", challenge.Id);
                await db.OtpChallenges.Where(x => x.Id == challenge.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, DateTime.UtcNow).SetProperty(x => x.ExpiresAt, DateTime.UtcNow), ct);
                return Results.Problem("Code delivery is temporarily unavailable.", statusCode: 503);
            }
            return Results.Ok(new { challengeId = challenge.Id, channel, expiresInSeconds = 300, resendAfterSeconds = 60 });
        }).AllowAnonymous().WithTags("Authentication");

        endpoints.MapPost("/api/auth/register/request", async (RequestRegistrationOtp request, IConfiguration config, AlphaDbContext db,
            OtpDelivery delivery, ILogger<OtpDelivery> logger, IDataProtectionService protector, CancellationToken ct) =>
        {
            var displayName = request.DisplayName?.Trim() ?? "";
            var email = request.Email?.Trim().ToLowerInvariant() ?? "";
            var nationalId = request.NationalId?.Trim() ?? "";
            var phone = request.Phone?.Trim() ?? "";

            if (displayName.Length is < 2 or > 120 || !IsValidEmail(email) || !IsIsraeliId(nationalId) || !IsIsraeliMobile(phone))
                return Results.BadRequest(new { error = "invalid_input" });
            if ((config["PrototypeAuth:SigningKey"]?.Length ?? 0) < 32)
                return Results.Problem("Authentication is not configured.", statusCode: 503);

            var registrationNationalIdHash = protector.LookupHash(nationalId, "auth-national-id-lookup");
            if (await db.Users.AsNoTracking().AnyAsync(x =>
                    x.NationalIdLookupHash == registrationNationalIdHash, ct))
                return Results.Conflict(new { error = "user_exists" });

            string? invitationTokenHash = null;
            if (!string.IsNullOrWhiteSpace(request.InvitationToken))
            {
                invitationTokenHash = InvitationService.HashToken(request.InvitationToken.Trim());
                var invitation = await db.UserInvitations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.TokenHash == invitationTokenHash, ct);
                if (invitation is null)
                    return Results.BadRequest(new { error = "invitation_not_found" });
                if (invitation.Status != UserInvitationStatus.Pending)
                    return Results.Conflict(new { error = "invitation_not_pending", status = invitation.Status });
                if (invitation.ExpiresAt <= DateTimeOffset.UtcNow)
                    return Results.Json(new { error = "invitation_expired" }, statusCode: StatusCodes.Status410Gone);
                if (!string.Equals(invitation.Email, email, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "invitation_email_mismatch" });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({nationalId + ":" + phone}))", ct);
            var now = DateTime.UtcNow;
            var last = await db.RegistrationOtpChallenges
                .Where(x => x.Email == email && x.ExpiresAt > now)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => (DateTime?)x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (last is not null && last > now.AddSeconds(-60))
                return Results.StatusCode(429);

            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            var challenge = new RegistrationOtpChallenge
            {
                Id = Guid.NewGuid(),
                DisplayName = displayName,
                Email = email,
                NationalIdEncrypted = protector.Protect(nationalId, "auth-national-id"),
                NationalIdLookupHash = protector.LookupHash(nationalId, "auth-national-id-lookup"),
                PhoneEncrypted = protector.Protect(phone, "auth-phone"),
                PhoneLookupHash = protector.LookupHash(phone, "auth-phone-lookup"),
                CodeHash = HashCode(config, code),
                InvitationTokenHash = invitationTokenHash,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(5)
            };
            db.RegistrationOtpChallenges.Add(challenge);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            try { await delivery.SendAsync("email", email, code, ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Registration OTP delivery failed for challenge {ChallengeId}", challenge.Id);
                await db.RegistrationOtpChallenges.Where(x => x.Id == challenge.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ConsumedAt, DateTime.UtcNow)
                        .SetProperty(x => x.ExpiresAt, DateTime.UtcNow), ct);
                return Results.Problem("Code delivery is temporarily unavailable.", statusCode: 503);
            }

            return Results.Ok(new { challengeId = challenge.Id, expiresInSeconds = 300, resendAfterSeconds = 60 });
        }).AllowAnonymous().WithTags("Authentication");

        endpoints.MapPost("/api/auth/register/verify", async (VerifyRegistrationOtp request, IConfiguration config,
            AlphaDbContext db, EntitlementService entitlements, InvitationService invitations, OrganizationEntitlementLock entitlementLock, IDataProtectionService protector, CancellationToken ct) =>
        {
            if (request.ChallengeId == Guid.Empty || request.Code is null || request.Code.Length != 6 || !request.Code.All(char.IsAsciiDigit))
                return Results.BadRequest(new { error = "invalid_code" });
            if ((config["PrototypeAuth:SigningKey"]?.Length ?? 0) < 32)
                return Results.StatusCode(503);

            var now = DateTime.UtcNow;
            var attempts = await db.RegistrationOtpChallenges
                .Where(x => x.Id == request.ChallengeId && x.ConsumedAt == null && x.ExpiresAt > now && x.Attempts < 5)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (attempts == 0) return Results.Unauthorized();

            var challenge = await db.RegistrationOtpChallenges.AsNoTracking().SingleAsync(x => x.Id == request.ChallengeId, ct);
            var expected = Convert.FromHexString(challenge.CodeHash);
            var supplied = Convert.FromHexString(HashCode(config, request.Code));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
                return Results.Unauthorized();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({challenge.NationalIdLookupHash}))", ct);

            UserInvitation? invitation = null;
            if (!string.IsNullOrWhiteSpace(challenge.InvitationTokenHash))
            {
                invitation = await db.UserInvitations.SingleOrDefaultAsync(x =>
                    x.TokenHash == challenge.InvitationTokenHash, ct);
                if (invitation is null)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "invitation_not_found" });
                }

                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtext({invitation.Id.ToString()}))", ct);

                await db.Entry(invitation).ReloadAsync(ct);
                if (invitation.Status != UserInvitationStatus.Pending)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Conflict(new { error = "invitation_not_pending", status = invitation.Status });
                }
                if (invitation.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    invitation.Expire();
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return Results.Json(new { error = "invitation_expired" }, statusCode: StatusCodes.Status410Gone);
                }
                if (!string.Equals(invitation.Email, challenge.Email, StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "invitation_email_mismatch" });
                }

                await entitlementLock.AcquireInCurrentTransactionAsync(invitation.OrganizationId, ct);
                var entitlement = await entitlements.CanAcceptInvitation(invitation.OrganizationId, ct);
                if (!entitlement.Allowed)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Json(new
                    {
                        error = entitlement.Error,
                        limit = entitlement.Limit,
                        current = entitlement.Current,
                        maximum = entitlement.Maximum
                    }, statusCode: StatusCodes.Status409Conflict);
                }
            }

            var claimed = await db.RegistrationOtpChallenges
                .Where(x => x.Id == challenge.Id && x.ConsumedAt == null && x.ExpiresAt > DateTime.UtcNow && x.Attempts <= 5)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, DateTime.UtcNow), ct);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            if (await db.Users.AnyAsync(x =>
                    x.NationalIdLookupHash == challenge.NationalIdLookupHash, ct))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { error = "user_exists" });
            }

            var nationalId = protector.Unprotect(challenge.NationalIdEncrypted, "auth-national-id");
            var phone = protector.Unprotect(challenge.PhoneEncrypted, "auth-phone");
            var user = new User($"national-id-hash:{challenge.NationalIdLookupHash}", challenge.Email, challenge.DisplayName, nationalId, phone);
            user.SetProtectedIdentity(challenge.NationalIdEncrypted, challenge.NationalIdLookupHash, challenge.PhoneEncrypted, challenge.PhoneLookupHash);
            db.Users.Add(user);

            if (invitation is not null)
            {
                await invitations.ApplyAccessAsync(invitation, user.Id, ct);
                invitation.Accept(user.Id);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return await CreateTokenResultAsync(config, db, user.Id, user.DisplayName, false, ct);
        }).AllowAnonymous().WithTags("Authentication");

        endpoints.MapPost("/api/auth/otp/verify", async (VerifyOtp request, IConfiguration config, AlphaDbContext db, CancellationToken ct) =>
        {
            if (request.ChallengeId == Guid.Empty || request.Code is null || request.Code.Length != 6 || !request.Code.All(char.IsAsciiDigit))
                return Results.BadRequest(new { error = "invalid_code" });
            if ((config["PrototypeAuth:SigningKey"]?.Length ?? 0) < 32) return Results.StatusCode(503);
            var now = DateTime.UtcNow;
            // Failed guesses count atomically across API instances; the sixth guess locks the challenge.
            var attempts = await db.OtpChallenges.Where(x => x.Id == request.ChallengeId && x.ConsumedAt == null && x.ExpiresAt > now && x.Attempts < 5)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (attempts == 0) return Results.Unauthorized();
            var challenge = await db.OtpChallenges.AsNoTracking().SingleAsync(x => x.Id == request.ChallengeId, ct);
            if (challenge.Channel == "sms" && !config.GetValue<bool>("Otp:Sms:Enabled")) return Results.Unauthorized();
            var expected = Convert.FromHexString(challenge.CodeHash);
            var supplied = Convert.FromHexString(HashCode(config, request.Code));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return Results.Unauthorized();
            var claimed = await db.OtpChallenges.Where(x => x.Id == challenge.Id && x.ConsumedAt == null && x.ExpiresAt > DateTime.UtcNow && x.Attempts <= 5)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, DateTime.UtcNow), ct);
            if (claimed != 1) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == challenge.UserId && x.IsActive, ct);
            if (user is null) return Results.Unauthorized();
            return await CreateTokenResultAsync(config, db, user.Id, user.DisplayName, user.IsPlatformAdmin, ct);
        }).AllowAnonymous().WithTags("Authentication");
        return endpoints;
    }

    private static string HashCode(IConfiguration config, string code)
    {
        var signingKey = config["PrototypeAuth:SigningKey"]!;
        var otpKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes("alpha:otp:v1"));
        return Convert.ToHexString(HMACSHA256.HashData(otpKey, Encoding.UTF8.GetBytes(code)));
    }

    private static async Task<IResult> CreateTokenResultAsync(IConfiguration configuration, AlphaDbContext db, Guid userId, string displayName, bool platformAdmin, CancellationToken ct)
    {
        var key = configuration["PrototypeAuth:SigningKey"]!;
        if (key.Length < 32) return Results.StatusCode(503);
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var session = new UserSession(userId, now, now.AddHours(12));
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()), new("alpha:session_id", session.Id.ToString()) };
        if (platformAdmin) claims.Add(new Claim("alpha:platform_admin", "true"));
        var token = new JwtSecurityToken(issuer: "alpha-prototype", audience: "alpha-frontend",
            claims: claims, notBefore: now,
            expires: now.AddHours(12), signingCredentials: credentials);
        return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), userId, platformAdmin, displayName });
    }

    private static bool IsValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) return false;
        try { return new System.Net.Mail.MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsIsraeliMobile(string value) => value.Length == 10 && value.StartsWith("05", StringComparison.Ordinal) && value.All(char.IsAsciiDigit);
    private static bool IsIsraeliId(string value)
    {
        if (value.Length is < 1 or > 9 || !value.All(char.IsAsciiDigit)) return false;
        var id = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < id.Length; i++) { var n = (id[i] - '0') * (i % 2 == 0 ? 1 : 2); sum += n > 9 ? n - 9 : n; }
        return sum % 10 == 0;
    }
}
