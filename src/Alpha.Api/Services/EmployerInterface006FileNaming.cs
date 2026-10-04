using System.Text.RegularExpressions;

namespace Alpha.Api.Services;

public static partial class EmployerInterface006FileNaming
{
    public const int EmployerToClearinghouseDirection = 3;
    public const int ServiceBureauToClearinghouseDirection = 6;
    public const string CurrentServiceId = "EMPONG";
    public const string NegativeServiceId = "EMPNEG";
    public const string EmployerInterfaceProductType = "000";
    public const string Version006 = "006";

    public sealed record PackageName(string PayloadFileName, string BaseName);

    [GeneratedRegex(@"^(003|006)[0-9]{12}(EMPONG|EMPNEG)000006[0-9]{14}[0-9]{4}\.(DAT|TST)$", RegexOptions.CultureInvariant)]
    private static partial Regex OfficialOutboundNameRegex();

    public static bool IsOfficialOutboundEmployerInterfaceName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && OfficialOutboundNameRegex().IsMatch(Path.GetFileName(fileName.Trim()));

    public static PackageName Build(string senderIdentifier, int directionCode, bool negative,
        DateTimeOffset preparedAt, int sequence = 1, bool testFile = false)
    {
        if (sequence is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (directionCode is not (EmployerToClearinghouseDirection or ServiceBureauToClearinghouseDirection))
            throw new ArgumentOutOfRangeException(nameof(directionCode),
                "Employer Interface files sent to the clearing house must use direction 003 (employer) or 006 (service bureau).");

        var senderDigits = new string((senderIdentifier ?? string.Empty).Where(char.IsDigit).ToArray());
        if (senderDigits.Length is 0 or > 12)
            throw new InvalidOperationException("Annex VI file naming requires the actual sender identifier to contain 1-12 digits.");

        var senderId = senderDigits.PadLeft(12, '0');
        var serviceId = negative ? NegativeServiceId : CurrentServiceId;
        var baseName = $"{directionCode:000}{senderId}{serviceId}{EmployerInterfaceProductType}{Version006}{preparedAt:yyyyMMddHHmmss}{sequence:0000}";
        return new PackageName($"{baseName}.{(testFile ? "TST" : "DAT")}", baseName);
    }

    public static string BuildAttachmentFileName(string baseName, int attachmentSequence, string extension)
    {
        if (attachmentSequence is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(attachmentSequence));
        var normalizedExtension = extension.Trim().TrimStart('.').ToUpperInvariant();
        if (normalizedExtension.Length is 0 or > 4 || normalizedExtension.Any(ch => !char.IsLetterOrDigit(ch)))
            throw new ArgumentException("Attachment extension is invalid.", nameof(extension));
        return $"{baseName}_{attachmentSequence:000}.{normalizedExtension}";
    }
}
