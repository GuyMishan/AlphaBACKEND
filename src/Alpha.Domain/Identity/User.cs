using Alpha.Domain.Common;

namespace Alpha.Domain.Identity;

public sealed class User : Entity
{
    private User() { }

    public User(string externalSubject, string email, string displayName)
    {
        ExternalSubject = Require(externalSubject, nameof(externalSubject));
        Email = Require(email, nameof(email)).ToLowerInvariant();
        DisplayName = Require(displayName, nameof(displayName));
    }

    public User(string externalSubject, string email, string displayName, string nationalId, string phone)
        : this(externalSubject, email, displayName)
    {
        NationalId = Require(nationalId, nameof(nationalId));
        Phone = Require(phone, nameof(phone));
    }

    public string ExternalSubject { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? NationalId { get; private set; }
    public string? Phone { get; private set; }
    public bool IsActive { get; private set; } = true;

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
