using System.Xml.Linq;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

public static class EmployerInterface006WorkbookRules
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

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
            {
                issues.Add($"{label}: operation {first.OperationCode} must reference the original report using MISPAR-ZIHUI-KODEM or MISPAR-MISLAKA-KODEM, or select one of the official Version 6 exceptions.");
            }

            if (first.PreviousReferenceExceptionCode is not null && first.PreviousReferenceExceptionCode is not (1 or 2 or 3))
                issues.Add($"{label}: unsupported previous-report reference exception code.");

            SetNullable(transfers[i].Element("MISPAR-ZIHUI-KODEM"), first.PreviousIdentifier);
            SetNullable(transfers[i].Element("MISPAR-MISLAKA-KODEM"), first.PreviousClearingIdentifier);
        }

        return issues;
    }

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
