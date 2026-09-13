using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public sealed class EmployerUserAccess : Entity
{
    private EmployerUserAccess() { }

    public EmployerUserAccess(Guid userId, Guid organizationId, Guid employerId)
    {
        UserId = userId;
        OrganizationId = organizationId;
        EmployerId = employerId;
    }

    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
}
