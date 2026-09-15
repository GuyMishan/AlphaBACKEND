using System.Text.RegularExpressions;
using Alpha.Api.Contracts;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Validation;

public static class ApiInputValidation
{
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);

    public static string? Employer(string legalName, string registrationNumber, string withholdingFileNumber)
    {
        if (string.IsNullOrWhiteSpace(legalName)) return "שם משפטי הוא שדה חובה.";
        if (legalName.Trim().Length is < 2 or > 200) return "שם משפטי חייב להכיל בין 2 ל-200 תווים.";
        if (string.IsNullOrWhiteSpace(registrationNumber) || !Digits.IsMatch(registrationNumber.Trim())) return "מספר חברה / עוסק חייב להכיל ספרות בלבד.";
        if (registrationNumber.Trim().Length is < 5 or > 15) return "מספר חברה / עוסק אינו באורך תקין.";
        if (string.IsNullOrWhiteSpace(withholdingFileNumber) || !Digits.IsMatch(withholdingFileNumber.Trim())) return "מספר תיק ניכויים חייב להכיל ספרות בלבד.";
        if (withholdingFileNumber.Trim().Length is < 5 or > 15) return "מספר תיק ניכויים אינו באורך תקין.";
        return null;
    }

    public static string? Employee(string nationalId, string firstName, string lastName, string employeeNumber, DateOnly startDate)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length is < 2 or > 100) return "שם פרטי הוא שדה חובה ובאורך 2-100 תווים.";
        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length is < 2 or > 100) return "שם משפחה הוא שדה חובה ובאורך 2-100 תווים.";
        var id = nationalId.Trim();
        if (!IsIsraeliId(id)) return "תעודת הזהות אינה תקינה.";
        if (string.IsNullOrWhiteSpace(employeeNumber) || employeeNumber.Trim().Length > 50) return "מספר עובד הוא שדה חובה ועד 50 תווים.";
        if (startDate < new DateOnly(1950, 1, 1) || startDate > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))) return "תאריך תחילת העבודה אינו הגיוני.";
        return null;
    }

    public static IReadOnlyList<string> Products(IReadOnlyCollection<ManualProductInput> products)
    {
        var errors = new List<string>();
        if (products.Count == 0) errors.Add("יש להגדיר לפחות מוצר פנסיוני אחד לעובד.");
        var duplicatePolicies = products.Where(x => !string.IsNullOrWhiteSpace(x.PolicyNumber))
            .GroupBy(x => $"{(int)x.ProductType}:{x.PolicyNumber.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() > 1);
        if (duplicatePolicies) errors.Add("אותו מספר פוליסה מופיע יותר מפעם אחת אצל העובד.");

        var index = 0;
        foreach (var product in products)
        {
            index++;
            var prefix = $"מוצר {index}: ";
            if (string.IsNullOrWhiteSpace(product.PolicyNumber)) errors.Add(prefix + "מספר פוליסה הוא שדה חובה.");
            if (product.PolicyNumber?.Trim().Length > 100) errors.Add(prefix + "מספר פוליסה ארוך מדי.");
            if (product.Salary <= 0) errors.Add(prefix + "השכר חייב להיות גדול מאפס.");
            if (product.Salary > 10_000_000) errors.Add(prefix + "השכר חורג מהטווח המותר.");
            if (product.SalaryMonth.Year < 2000 || product.SalaryMonth > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))) errors.Add(prefix + "חודש השכר אינו תקין.");
            if (string.IsNullOrWhiteSpace(product.ReportingType)) errors.Add(prefix + "סוג דיווח הוא שדה חובה.");
            if (string.IsNullOrWhiteSpace(product.SalaryLayer)) errors.Add(prefix + "רובד שכר הוא שדה חובה.");
            if (product.Section14 && product.Section14StartDate is null) errors.Add(prefix + "יש להזין תאריך תחילת סעיף 14.");
            if (product.Section14StartDate is { } section14Date && section14Date > DateOnly.FromDateTime(DateTime.UtcNow)) errors.Add(prefix + "תאריך תחילת סעיף 14 לא יכול להיות בעתיד.");

            ValidateContributions(errors, prefix, product.ProductType, ContributionParty.Employer, product.Salary, product.EmployerContributions);
            ValidateContributions(errors, prefix, product.ProductType, ContributionParty.Employee, product.Salary, product.EmployeeContributions);
        }
        return errors;
    }

    public static IReadOnlyList<string> Payment(SaveManualReportPaymentRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.ProviderName)) errors.Add("שם יצרן / מוצר הוא שדה חובה.");
        if (string.IsNullOrWhiteSpace(request.ProviderAccount)) errors.Add("חשבון יצרן לזיכוי הוא שדה חובה.");
        if (string.IsNullOrWhiteSpace(request.PaymentMethod)) errors.Add("אופן התשלום הוא שדה חובה.");
        if (request.PaymentMethod == "העברה בנקאית" || request.PaymentMethod == "מס״ב")
        {
            if (request.ValueDate is null) errors.Add("תאריך ערך הוא שדה חובה בהעברה בנקאית / מס״ב.");
            if (string.IsNullOrWhiteSpace(request.ReferenceNumber)) errors.Add("מספר אסמכתא הוא שדה חובה בהעברה בנקאית / מס״ב.");
            if (string.IsNullOrWhiteSpace(request.EmployerBankCode) || !Digits.IsMatch(request.EmployerBankCode.Trim())) errors.Add("מספר בנק חייב להכיל ספרות בלבד.");
            if (string.IsNullOrWhiteSpace(request.EmployerBranch) || !Digits.IsMatch(request.EmployerBranch.Trim())) errors.Add("מספר סניף חייב להכיל ספרות בלבד.");
            if (string.IsNullOrWhiteSpace(request.EmployerAccount) || !Digits.IsMatch(request.EmployerAccount.Trim())) errors.Add("מספר חשבון מעסיק חייב להכיל ספרות בלבד.");
        }
        if (request.ValueDate is { } valueDate && valueDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(31))) errors.Add("תאריך הערך רחוק מדי בעתיד.");
        return errors;
    }

    private static void ValidateContributions(List<string> errors, string prefix, PensionProductType productType,
        ContributionParty party, decimal salary, IReadOnlyCollection<ManualContributionInput> items)
    {
        if (items.GroupBy(x => x.Component).Any(g => g.Count() > 1))
        {
            errors.Add(prefix + "כל רכיב הפקדה יכול להופיע פעם אחת בלבד לכל צד.");
            return;
        }

        foreach (var item in items)
        {
            var side = party == ContributionParty.Employer ? "מעסיק" : "עובד";
            if (item.Amount < 0 || item.Percentage < 0 || item.ExemptPayments < 0)
                errors.Add(prefix + $"ערכי הפקדת {side} לא יכולים להיות שליליים.");
            if (item.Percentage > MaxPercentage(productType, party, item.Component))
                errors.Add(prefix + $"אחוז {ComponentName(item.Component)} של {side} חורג מהמקסימום המותר ({MaxPercentage(productType, party, item.Component):0.##}%).");
            if (item.ExemptPayments > item.Amount)
                errors.Add(prefix + $"תשלומים פטורים של {side} לא יכולים להיות גבוהים מסכום ההפקדה.");
            if (item.Amount > salary)
                errors.Add(prefix + $"סכום {ComponentName(item.Component)} של {side} לא יכול להיות גבוה מהשכר המדווח.");
        }
    }

    private static decimal MaxPercentage(PensionProductType productType, ContributionParty party, ContributionComponent component)
    {
        if (component == ContributionComponent.Other) return 100m;
        if (party == ContributionParty.Employer)
        {
            if (component == ContributionComponent.Severance) return 8.33m;
            if (component == ContributionComponent.Disability) return 2.5m;
            if (component == ContributionComponent.Benefits) return 7.5m;
        }
        else if (component == ContributionComponent.Benefits)
        {
            return productType == PensionProductType.StudyFund ? 2.5m : 7m;
        }
        return 100m;
    }

    private static string ComponentName(ContributionComponent component) => component switch
    {
        ContributionComponent.Severance => "פיצויים",
        ContributionComponent.Benefits => "תגמולים",
        ContributionComponent.Disability => "אכ״ע",
        _ => "שונות"
    };

    public static bool IsIsraeliId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(ch => !char.IsDigit(ch)) || value.Length > 9) return false;
        var padded = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var n = (padded[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += n > 9 ? n - 9 : n;
        }
        return sum % 10 == 0;
    }
}