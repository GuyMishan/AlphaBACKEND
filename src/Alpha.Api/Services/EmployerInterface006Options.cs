namespace Alpha.Api.Services;

public sealed class EmployerInterface006Options
{
    public const string SectionName = "EmployerInterface006";

    // Official workbook: 1 = TEST, 2 = PRODUCTION.
    public int EnvironmentCode { get; set; } = 2;

    // Sender identity must describe the entity that owns the vault / sends the file.
    // For a direct employer SenderCode=5 can fall back to the employer profile.
    // For an intermediary/service bureau configure all sender fields explicitly.
    public int SenderCode { get; set; } = 5;
    public int SenderIdentifierType { get; set; } = 1;
    public string SenderIdentifier { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderContactFirstName { get; set; } = string.Empty;
    public string SenderContactLastName { get; set; } = string.Empty;
    public string SenderContactPhone { get; set; } = string.Empty;
    public string SenderContactEmail { get; set; } = string.Empty;
    public string SenderContactMobile { get; set; } = string.Empty;

    // Annex VI direction prefix. Direct employer -> clearinghouse = 003.
    // A service bureau/vault owner must configure its assigned/appropriate direction.
    public int FileDirectionCode { get; set; } = 3;

    public int RecipientCode { get; set; }
    public int RecipientIdentifierType { get; set; }
    public string RecipientIdentifier { get; set; } = string.Empty;
    public string? RecipientInternalIdentifier { get; set; }
}
