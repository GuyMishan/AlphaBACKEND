using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;

namespace Alpha.Application.Reporting;

/// <summary>
/// Deterministic matching between immutable report snapshots and current employee pension master data.
/// A current entity is returned only when the match is unique; ambiguous candidates are never guessed.
/// </summary>
public static class FeedbackResolutionContextMatcher
{
    public static EmployeePensionProduct? FindCurrentProduct(
        ManualReportProduct reported,
        Guid employmentId,
        IReadOnlyCollection<EmployeePensionProduct> currentProducts)
    {
        var candidates = currentProducts.Where(x => x.EmploymentId == employmentId).ToArray();
        if (candidates.Length == 0) return null;

        var hasStableReference = false;

        if (!string.IsNullOrWhiteSpace(reported.PolicyNumber))
        {
            hasStableReference = true;
            var byPolicy = candidates.Where(x =>
                string.Equals(x.PolicyNumber, reported.PolicyNumber, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byPolicy.Length == 1) return byPolicy[0];
            if (byPolicy.Length > 1) return null;
        }

        if (!string.IsNullOrWhiteSpace(reported.FundExternalKey))
        {
            hasStableReference = true;
            var byExternalKey = candidates.Where(x =>
                string.Equals(x.FundExternalKey, reported.FundExternalKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byExternalKey.Length == 1) return byExternalKey[0];
            if (byExternalKey.Length > 1) return null;
        }

        if (!string.IsNullOrWhiteSpace(reported.FundCode))
        {
            hasStableReference = true;
            var byFundCode = candidates.Where(x =>
                string.Equals(x.FundCode, reported.FundCode, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byFundCode.Length == 1) return byFundCode[0];
            if (byFundCode.Length > 1) return null;
        }

        if (hasStableReference) return null;

        var byType = candidates.Where(x => x.ProductType == reported.ProductType).ToArray();
        return byType.Length == 1 ? byType[0] : null;
    }

    public static EmployeePensionContribution? FindCurrentContribution(
        ManualContribution? reported,
        EmployeePensionProduct? currentProduct,
        IReadOnlyCollection<EmployeePensionContribution> currentContributions)
    {
        if (reported is null || currentProduct is null) return null;

        var matches = currentContributions.Where(x =>
            x.EmployeePensionProductId == currentProduct.Id
            && x.Party == reported.Party
            && x.Component == reported.Component).ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }
}
