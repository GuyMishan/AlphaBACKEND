using Alpha.Domain.Common;

namespace Alpha.Domain.Organizations;

public enum OrganizationRole { Admin = 1, PayrollManager = 2, OperationsAgent = 3, Viewer = 4 }
public enum EmployerAccessMode { AllEmployers = 1, SelectedEmployers = 2 }

public sealed class OrganizationMembership : Entity
{
    private OrganizationMembership() { }

    public OrganizationMembership(Guid userId, Guid organizationId, OrganizationRole role,
        EmployerAccessMode employerAccessMode, Guid createdBy)
    {
        UserId = userId;
        OrganizationId = organizationId;
        Role = role;
        EmployerAccessMode = employerAccessMode;
        CreatedBy = createdBy;
    }

    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public OrganizationRole Role { get; private set; }
    public EmployerAccessMode EmployerAccessMode { get; private set; }
    public bool IsActive { get; private set; } = true;
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsValidAt(DateTimeOffset now) => IsActive && (ExpiresAt is null || ExpiresAt > now);
}
