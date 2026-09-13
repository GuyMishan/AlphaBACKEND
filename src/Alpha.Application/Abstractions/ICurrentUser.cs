namespace Alpha.Application.Abstractions;

public interface ICurrentUser
{
    Guid UserId { get; }
    bool IsAuthenticated { get; }
    bool IsPlatformAdmin { get; }
}
