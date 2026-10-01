using System.Globalization;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

public static class ReportFeedbackStatusResolver
{
    public static string ResolveReportState(
        ReportTransmissionStatus? transmissionStatus,
        int officialFeedbackCount,
        int expectedContributionCount,
        int receivedContributionCount,
        int errorContributionCount)
    {
        if (transmissionStatus is ReportTransmissionStatus.Rejected or ReportTransmissionStatus.Error
            || errorContributionCount > 0)
            return "attention";

        if (officialFeedbackCount > 0)
        {
            if (expectedContributionCount > 0 && receivedContributionCount < expectedContributionCount)
                return "partial";
            return "completed";
        }

        return transmissionStatus is null ? "not-sent" : "pending";
    }

    public static string ResolveMoneyState(
        decimal reportedDepositAmount,
        decimal actualReceivedAmount,
        decimal allocatedAmount,
        decimal inTransitAmount)
    {
        if (inTransitAmount > 0) return "in-transit";
        if (reportedDepositAmount > 0 && allocatedAmount >= reportedDepositAmount) return "allocated";
        if (actualReceivedAmount > 0 || allocatedAmount > 0) return "received-partial";
        return "unresolved";
    }

    public static bool IsEffectiveContribution(ManualContribution contribution) =>
        contribution.Amount != 0m || contribution.Percentage != 0m || contribution.ExemptPayments != 0m;
}

public static class ReportCsvFormatter
{
    public static string Escape(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (value is string)
        {
            var probe = text.TrimStart();
            if (probe.Length > 0 && probe[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
                text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static byte[] Utf8WithBom(IEnumerable<string> lines)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
        return [0xEF, 0xBB, 0xBF, .. payload];
    }
}
