namespace Alpha.Api.Services;

public sealed class EmployerInterface006Options
{
    public const string SectionName = "EmployerInterface006";
    public int EnvironmentCode { get; set; } = 2;
    public int SenderCode { get; set; } = 3;
    public int SenderIdentifierType { get; set; } = 1;
    public int RecipientCode { get; set; }
    public int RecipientIdentifierType { get; set; }
    public string RecipientIdentifier { get; set; } = string.Empty;
    public string? RecipientInternalIdentifier { get; set; }
}
