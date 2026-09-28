using Alpha.Domain.Common;

namespace Alpha.Domain.Identity;

public sealed class UserSession : Entity
{
    private UserSession() { }
    public UserSession(Guid userId, DateTime createdAt, DateTime expiresAt)
    {
        UserId = userId; CreatedAt = createdAt; LastActivityAt = createdAt; ExpiresAt = expiresAt;
    }
    public Guid UserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public void TouchActivity(DateTime at) => LastActivityAt = at;
    public void Revoke(DateTime at) => RevokedAt ??= at;
}
