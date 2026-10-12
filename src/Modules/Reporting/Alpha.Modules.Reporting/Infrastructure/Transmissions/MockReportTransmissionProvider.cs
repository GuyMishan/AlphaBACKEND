using System.Text.Json;
using Alpha.Modules.Reporting.Contracts;

namespace Alpha.Modules.Reporting.Infrastructure.Transmissions;

public sealed class MockReportTransmissionProvider : IReportTransmissionProvider
{
    public string Name => "MockClearinghouse";
    public bool IsConfigured => true;

    public Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var externalId = $"MOCK-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{envelope.ReportId.ToString("N")[..8]}";
        var response = JsonSerializer.Serialize(new
        {
            accepted = true,
            externalId,
            provider = Name,
            payloadFileName = envelope.PayloadFileName,
            attachmentCount = envelope.Attachments?.Count ?? 0,
            attachments = envelope.Attachments?.Select(x => x.FileName).ToArray() ?? Array.Empty<string>()
        });
        return Task.FromResult(new ReportTransmissionProviderResult(true, "Accepted", externalId, response, null));
    }
}
