namespace Alpha.Infrastructure.Persistence;

public sealed class RegistrationOtpChallenge
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string NationalIdEncrypted { get; set; } = "";
    public string NationalIdLookupHash { get; set; } = "";
    public string PhoneEncrypted { get; set; } = "";
    public string PhoneLookupHash { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string? InvitationTokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public int Attempts { get; set; }
}
