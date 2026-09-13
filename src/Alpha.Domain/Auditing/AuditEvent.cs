using Alpha.Domain.Common;

namespace Alpha.Domain.Auditing;

public sealed class AuditEvent : Entity
{
    private AuditEvent() { }

    public AuditEvent(Guid actorUserId, string action, string entityType, Guid entityId,
        Guid? organizationId, Guid? employerId, string data, string correlationId)
    {
        ActorUserId = actorUserId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        OrganizationId = organizationId;
        EmployerId = employerId;
        Data = data;
        CorrelationId = correlationId;
    }

    public Guid ActorUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public Guid? EmployerId { get; private set; }
    public string Data { get; private set; } = "{}";
    public string CorrelationId { get; private set; } = string.Empty;
}
