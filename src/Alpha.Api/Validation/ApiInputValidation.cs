using System.Text.RegularExpressions;
using Alpha.Api.Contracts;
using Alpha.Api.Endpoints;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Validation;

public static class ApiInputValidation
{
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);

    public static string? Employer(string legalName, string registrationNumber, string withholdingFileNumber,
        string? contactFirstName, string? contactLastName, string? contactPhone, string? contactEmail, string? contactMobile)
    {
        if (string.IsNullOrWhiteSpace(legalName)) return "שם משפטי הוא שדה חובה.";
        if (legalName.Trim().Length is < 2 or > 200) return "שם משפטי חייב להכיל בין 2 ל-200 תווים.";
        if (string.IsNullOrWhiteSpace(registrationNumber) || !Digits.IsMatch(registrationNumber.Trim())) return "מספר חברה / עוסק חייב להכיל ספרות בלבד.";
        if (registrationNumber.Trim().Length is < 5 or > 15) return "מספר חברה / עוסק אינו באורך תקין.";
        if (string.IsNullOrWhiteSpace(withholdingFileNumber) || !Digits.IsMatch(withholdingFileNumber.Trim())) return "מספר תיק ניכויים חייב להכיל ספרות בלבד.";
        if (withholdingFileNumber.Trim().Length is < 5 or > 9) return "מספר תיק ניכויים חייב להכיל 5-9 ספרות.";
        if (string.IsNullOrWhiteSpace(contactFirstName)) return "שם פרטי של איש הקשר הוא שדה חובה.";
        if (string.IsNullOrWhiteSpace(contactLastName)) return "שם משפחה של איש הקשר הוא שדה חובה.";
        if (string.IsNullOrWhiteSpace(contactEmail) || !contactEmail.Contains('@')) return "כתובת האימייל של איש הקשר אינה תקינה.";

        var phone = new string((contactPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        var mobile = new string((contactMobile ?? string.Empty).Where(char.IsDigit).ToArray());
        if (phone.Length == 0 && mobile.Length == 0) return "יש להזין לפחות טלפון או נייד אחד של איש הקשר.";
        if (phone.Length > 11) return "טלפון איש הקשר יכול להכיל עד 11 ספרות.";
        if (mobile.Length > 15) return "מספר הנייד יכול להכיל עד 15 ספרות.";
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

    public static IReadOnlyList<string> Products(IReadOnlyCollection<ManualProductInput> products,
        IReadOnlyCollection<ContributionPercentageLimit> limits)
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
            if (product.PolicyNumber?.Trim().Length > 20) errors.Add(prefix + "מספר פוליסה/חשבון יכול להכיל עד 20 תווים לפי ממשק מעסיקים 006.");
            if (product.Salary <= 0) errors.Add(prefix + "השכר חייב להיות גדול מאפס.");
            if (product.Salary > 10_000_000) errors.Add(prefix + "השכר חורג מהטווח המותר.");
            if (product.SalaryMonth.Year < 2000 || product.SalaryMonth > DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1))) errors.Add(prefix + "חודש השכר אינו תקין.");
            if (string.IsNullOrWhiteSpace(product.ReportingType)) errors.Add(prefix + "סוג דיווח הוא שדה חובה.");
            if (string.IsNullOrWhiteSpace(product.SalaryLayer)) errors.Add(prefix + "רובד שכר הוא שדה חובה.");
            var section14Code = product.Section14Code ?? (product.Section14
                ? product.Section14StartDate.HasValue ? 2 : 1
                : product.Section14StartDate.HasValue ? 4 : 3);
            if (section14Code is < 1 or > 5)
                errors.Add(prefix + "קוד סעיף 14 אינו תקין.");
            if (section14Code is 2 or 4 && product.Section14StartDate is null)
                errors.Add(prefix + "יש להזין תאריך תחולה/ביטול לסעיף 14.");
            if (section14Code is not (2 or 4) && product.Section14StartDate is not null)
                errors.Add(prefix + "אין להעביר תאריך סעיף 14 עבור הקוד שנבחר.");
            if (product.Section14StartDate is { } section14Date && section14Date > DateOnly.FromDateTime(DateTime.UtcNow))
                errors.Add(prefix + "תאריך תחולה/ביטול סעיף 14 לא יכול להיות בעתיד.");

            if (!product.EmployerContributions.Concat(product.EmployeeContributions).Any(x => x.Amount > 0 || x.ExemptPayments != 0))
                errors.Add(prefix + "יש להזין לפחות רכיב הפקדה או תיקון תשלומים פטורים שאינו אפס.");

            ValidateContributions(errors, prefix, product.SalaryMonth.Year, product.ProductType, ContributionParty.Employer,
                product.Salary, product.EmployerContributions, limits);
            ValidateContributions(errors, prefix, product.SalaryMonth.Year, product.ProductType, ContributionParty.Employee,
                product.Salary, product.EmployeeContributions, limits);
        }
        return errors;
    }

    public static IReadOnlyList<string> Payment(SaveManualReportPaymentRequest request, decimal? totalDeposit = null,
        int? operationCode = null, int? employerAccountType = null, int? receiverAccountType = null)
    {
        var errors = new List<string>();
        var paymentMethod = request.PaymentMethod?.Trim() ?? string.Empty;
        var bankTransfer = paymentMethod is "1" or "העברה בנקאית";
        var masav = paymentMethod is "7" or "מס״ב" or "מס\"ב";
        var noMoneyCorrection = operationCode is 2 or 7;
        var effectiveDeposit = operationCode == 3 ? request.ActualDepositAmount : totalDeposit;
        var hasPositiveDeposit = !noMoneyCorrection && (!effectiveDeposit.HasValue || effectiveDeposit.Value > 0);
        var receiverAccountRequired = !noMoneyCorrection && (masav || (bankTransfer && hasPositiveDeposit));
        var employerBranchAccountRequired = (bankTransfer || masav) && hasPositiveDeposit;

        if (string.IsNullOrWhiteSpace(request.ProviderName)) errors.Add("שם יצרן / מוצר הוא שדה חובה.");
        if (receiverAccountRequired && string.IsNullOrWhiteSpace(request.ProviderAccount)) errors.Add("חשבון יצרן לזיכוי הוא שדה חובה לפי כללי אמצעי התשלום בממשק 006.");
        if (string.IsNullOrWhiteSpace(paymentMethod)) errors.Add("אופן התשלום הוא שדה חובה.");

        if (!noMoneyCorrection && receiverAccountType == 1 && paymentMethod is not ("6" or "9") && request.ValueDate is null)
            errors.Add("תאריך ערך הפקדה לקופה הוא שדה חובה כאשר החשבון הקולט הוא חשבון יצרן.");
        if (!noMoneyCorrection && employerAccountType == 2 && request.TrustAccountValueDate is null)
            errors.Add("בהעברה באמצעות חשבון נאמנות חובה להזין תאריך ערך הפקדה לחשבון הנאמנות.");
        if (operationCode == 3 && (request.ActualDepositAmount is null or <= 0))
            errors.Add("בקוד פעולה 3 יש להזין סכום הפקדה נוספת בפועל הגדול מאפס.");
        if (masav && (string.IsNullOrWhiteSpace(request.MasavSenderCode)
            || request.MasavSenderCode.Trim().Length is < 8 or > 16))
            errors.Add("בסליקה באמצעות מס״ב יש להזין קוד מס״ב פנימי באורך 8–16 תווים.");
        if (!noMoneyCorrection && hasPositiveDeposit && (paymentMethod is "1" or "העברה בנקאית" or "3")
            && string.IsNullOrWhiteSpace(request.ReferenceNumber))
            errors.Add("מספר אסמכתא בפועל הוא שדה חובה באמצעי תשלום זה.");

        if (employerBranchAccountRequired)
        {
            if (string.IsNullOrWhiteSpace(request.EmployerBankCode) || !Digits.IsMatch(request.EmployerBankCode.Trim())) errors.Add("מספר בנק חייב להכיל ספרות בלבד.");
            if (string.IsNullOrWhiteSpace(request.EmployerBranch) || !Digits.IsMatch(request.EmployerBranch.Trim())) errors.Add("מספר סניף חייב להכיל ספרות בלבד.");
            if (string.IsNullOrWhiteSpace(request.EmployerAccount) || !Digits.IsMatch(request.EmployerAccount.Trim())) errors.Add("מספר חשבון מעסיק חייב להכיל ספרות בלבד.");
        }
        if (request.ValueDate is { } valueDate && valueDate > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(31))) errors.Add("תאריך הערך רחוק מדי בעתיד.");
        return errors;
    }

    private static void ValidateContributions(List<string> errors, string prefix, int year, PensionProductType productType,
        ContributionParty party, decimal salary, IReadOnlyCollection<ManualContributionInput> items,
        IReadOnlyCollection<ContributionPercentageLimit> limits)
    {
        if (items.GroupBy(x => x.Component).Any(g => g.Count() > 1
            && !(party == ContributionParty.Employee && g.Key == ContributionComponent.Benefits)))
        {
            errors.Add(prefix + "כל רכיב הפקדה יכול להופיע פעם אחת בלבד לכל צד, למעט תגמולים 47 (קוד 4).");
            return;
        }

        foreach (var item in items)
        {
            var side = party == ContributionParty.Employer ? "מעסיק" : "עובד";
            if (item.Amount < 0 || item.Percentage < 0)
                errors.Add(prefix + $"ערכי הפקדת {side} לא יכולים להיות שליליים.");

            var limit = limits.FirstOrDefault(x => x.Year == year && x.ProductType == productType
                && x.Party == party && x.Component == item.Component);
            if (limit is null)
            {
                errors.Add(prefix + $"לא הוגדר גבול אחוזים לשנת {year}, {ProductName(productType)}, {ComponentName(item.Component)} ({side}).");
            }
            else if (item.Percentage > limit.MaxPercentage)
            {
                errors.Add(prefix + $"אחוז {ComponentName(item.Component)} של {side} חורג מהמקסימום לשנת {year} ({limit.MaxPercentage:0.##}%).");
            }

            if (item.ExemptPayments > item.Amount)
                errors.Add(prefix + $"תשלומים פטורים של {side} לא יכולים להיות גבוהים מסכום ההפקדה.");
            if (item.Amount > salary)
                errors.Add(prefix + $"סכום {ComponentName(item.Component)} של {side} לא יכול להיות גבוה מהשכר המדווח.");
        }
    }

    private static string ProductName(PensionProductType productType) => productType switch
    {
        PensionProductType.PensionFund => "קרן פנסיה",
        PensionProductType.StudyFund => "קרן השתלמות",
        PensionProductType.ManagersInsurance => "ביטוח מנהלים",
        PensionProductType.ProvidentFund => "קופת גמל",
        _ => "מוצר אחר"
    };

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
