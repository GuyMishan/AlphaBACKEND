using Alpha.Application.Abstractions;

namespace Alpha.Api.Services;

public sealed class UnavailableReportTransmissionProvider : IReportTransmissionProvider
{
    public string Name => "UnconfiguredClearinghouse";
    public bool IsConfigured => false;

    public Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken ct) =>
        Task.FromResult(new ReportTransmissionProviderResult(
            false,
            null,
            null,
            "Clearing-house transmission provider is not configured."));
}
