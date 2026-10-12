using Alpha.Domain.Employees;

namespace Alpha.Modules.Reporting.Features.Profiles;

public static class EmployerInterfaceProfileValidation
{
    public static string? Validate(DateOnly? birthDate, PersonGender? gender, string? email, string? mobile,
        string? city, string? street, string? houseNumber, string? postalCode, string? postOfficeBox)
    {
        if (!birthDate.HasValue || birthDate.Value >= DateOnly.FromDateTime(DateTime.UtcNow))
            return "תאריך הלידה הוא שדה חובה וחייב להיות בעבר.";
        if (!gender.HasValue || !Enum.IsDefined(gender.Value))
            return "מין העובד הוא שדה חובה ואינו תקין.";

        var normalizedEmail = email?.Trim() ?? string.Empty;
        if (normalizedEmail.Length == 0 || normalizedEmail.Length > 50
            || !normalizedEmail.Contains('@') || normalizedEmail.StartsWith('@') || normalizedEmail.EndsWith('@'))
            return "יש להזין כתובת אימייל תקינה לעובד, עד 50 תווים.";

        var normalizedMobile = new string((mobile ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedMobile.Length is < 7 or > 15)
            return "יש להזין מספר נייד אמיתי לעובד, 7-15 ספרות.";

        var postal = (postalCode ?? string.Empty).Trim();
        if (postal.Length > 0 && (postal.Any(ch => !char.IsDigit(ch)) || postal.Length > 7))
            return "מיקוד חייב להכיל עד 7 ספרות.";
        var box = (postOfficeBox ?? string.Empty).Trim();
        if (box.Length > 0 && (box.Any(ch => !char.IsDigit(ch)) || !int.TryParse(box, out var boxNumber) || boxNumber > 99999))
            return "תא דואר חייב להיות מספר בין 0 ל־99999. אין להזין מיקוד בשדה תא הדואר.";
        var hasPostOfficeBox = !string.IsNullOrWhiteSpace(postOfficeBox)
            && new string(postOfficeBox.Where(char.IsDigit).ToArray()).Length > 0;
        var normalizedPostalCode = new string((postalCode ?? string.Empty).Where(char.IsDigit).ToArray());
        var hasStreetAddress = !string.IsNullOrWhiteSpace(city)
            && !string.IsNullOrWhiteSpace(street)
            && !string.IsNullOrWhiteSpace(houseNumber)
            && normalizedPostalCode.Length > 0;
        if (!hasPostOfficeBox && !hasStreetAddress)
            return "יש להזין כתובת מלאה (יישוב, רחוב, מספר בית ומיקוד) או תא דואר.";
        return null;
    }
}
