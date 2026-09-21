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
    public string Appearance { get; private set; } = "system";
    public bool IsPlatformAdmin { get; private set; }
    public bool IsActive { get; private set; } = true;

    public void SetAppearance(string appearance)
    {
        var normalized = appearance.Trim().ToLowerInvariant();
        if (normalized is not ("system" or "light" or "dark"))
            throw new ArgumentException("Appearance must be system, light or dark.", nameof(appearance));

        Appearance = normalized;
        Touch();
    }

    public void UpdateProfile(string displayName, string email)
    {
        DisplayName = Require(displayName, nameof(displayName));
        Email = Require(email, nameof(email)).ToLowerInvariant();
        Touch();
    }

    public void SetPlatformAdmin(bool isPlatformAdmin)
    {
        IsPlatformAdmin = isPlatformAdmin;
        Touch();
    }

    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        Touch();
    }

    public void Deactivate()
    {
        SetActive(false);
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
