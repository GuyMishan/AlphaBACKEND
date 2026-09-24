using Alpha.Domain.Employers;

namespace Alpha.Api.Services;

public static class EmployerInterface006FileNaming
{
    public sealed record PackageName(string PayloadFileName, string BaseName);

    public static PackageName Build(Employer employer, bool negative, DateTimeOffset preparedAt, int sequence = 1, bool testFile = false)
    {
        if (sequence is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(sequence));
        var senderDigits = new string((employer.RegistrationNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (senderDigits.Length is 0 or > 12)
            throw new InvalidOperationException("Employer Interface Annex VI file naming requires a sender identifier of up to 12 digits.");

        var customerId = senderDigits.PadLeft(12, '0');
        var serviceId = negative ? "EMPNEG" : "EMPONG";
        var baseName = $"003{customerId}{serviceId}000006{preparedAt:yyyyMMddHHmmss}{sequence:0000}";
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
