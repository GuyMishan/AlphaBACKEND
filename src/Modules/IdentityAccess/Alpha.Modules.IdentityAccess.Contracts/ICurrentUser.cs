namespace Alpha.Modules.IdentityAccess.Contracts;

/// <summary>
/// Describes the authenticated actor for application-level authorization.
/// Implementations may be HTTP users, trusted workers, or future service identities.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }
    bool IsAuthenticated { get; }
    bool IsPlatformAdmin { get; }
}
