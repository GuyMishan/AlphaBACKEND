using Alpha.Modules.Reporting.Contracts;

namespace Alpha.Modules.Reporting.Infrastructure.Transmissions;

public sealed class UnavailableReportTransmissionProvider : IReportTransmissionProvider
{
    public string Name => "UnconfiguredClearinghouse";
    public bool IsConfigured => false;

    public Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken ct) =>
        Task.FromResult(new ReportTransmissionProviderResult(
            false,
            "Unavailable",
            null,
            null,
            "Clearing-house transmission provider is not configured."));
}
