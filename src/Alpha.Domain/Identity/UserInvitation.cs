using Alpha.Domain.Common;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;

namespace Alpha.Domain.Identity;

public enum UserInvitationStatus
{
    Pending = 1,
    Accepted = 2,
    Expired = 3,
    Cancelled = 4
}

public sealed class UserInvitation : Entity
{
    private UserInvitation() { }

    public UserInvitation(
        string email,
        Guid organizationId,
        Guid? employerId,
        OrganizationRole? organizationRole,
        EmployerRole? employerRole,
        string tokenHash,
        DateTimeOffset expiresAt,
        Guid createdBy)
    {
        Email = string.IsNullOrWhiteSpace(email) ? throw new ArgumentException("Email is required.", nameof(email)) : email.Trim().ToLowerInvariant();
        OrganizationId = organizationId == Guid.Empty ? throw new ArgumentException("Organization is required.", nameof(organizationId)) : organizationId;
        EmployerId = employerId;
        OrganizationRole = organizationRole;
        EmployerRole = employerRole;
        TokenHash = string.IsNullOrWhiteSpace(tokenHash) ? throw new ArgumentException("Token hash is required.", nameof(tokenHash)) : tokenHash;
        ExpiresAt = expiresAt;
        CreatedBy = createdBy == Guid.Empty ? throw new ArgumentException("Creator is required.", nameof(createdBy)) : createdBy;

        if (!OrganizationRole.HasValue && (!EmployerId.HasValue || !EmployerRole.HasValue))
            throw new ArgumentException("Invitation must grant organization access or employer access.");
        if (EmployerRole.HasValue && !EmployerId.HasValue)
            throw new ArgumentException("Employer role requires an employer.");
    }

    public string Email { get; private set; } = string.Empty;
    public Guid OrganizationId { get; private set; }
    public Guid? EmployerId { get; private set; }
    public OrganizationRole? OrganizationRole { get; private set; }
    public EmployerRole? EmployerRole { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public UserInvitationStatus Status { get; private set; } = UserInvitationStatus.Pending;
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }

    public bool IsUsableAt(DateTimeOffset now) => Status == UserInvitationStatus.Pending && ExpiresAt > now;

    public void Accept(Guid userId)
    {
        if (!IsUsableAt(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Invitation is not active.");
        Status = UserInvitationStatus.Accepted;
        AcceptedAt = DateTimeOffset.UtcNow;
        AcceptedByUserId = userId;
        Touch();
    }

    public void Cancel()
    {
        if (Status != UserInvitationStatus.Pending) return;
        Status = UserInvitationStatus.Cancelled;
        Touch();
    }

    public void Expire()
    {
        if (Status != UserInvitationStatus.Pending) return;
        Status = UserInvitationStatus.Expired;
        Touch();
    }
}
