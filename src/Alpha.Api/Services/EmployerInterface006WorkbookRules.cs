using System.Xml.Linq;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

public static class EmployerInterface006WorkbookRules
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // Official "קוד אמצעי תשלום וסוג פעולה" matrix supplied with the Version 6
    // clearinghouse rules. Operation 6 intentionally carries no payment-method
    // element in the negative interface and is validated separately.
    private static readonly IReadOnlyDictionary<int, HashSet<int>> AllowedPaymentMethodsByOperation =
        new Dictionary<int, HashSet<int>>
        {
            [1] = [1, 3, 5, 6, 7, 9],
            [2] = [1],
            [3] = [1, 3, 5, 6, 7, 9],
            [5] = [1, 3, 6, 7, 9],
            [6] = [1],
            [7] = [1]
        };

    public static IReadOnlyCollection<int> AllowedPaymentMethods(int operationCode)
    {
        if (operationCode == 6) return Array.Empty<int>();
        return AllowedPaymentMethodsByOperation.TryGetValue(operationCode, out var allowed)
            ? allowed.OrderBy(x => x).ToArray()
            : Array.Empty<int>();
    }

    public static IReadOnlyList<string> ValidateAndApply(XDocument document,
        EmployerInterface006XmlBuilder.BuildContext context, bool negative)
    {
        var issues = new List<string>();
        var groups = context.Products.GroupBy(x => new { x.FundCode, x.FundName }).ToList();
        var transfers = document.Descendants("PirteiHaavaratKsafim").ToList();
        if (transfers.Count != groups.Count)
        {
            issues.Add("Employer Interface V6: generated transfer groups do not match product groups.");
            return issues;
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var products = groups[i].ToList();
            var first = context.ProductMetadata.Single(x => x.ReportProductId == products[0].Id);
            var metas = products.Select(p => context.ProductMetadata.Single(x => x.ReportProductId == p.Id)).ToList();
            var label = $"Fund {products[0].FundCode}";

            if (metas.Any(x => x.OperationCode != first.OperationCode
                || x.PaymentMethodCode != first.PaymentMethodCode
                || x.EmployerAccountType != first.EmployerAccountType
                || x.ReceiverAccountType != first.ReceiverAccountType
                || !StringEquals(x.PreviousIdentifier, first.PreviousIdentifier)
                || !StringEquals(x.PreviousClearingIdentifier, first.PreviousClearingIdentifier)
                || x.PreviousReferenceExceptionCode != first.PreviousReferenceExceptionCode))
            {
                issues.Add($"{label}: products grouped into one transfer must use the same operation, payment and previous-report reference data.");
                continue;
            }

            var requiresPrevious = negative
                ? first.OperationCode is 5 or 6
                : first.OperationCode is 2 or 3 or 7;
            var hasPrevious = !string.IsNullOrWhiteSpace(first.PreviousIdentifier)
                || !string.IsNullOrWhiteSpace(first.PreviousClearingIdentifier);
            if (requiresPrevious && !hasPrevious && first.PreviousReferenceExceptionCode is null)
                issues.Add($"{label}: operation {first.OperationCode} must reference the original report using MISPAR-ZIHUI-KODEM or MISPAR-MISLAKA-KODEM, or select one of the official Version 6 exceptions.");

            if (first.PreviousReferenceExceptionCode is not null && first.PreviousReferenceExceptionCode is not (1 or 2 or 3))
                issues.Add($"{label}: unsupported previous-report reference exception code.");

            if (first.OperationCode is int operationCode)
            {
                if (first.PaymentMethodCode is not int paymentMethodCode)
                {
                    issues.Add($"{label}: operation {operationCode} requires a payment method according to the official Version 6 operation/payment matrix.");
                }
                else if (!AllowedPaymentMethodsByOperation.TryGetValue(operationCode, out var allowedPaymentMethods)
                    || !allowedPaymentMethods.Contains(paymentMethodCode))
                {
                    issues.Add($"{label}: payment method {paymentMethodCode} is not allowed for operation {operationCode} according to the official Version 6 operation/payment matrix.");
                }
            }

            if (negative) NormalizeNegativeTransfer(transfers[i], first);
            SetNullable(transfers[i].Element("MISPAR-ZIHUI-KODEM"), requiresPrevious ? first.PreviousIdentifier : null);
            SetNullable(transfers[i].Element("MISPAR-MISLAKA-KODEM"), requiresPrevious ? first.PreviousClearingIdentifier : null);
        }

        return issues;
    }

    private static void NormalizeNegativeTransfer(XElement transfer, EmployerInterfaceReportProductData metadata)
    {
        var id = transfer.Element("MISPAR-ZIHUI");
        if (id is null) return;

        EnsureBefore(id, "TAARICH-ERECH-HAFKADA-LEKUPA");
        EnsureBefore(id, "TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT");
        EnsureBefore(id, "MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM");

        var previous = transfer.Element("MISPAR-ZIHUI-KODEM");
        if (previous is null) return;
        EnsureBefore(previous, "MISPAR-BANK-MAASIK");
        EnsureBefore(previous, "MISPAR-SNIF-MAASIK");
        EnsureBefore(previous, "MISPAR-CHESHBON-MAASIK");
        EnsureBefore(previous, "SUG-CHESHBON");
        EnsureBefore(previous, "SUG-CHESHBON-MAASIK");
        EnsureBefore(previous, "SUG-CHESHBON-KOLET-TASHLUM");
        EnsureBefore(previous, "MISPAR-BANK-KOLET");
        EnsureBefore(previous, "MISPAR-SNIF-KOLET");
        EnsureBefore(previous, "MISPAR-CHESHBON-KOLET");

        // KOD-EMTZAI-TASHLUM is required by the negative 006 XSD. The official
        // operation/payment matrix allows operation 6 only with payment method 1.
    }

    private static void EnsureBefore(XElement anchor, string name)
    {
        var parent = anchor.Parent!;
        if (parent.Element(name) is not null) return;
        anchor.AddBeforeSelf(NilElement(name));
    }

    private static XElement NilElement(string name) => new(name, new XAttribute(Xsi + "nil", "true"));

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void SetNullable(XElement? element, string? value)
    {
        if (element is null) return;
        element.RemoveAttributes();
        if (string.IsNullOrWhiteSpace(value))
        {
            element.RemoveNodes();
            element.SetAttributeValue(Xsi + "nil", "true");
        }
        else
        {
            element.Value = value.Trim().ToUpperInvariant();
        }
    }
}
