using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public enum EmployerRole
{
    Owner = 1,
    Admin = 2,
    User = 3,
    Viewer = 4
}

public sealed class EmployerUserAccess : Entity
{
    private EmployerUserAccess() { }

    public EmployerUserAccess(Guid userId, Guid organizationId, Guid employerId, EmployerRole role = EmployerRole.User)
    {
        UserId = userId;
        OrganizationId = organizationId;
        EmployerId = employerId;
        Role = role;
    }

    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public EmployerRole Role { get; private set; }

    public void ChangeRole(EmployerRole role)
    {
        Role = role;
        Touch();
    }
}
