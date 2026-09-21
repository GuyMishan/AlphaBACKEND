using System.Security.Cryptography;
using System.Text.Json;
using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Entitlements;
using Alpha.Application.Identity;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record CreateInvitationRequest(
    string Email,
    string NationalId,
    string Phone,
    Guid? EmployerId,
    OrganizationRole? OrganizationRole,
    EmployerRole? EmployerRole,
    int? ExpiresInDays = null);

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/invitations")
            .RequireAuthorization().WithTags("Invitations");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/{invitationId:guid}/cancel", CancelAsync);

        endpoints.MapGet("/api/invitations/{token}", GetPublicAsync)
            .AllowAnonymous().WithTags("Invitations");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid organizationId, AlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var expired = await db.UserInvitations
            .Where(x => x.OrganizationId == organizationId &&
                        x.Status == UserInvitationStatus.Pending &&
                        x.ExpiresAt <= now)
            .ToListAsync(ct);
        foreach (var invitation in expired) invitation.Expire();
        if (expired.Count > 0) await db.SaveChangesAsync(ct);

        var rows = await (
            from invitation in db.UserInvitations.AsNoTracking()
            where invitation.OrganizationId == organizationId
            join employer in db.Employers.AsNoTracking()
                on invitation.EmployerId equals employer.Id into employers
            from employer in employers.DefaultIfEmpty()
            orderby invitation.CreatedAt descending
            select new
            {
                invitation.Id,
                invitation.Email,
                invitation.OrganizationId,
                invitation.EmployerId,
                employerName = employer == null ? null : employer.LegalName,
                invitation.OrganizationRole,
                invitation.EmployerRole,
                invitation.Status,
                invitation.ExpiresAt,
                invitation.CreatedBy,
                invitation.AcceptedAt,
                invitation.AcceptedByUserId,
                invitation.CreatedAt
            }).ToListAsync(ct);

        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateAsync(Guid organizationId, CreateInvitationRequest request,
        AlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        EntitlementService entitlements, OtpDelivery delivery, IConfiguration configuration,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var nationalId = request.NationalId?.Trim() ?? string.Empty;
        var phone = request.Phone?.Trim() ?? string.Empty;
        if (!IsValidEmail(email)) return Results.BadRequest(new { error = "invalid_email" });
        if (!IsIsraeliId(nationalId)) return Results.BadRequest(new { error = "invalid_national_id" });
        if (!IsIsraeliMobile(phone)) return Results.BadRequest(new { error = "invalid_phone" });
        if (request.OrganizationRole.HasValue && !Enum.IsDefined(request.OrganizationRole.Value))
            return Results.BadRequest(new { error = "invalid_organization_role" });
        if (request.EmployerRole.HasValue && !Enum.IsDefined(request.EmployerRole.Value))
            return Results.BadRequest(new { error = "invalid_employer_role" });
        if (!request.OrganizationRole.HasValue && (!request.EmployerId.HasValue || !request.EmployerRole.HasValue))
            return Results.BadRequest(new { error = "invitation_scope_required" });
        if (request.EmployerId.HasValue && !request.EmployerRole.HasValue)
            return Results.BadRequest(new { error = "employer_role_required" });
        if (!request.EmployerId.HasValue && request.EmployerRole.HasValue)
            return Results.BadRequest(new { error = "employer_required" });

        var organization = await db.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == organizationId, ct);
        if (organization is null) return Results.NotFound();

        if (request.EmployerId.HasValue && !await db.Employers.AsNoTracking()
                .AnyAsync(x => x.Id == request.EmployerId.Value && x.OrganizationId == organizationId, ct))
            return Results.BadRequest(new { error = "employer_not_in_organization" });

        if (await db.Users.AsNoTracking().AnyAsync(x => x.NationalId == nationalId && x.Phone == phone, ct))
            return Results.Conflict(new { error = "existing_identity_phone_pair_not_supported_yet" });

        var now = DateTimeOffset.UtcNow;
        var existing = await db.UserInvitations.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Email == email &&
            x.Status == UserInvitationStatus.Pending && x.ExpiresAt > now, ct);
        if (existing is not null)
            return Results.Conflict(new { error = "invitation_already_pending", invitationId = existing.Id });

        var entitlement = await entitlements.CanInviteNewUser(organizationId, ct);
        if (!entitlement.Allowed) return EntitlementError(entitlement);

        var expiresInDays = Math.Clamp(request.ExpiresInDays ?? 7, 1, 30);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var invitation = new UserInvitation(
            email,
            organizationId,
            request.EmployerId,
            request.OrganizationRole,
            request.EmployerRole,
            InvitationService.HashToken(token),
            now.AddDays(expiresInDays),
            currentUser.UserId);
        db.UserInvitations.Add(invitation);
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "invitation.created", nameof(UserInvitation),
            invitation.Id, organizationId, request.EmployerId,
            JsonSerializer.Serialize(new
            {
                email,
                nationalId,
                phone,
                request.EmployerId,
                request.OrganizationRole,
                request.EmployerRole,
                invitation.ExpiresAt
            }), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);

        var frontendBaseUrl = configuration["Frontend:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(frontendBaseUrl))
        {
            var origin = http.Request.Headers.Origin.FirstOrDefault();
            frontendBaseUrl = !string.IsNullOrWhiteSpace(origin)
                ? origin.TrimEnd('/')
                : $"{http.Request.Scheme}://{http.Request.Host}";
        }
        var invitationUrl = $"{frontendBaseUrl}/invite?token={Uri.EscapeDataString(token)}";

        try
        {
            await delivery.SendInvitationAsync(email, invitationUrl, organization.Name, ct);
        }
        catch (Exception)
        {
            invitation.Cancel();
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Problem("Invitation email delivery is temporarily unavailable.", statusCode: 503);
        }

        return Results.Created($"/api/organizations/{organizationId}/invitations/{invitation.Id}", new
        {
            invitation.Id,
            invitation.Email,
            invitation.OrganizationId,
            invitation.EmployerId,
            invitation.OrganizationRole,
            invitation.EmployerRole,
            invitation.Status,
            invitation.ExpiresAt
        });
    }

    private static async Task<IResult> CancelAsync(Guid organizationId, Guid invitationId,
        AlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var invitation = await db.UserInvitations.SingleOrDefaultAsync(x =>
            x.Id == invitationId && x.OrganizationId == organizationId, ct);
        if (invitation is null) return Results.NotFound();
        if (invitation.Status != UserInvitationStatus.Pending)
            return Results.Conflict(new { error = "invitation_not_pending", status = invitation.Status });

        invitation.Cancel();
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "invitation.cancelled", nameof(UserInvitation),
            invitation.Id, organizationId, invitation.EmployerId,
            JsonSerializer.Serialize(new { invitation.Email }), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPublicAsync(string token, AlphaDbContext db,
        InvitationService invitations, CancellationToken ct)
    {
        var invitation = await invitations.GetByTokenAsync(token, ct);
        if (invitation is null) return Results.NotFound(new { error = "invitation_not_found" });

        if (invitation.Status == UserInvitationStatus.Pending && invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            invitation.Expire();
            await db.SaveChangesAsync(ct);
        }

        var organizationName = await db.Organizations.AsNoTracking()
            .Where(x => x.Id == invitation.OrganizationId)
            .Select(x => x.Name)
            .SingleAsync(ct);
        var employerName = invitation.EmployerId.HasValue
            ? await db.Employers.AsNoTracking()
                .Where(x => x.Id == invitation.EmployerId.Value)
                .Select(x => x.LegalName)
                .SingleOrDefaultAsync(ct)
            : null;

        return Results.Ok(new
        {
            invitation.Email,
            invitation.OrganizationId,
            organizationName,
            invitation.EmployerId,
            employerName,
            invitation.OrganizationRole,
            invitation.EmployerRole,
            invitation.Status,
            invitation.ExpiresAt,
            usable = invitation.IsUsableAt(DateTimeOffset.UtcNow)
        });
    }

    private static IResult EntitlementError(EntitlementDecision decision) =>
        Results.Json(new
        {
            error = decision.Error,
            limit = decision.Limit,
            current = decision.Current,
            maximum = decision.Maximum,
            feature = decision.Feature
        }, statusCode: StatusCodes.Status409Conflict);

    private static bool IsValidEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) return false;
        try { return new System.Net.Mail.MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsIsraeliMobile(string value) =>
        value.Length == 10 && value.StartsWith("05", StringComparison.Ordinal) && value.All(char.IsAsciiDigit);

    private static bool IsIsraeliId(string value)
    {
        if (value.Length is < 1 or > 9 || !value.All(char.IsAsciiDigit)) return false;
        var id = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < id.Length; i++)
        {
            var n = (id[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += n > 9 ? n - 9 : n;
        }
        return sum % 10 == 0;
    }
}
