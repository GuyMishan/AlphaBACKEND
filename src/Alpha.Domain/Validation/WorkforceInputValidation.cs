using Alpha.Domain.Common;
using System.Text.RegularExpressions;
using Alpha.Domain.Employees;

namespace Alpha.Domain.Validation;

/// <summary>A rejected input value: <see cref="Field"/> is the request property (camelCase JSON name).</summary>
public sealed record InputError(string Field, string Message);

public static class WorkforceInputValidation
{
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);

    public static InputError? Employer(string legalName, string registrationNumber, string withholdingFileNumber,
        string? contactFirstName, string? contactLastName, string? contactPhone, string? contactEmail, string? contactMobile)
    {
        if (string.IsNullOrWhiteSpace(legalName)) return new("legalName", "שם משפטי הוא שדה חובה.");
        if (legalName.Trim().Length is < 2 or > 200) return new("legalName", "שם משפטי חייב להכיל בין 2 ל-200 תווים.");
        if (string.IsNullOrWhiteSpace(registrationNumber) || !Digits.IsMatch(registrationNumber.Trim())) return new("registrationNumber", "מספר חברה / עוסק חייב להכיל ספרות בלבד.");
        if (registrationNumber.Trim().Length is < 5 or > 15) return new("registrationNumber", "מספר חברה / עוסק אינו באורך תקין.");
        if (string.IsNullOrWhiteSpace(withholdingFileNumber) || !Digits.IsMatch(withholdingFileNumber.Trim())) return new("withholdingFileNumber", "מספר תיק ניכויים חייב להכיל ספרות בלבד.");
        if (withholdingFileNumber.Trim().Length is < 5 or > 9) return new("withholdingFileNumber", "מספר תיק ניכויים חייב להכיל 5-9 ספרות.");
        if ((contactFirstName?.Trim().Length ?? 0) is < 2 or > 20) return new("contactFirstName", "שם פרטי של איש הקשר חייב להכיל 2–20 תווים.");
        if ((contactLastName?.Trim().Length ?? 0) is < 2 or > 20) return new("contactLastName", "שם משפחה של איש הקשר חייב להכיל 2–20 תווים.");
        if (string.IsNullOrWhiteSpace(contactEmail) || contactEmail.Trim().Length > 50 || !contactEmail.Contains('@')) return new("contactEmail", "כתובת האימייל של איש הקשר אינה תקינה.");

        var phone = new string((contactPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        var mobile = new string((contactMobile ?? string.Empty).Where(char.IsDigit).ToArray());
        if (phone.Length == 0 && mobile.Length == 0) return new("contactPhone", "יש להזין לפחות טלפון או נייד אחד של איש הקשר.");
        if (phone.Length > 0 && (phone.Length < 9 || phone.Length > 11)) return new("contactPhone", "טלפון איש הקשר חייב להכיל 9–11 ספרות.");
        if (mobile.Length > 0 && (mobile.Length != 10 || !mobile.StartsWith("05", StringComparison.Ordinal)))
            return new("contactMobile", "מספר הנייד של איש הקשר חייב להכיל 10 ספרות ולהתחיל ב-05.");
        return null;
    }

    public static string? EmployeeInterfaceProfile(DateOnly? birthDate, PersonGender? gender, string? email, string? mobile,
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

        var postal = (postalCode ?? "").Trim();
        if (postal.Length > 0 && (!Digits.IsMatch(postal) || postal.Length > 7))
            return "מיקוד חייב להכיל עד 7 ספרות.";
        var box = (postOfficeBox ?? "").Trim();
        if (box.Length > 0 && (!Digits.IsMatch(box) || !int.TryParse(box, out var boxNumber) || boxNumber > 99999))
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

    public static InputError? Employee(PersonIdentifierType identifierType, string identifier, string firstName, string lastName, string employeeNumber, DateOnly startDate)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length is < 2 or > 100) return new("firstName", "שם פרטי הוא שדה חובה ובאורך 2-100 תווים.");
        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length is < 2 or > 100) return new("lastName", "שם משפחה הוא שדה חובה ובאורך 2-100 תווים.");
        if (!Enum.IsDefined(identifierType)) return new("identifierType", "סוג המזהה של העובד אינו תקין.");
        var normalizedIdentifier = identifier.Trim();
        if (identifierType == PersonIdentifierType.IsraeliId && !IsraeliNationalId.IsValid(normalizedIdentifier)) return new("identifier", "תעודת הזהות אינה תקינה.");
        if (identifierType == PersonIdentifierType.Passport
            && (normalizedIdentifier.Length is < 1 or > 16 || normalizedIdentifier.Any(char.IsWhiteSpace) || normalizedIdentifier.Any(char.IsControl)))
            return new("identifier", "מספר הדרכון הוא שדה חובה ויכול להכיל עד 16 תווים ללא רווחים.");
        if (string.IsNullOrWhiteSpace(employeeNumber) || employeeNumber.Trim().Length > 50) return new("employeeNumber", "מספר עובד הוא שדה חובה ועד 50 תווים.");
        if (startDate < new DateOnly(1950, 1, 1) || startDate > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))) return new("startDate", "תאריך תחילת העבודה אינו הגיוני.");
        return null;
    }

    public static InputError? Employee(string nationalId, string firstName, string lastName, string employeeNumber, DateOnly startDate) =>
        Employee(PersonIdentifierType.IsraeliId, nationalId, firstName, lastName, employeeNumber, startDate);
}
