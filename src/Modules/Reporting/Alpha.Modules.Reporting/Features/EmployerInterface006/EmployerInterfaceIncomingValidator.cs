using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using Alpha.Domain.Reporting;

namespace Alpha.Modules.Reporting.Features.EmployerInterface006;

public sealed class EmployerInterfaceIncomingValidator(EmployerInterfaceSchemaRegistry schemas)
{
    public const string CurrentVersion = EmployerInterfaceSchemaRegistry.Version;

    public EmployerInterface006FileValidation Validate(byte[] xmlBytes)
    {
        var detected = schemas.DetectAndValidate(xmlBytes);
        if (!detected.IsValid || detected.DocumentType is null)
            return new EmployerInterface006FileValidation(false, detected.DocumentType, null, detected.SchemaFileName, detected.Issues);

        try
        {
            var document = EmployerInterfaceSchemaRegistry.LoadXml(xmlBytes);
            var version = Value(document, "MISPAR-GIRSAT-XML");
            if (!string.Equals(version, CurrentVersion, StringComparison.Ordinal))
                return new EmployerInterface006FileValidation(false, detected.DocumentType, version, detected.SchemaFileName,
                    [$"Expected Employer Interface version {CurrentVersion}, received {version ?? "missing"}."]);
            var businessIssues = ValidateIncomingBusinessRules(document, detected.DocumentType.Value);
            return new EmployerInterface006FileValidation(businessIssues.Count == 0, detected.DocumentType, version, detected.SchemaFileName, businessIssues);
        }
        catch (Exception ex)
        {
            return new EmployerInterface006FileValidation(false, detected.DocumentType, null, detected.SchemaFileName, [ex.Message]);
        }
    }

    private static IReadOnlyList<string> ValidateIncomingBusinessRules(XDocument document, EmployerInterfaceDocumentType type)
    {
        if (type is not (EmployerInterfaceDocumentType.CurrentReport or EmployerInterfaceDocumentType.NegativeReport))
            return [];

        var issues = new List<string>();
        var negative = type == EmployerInterfaceDocumentType.NegativeReport;
        foreach (var transfer in Desc(document, "PirteiHaavaratKsafim"))
        {
            var mobile = Digits(Value(transfer, "MISPAR-CELLULARI-ISH-KESHER-MAASIK"));
            if (mobile.Length != 10 || !mobile.StartsWith("05", StringComparison.Ordinal))
                issues.Add("MISPAR-CELLULARI-ISH-KESHER-MAASIK must be a 10-digit Israeli mobile number beginning with 05; use 0500000000 only when the employer has no mobile.");

            var operation = IntValue(transfer, "SUG-PEULA");
            var paymentMethod = IntValue(transfer, "KOD-EMTZAI-TASHLUM");
            var deposit = DecimalValue(transfer, "SACH-HAFKADA-KUPA-H-P") ?? 0m;

            if (operation.HasValue)
            {
                if (negative && operation is not (5 or 6))
                    issues.Add($"Negative report contains invalid SUG-PEULA={operation}.");
                if (!negative && operation is not (1 or 2 or 3 or 7))
                    issues.Add($"Current report contains invalid SUG-PEULA={operation}.");

                if (!EmployerInterface006WorkbookRules.IsPaymentMethodAllowed(operation.Value, paymentMethod))
                    issues.Add(operation == 6
                        ? "SUG-PEULA=6 must not contain a KOD-EMTZAI-TASHLUM value."
                        : $"KOD-EMTZAI-TASHLUM is missing or incompatible with SUG-PEULA={operation}.");
            }

            if (operation is 2 or 7 && deposit != 0m)
                issues.Add($"SUG-PEULA={operation} is a no-money correction and SACH-HAFKADA-KUPA-H-P must be 0.");
            if (operation == 6 && deposit != 0m)
                issues.Add("Negative SUG-PEULA=6 is a cancellation without a refund and SACH-HAFKADA-KUPA-H-P must be 0.");

            if (!negative)
            {
                var employerAccountType = IntValue(transfer, "SUG-CHESHBON-MAASIK");
                var receiverAccountType = IntValue(transfer, "SUG-CHESHBON-KOLET-TASHLUM");

                if (paymentMethod == 9 && (employerAccountType != 1 || receiverAccountType != 1))
                    issues.Add("KOD-EMTZAI-TASHLUM=9 requires SUG-CHESHBON-MAASIK=1 and SUG-CHESHBON-KOLET-TASHLUM=1.");

                var noMoneyCorrection = operation is 2 or 7;
                var trustAccountRelevant = !noMoneyCorrection && (employerAccountType == 2 || receiverAccountType == 2);
                var trustValueDate = Value(transfer, "TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT");
                if (trustAccountRelevant && string.IsNullOrWhiteSpace(trustValueDate))
                    issues.Add("A transfer to or from a trust account requires TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT.");
                if (noMoneyCorrection && !string.IsNullOrWhiteSpace(trustValueDate))
                    issues.Add($"SUG-PEULA={operation} must not include a trust-account value date.");

                if (operation == 7)
                {
                    var rows = Desc(transfer, "PizulHafrashotOvedBeKupa").ToList();
                    if (rows.Any(x => (DecimalValue(x, "SCHUM-HAFRASHA") ?? 0m) != 0m))
                        issues.Add("SUG-PEULA=7 corrects exempt payments only; SCHUM-HAFRASHA must be 0 for every row.");
                    if (rows.All(x => (DecimalValue(x, "SACH-TASHLUMIM-PTURIM") ?? 0m) == 0m))
                        issues.Add("SUG-PEULA=7 requires at least one non-zero SACH-TASHLUMIM-PTURIM correction.");
                    if (employerAccountType != 1)
                        issues.Add("SUG-PEULA=7 requires SUG-CHESHBON-MAASIK=1.");
                }

                var zeroEmployer = deposit == 0m || paymentMethod is 3 or 5 or 6 or 9;
                var employerBranch = Digits(Value(transfer, "MISPAR-SNIF-MAASIK"));
                var employerAccount = Digits(Value(transfer, "MISPAR-CHESHBON-MAASIK"));
                if (zeroEmployer && ((employerBranch.Length > 0 && employerBranch.Any(ch => ch != '0'))
                    || (employerAccount.Length > 0 && employerAccount.Any(ch => ch != '0'))))
                    issues.Add("Employer branch/account must be zero when no money is transferred or payment method is 3, 5, 6 or 9.");

                var receiverRequired = (paymentMethod == 1 && deposit > 0m) || paymentMethod == 7;
                var receiverBank = IntValue(transfer, "MISPAR-BANK-KOLET");
                var receiverBranch = Digits(Value(transfer, "MISPAR-SNIF-KOLET"));
                var receiverAccount = Digits(Value(transfer, "MISPAR-CHESHBON-KOLET"));
                if (receiverRequired && (receiverBank is null or <= 0 || receiverBranch.Length == 0
                    || receiverBranch.All(ch => ch == '0') || receiverAccount.Length == 0 || receiverAccount.All(ch => ch == '0')))
                    issues.Add("Receiving bank, branch and account are required for bank transfer with money and for MASAV payment method 7.");

                var ids = Desc(transfer, "PirteiOved")
                    .Select(x => new
                    {
                        Type = IntValue(x, "SUG-MEZAHE-OVED"),
                        Identifier = Value(x, "MISPAR-MEZAHE")?.Trim() ?? string.Empty
                    })
                    .Where(x => x.Identifier.Length > 0)
                    .Select(x => $"{x.Type}:{x.Identifier}")
                    .ToArray();
                if (ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    issues.Add("PirteiOved must appear only once per employee identifier/type within a transfer batch.");
            }
        }

        if (!negative)
        {
            var repeatedEmployees = Desc(document, "PirteiOved")
                .Select(employee => new
                {
                    Key = $"{IntValue(employee, "SUG-MEZAHE-OVED")}:{Value(employee, "MISPAR-MEZAHE")?.Trim()}",
                    Snapshot = string.Join("|",
                        Value(employee, "SHEM-PRATI")?.Trim() ?? "",
                        Value(employee, "SHEM-MISHPACHA")?.Trim() ?? "",
                        Value(employee, "TAARICH-LEIDA")?.Trim() ?? "",
                        Value(employee, "MISPAR-OVED-ETZEL-MAASIK")?.Trim() ?? "",
                        Value(employee, "SHEM-YISHUV")?.Trim() ?? "",
                        Value(employee, "SHEM-RECHOV")?.Trim() ?? "",
                        Value(employee, "MISPAR-BAIT")?.Trim() ?? "",
                        Value(employee, "MISPAR-DIRA")?.Trim() ?? "",
                        Value(employee, "MIKUD")?.Trim() ?? "",
                        Value(employee, "TA-DOAR")?.Trim() ?? "",
                        Value(employee, "E-MAIL")?.Trim() ?? "",
                        Digits(Value(employee, "MISPAR-CELLULARI")),
                        Value(employee, "MIN")?.Trim() ?? "",
                        Value(employee, "MOED-TCHILAT-AHASAKAT-OVED")?.Trim() ?? "")
                })
                .Where(x => !x.Key.EndsWith(":", StringComparison.Ordinal))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var employee in repeatedEmployees)
            {
                if (employee.Select(x => x.Snapshot).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    issues.Add($"Employee {employee.Key} appears in multiple transfer blocks with conflicting snapshot data.");
            }
        }

        return issues;
    }


    private static IEnumerable<XElement> Desc(XContainer? container, params string[] names) =>
        container?.Descendants().Where(element => names.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase)) ?? [];

    private static string? Value(XContainer? container, params string[] names) =>
        Desc(container, names).Select(element => element.Value.Trim()).FirstOrDefault(value => value.Length > 0);

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static int? IntValue(XContainer? container, params string[] names) =>
        int.TryParse(Value(container, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal? DecimalValue(XContainer? container, params string[] names) =>
        decimal.TryParse(Value(container, names), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public static class EmployerInterfacePayload
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

