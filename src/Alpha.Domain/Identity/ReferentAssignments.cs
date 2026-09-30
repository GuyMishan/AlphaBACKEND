using Alpha.Domain.Common;

namespace Alpha.Domain.Identity;

public sealed class ReferentOrganizationAssignment : Entity
{
    private ReferentOrganizationAssignment() { }
    public ReferentOrganizationAssignment(Guid userId, Guid organizationId)
    {
        if (userId == Guid.Empty || organizationId == Guid.Empty) throw new ArgumentException("Invalid referent organization assignment.");
        UserId = userId;
        OrganizationId = organizationId;
    }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
}

public sealed class ReferentEmployerAssignment : Entity
{
    private ReferentEmployerAssignment() { }
    public ReferentEmployerAssignment(Guid userId, Guid employerId)
    {
        if (userId == Guid.Empty || employerId == Guid.Empty) throw new ArgumentException("Invalid referent employer assignment.");
        UserId = userId;
        EmployerId = employerId;
    }
    public Guid UserId { get; private set; }
    public Guid EmployerId { get; private set; }
}
