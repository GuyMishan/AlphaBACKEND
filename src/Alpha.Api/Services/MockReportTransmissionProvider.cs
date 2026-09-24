using System.Text.Json;
using Alpha.Application.Abstractions;

namespace Alpha.Api.Services;

public sealed class MockReportTransmissionProvider : IReportTransmissionProvider
{
    public string Name => "MockClearinghouse";

    public Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var externalId = $"MOCK-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{envelope.ReportId.ToString("N")[..8]}";
        var response = JsonSerializer.Serialize(new
        {
            accepted = true,
            externalId,
            provider = Name,
            attachmentCount = envelope.Attachments?.Count ?? 0,
            attachments = envelope.Attachments?.Select(x => new { x.FileName, x.ContentType, x.Sha256 }).ToArray() ?? []
        });
        return Task.FromResult(new ReportTransmissionProviderResult(true, "Accepted", externalId, response, null));
    }
}
