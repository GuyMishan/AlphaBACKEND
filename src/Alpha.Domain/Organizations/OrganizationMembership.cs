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
        ApplyRoleDefaults(role, employerAccessMode);
    }

    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public OrganizationRole Role { get; private set; }
    public EmployerAccessMode EmployerAccessMode { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool CanCreateEmployer { get; private set; }
    public bool CanEditEmployer { get; private set; }
    public bool CanCreateEmployee { get; private set; }
    public bool CanEditEmployee { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsValidAt(DateTimeOffset now) => IsActive && (ExpiresAt is null || ExpiresAt > now);

    public void ChangeAccess(OrganizationRole role, EmployerAccessMode employerAccessMode)
    {
        Role = role;
        EmployerAccessMode = employerAccessMode;
        ApplyRoleDefaults(role, employerAccessMode);
        Touch();
    }

    public void ChangePermissions(bool canCreateEmployer, bool canEditEmployer, bool canCreateEmployee, bool canEditEmployee)
    {
        CanCreateEmployer = canCreateEmployer;
        CanEditEmployer = canEditEmployer;
        CanCreateEmployee = canCreateEmployee;
        CanEditEmployee = canEditEmployee;
        Touch();
    }

    private void ApplyRoleDefaults(OrganizationRole role, EmployerAccessMode employerAccessMode)
    {
        CanCreateEmployer = role == OrganizationRole.Admin && employerAccessMode == EmployerAccessMode.AllEmployers;
        CanEditEmployer = role != OrganizationRole.Viewer;
        CanCreateEmployee = role != OrganizationRole.Viewer;
        CanEditEmployee = role != OrganizationRole.Viewer;
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void Reactivate(OrganizationRole role, EmployerAccessMode employerAccessMode)
    {
        Role = role;
        EmployerAccessMode = employerAccessMode;
        ApplyRoleDefaults(role, employerAccessMode);
        IsActive = true;
        ExpiresAt = null;
        Touch();
    }
}
