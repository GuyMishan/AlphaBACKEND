namespace Alpha.Application.Abstractions;

public sealed record ReportTransmissionAttachment(string FileName, string ContentType, byte[] Content, string Sha256);
public sealed record ReportTransmissionEnvelope(Guid ReportId, Guid OrganizationId, Guid EmployerId, byte[] Payload, string PayloadHash,
    IReadOnlyList<ReportTransmissionAttachment>? Attachments = null);
public sealed record ReportTransmissionProviderResult(bool Success, string Status, string? ExternalId, string? ResponsePayload, string? ErrorMessage);

public interface IReportTransmissionProvider
{
    string Name { get; }
    Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken cancellationToken);
}
