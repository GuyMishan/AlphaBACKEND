using System.Security.Cryptography;
using System.Text;
using Alpha.Application.Abstractions;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Identity;

public sealed class InvitationService(IAlphaDbContext db)
{
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<UserInvitation?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token.Trim());
        return await db.UserInvitations.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    }

    public async Task ApplyAccessAsync(UserInvitation invitation, Guid userId, CancellationToken ct = default)
    {
        if (invitation.OrganizationRole.HasValue)
        {
            var accessMode = invitation.EmployerId.HasValue
                ? EmployerAccessMode.SelectedEmployers
                : EmployerAccessMode.AllEmployers;

            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x =>
                x.OrganizationId == invitation.OrganizationId && x.UserId == userId, ct);
            if (membership is null)
            {
                db.OrganizationMemberships.Add(new OrganizationMembership(
                    userId,
                    invitation.OrganizationId,
                    invitation.OrganizationRole.Value,
                    accessMode,
                    invitation.CreatedBy));
            }
            else if (!membership.IsActive)
            {
                membership.Reactivate(invitation.OrganizationRole.Value, accessMode);
            }
            else
            {
                membership.ChangeAccess(invitation.OrganizationRole.Value, accessMode);
            }
        }

        if (invitation.EmployerId.HasValue && invitation.EmployerRole.HasValue)
        {
            var grant = await db.EmployerUserAccesses.SingleOrDefaultAsync(x =>
                x.OrganizationId == invitation.OrganizationId &&
                x.EmployerId == invitation.EmployerId.Value &&
                x.UserId == userId, ct);
            if (grant is null)
            {
                db.EmployerUserAccesses.Add(new EmployerUserAccess(
                    userId,
                    invitation.OrganizationId,
                    invitation.EmployerId.Value,
                    invitation.EmployerRole.Value));
            }
            else
            {
                grant.ChangeRole(invitation.EmployerRole.Value);
            }
        }
    }
}
