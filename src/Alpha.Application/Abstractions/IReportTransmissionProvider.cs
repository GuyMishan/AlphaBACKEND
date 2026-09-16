namespace Alpha.Application.Abstractions;

public sealed record ReportTransmissionEnvelope(Guid ReportId, Guid OrganizationId, Guid EmployerId, string Payload, string PayloadHash);
public sealed record ReportTransmissionProviderResult(bool Success, string Status, string? ExternalId, string? ResponsePayload, string? ErrorMessage);

public interface IReportTransmissionProvider
{
    string Name { get; }
    Task<ReportTransmissionProviderResult> SendAsync(ReportTransmissionEnvelope envelope, CancellationToken cancellationToken);
}
