using Alpha.Application.Abstractions;

namespace Alpha.Api.Services;

public sealed class MockReportTransmissionProvider : IReportTransmissionProvider
{
    public string Name => "MockClearinghouse";

    public Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var externalId = $"MOCK-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{envelope.ReportId.ToString("N")[..8]}";
        var response = $$"{"accepted":true,"externalId":"{{externalId}}","provider":"{{Name}}"}";
        return Task.FromResult(new ReportTransmissionProviderResult(true, "Accepted", externalId, response, null));
    }
}
