namespace Alpha.Domain.Common;

/// <summary>Israeli identity number (Teudat Zehut) check-digit validation.</summary>
public static class IsraeliNationalId
{
    /// <summary>
    /// Accepts 1-9 ASCII digits (left-padded to 9) whose weighted digit sum is divisible by 10.
    /// Non-ASCII digits are rejected: they are not valid identity numbers and would corrupt the checksum.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 9 || !value.All(char.IsAsciiDigit)) return false;
        var padded = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < padded.Length; i++)
        {
            var n = (padded[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += n > 9 ? n - 9 : n;
        }
        return sum % 10 == 0;
    }
}
