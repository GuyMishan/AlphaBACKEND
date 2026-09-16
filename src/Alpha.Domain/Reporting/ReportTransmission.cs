using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public enum ReportTransmissionStatus
{
    Pending = 1,
    Sending = 2,
    Sent = 3,
    Accepted = 4,
    Rejected = 5,
    Error = 6
}

public sealed class ReportTransmission : Entity
{
    private ReportTransmission() { }

    public ReportTransmission(Guid reportId, Guid organizationId, Guid employerId, string provider, int attemptNumber)
    {
        if (attemptNumber <= 0) throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        ReportId = reportId;
        OrganizationId = organizationId;
        EmployerId = employerId;
        Provider = string.IsNullOrWhiteSpace(provider) ? throw new ArgumentException("Provider is required.", nameof(provider)) : provider.Trim();
        AttemptNumber = attemptNumber;
    }

    public Guid ReportId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public int AttemptNumber { get; private set; }
    public ReportTransmissionStatus Status { get; private set; } = ReportTransmissionStatus.Pending;
    public string ExternalId { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public string ResponsePayload { get; private set; } = string.Empty;
    public string ErrorMessage { get; private set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Start(string payloadHash)
    {
        Status = ReportTransmissionStatus.Sending;
        PayloadHash = payloadHash?.Trim() ?? string.Empty;
        StartedAt = DateTimeOffset.UtcNow;
        ErrorMessage = string.Empty;
        Touch();
    }

    public void Complete(ReportTransmissionStatus status, string? externalId, string? responsePayload, string? errorMessage)
    {
        if (status is ReportTransmissionStatus.Pending or ReportTransmissionStatus.Sending)
            throw new ArgumentOutOfRangeException(nameof(status));

        Status = status;
        ExternalId = externalId?.Trim() ?? string.Empty;
        ResponsePayload = responsePayload?.Trim() ?? string.Empty;
        ErrorMessage = errorMessage?.Trim() ?? string.Empty;
        SentAt ??= status is ReportTransmissionStatus.Sent or ReportTransmissionStatus.Accepted ? DateTimeOffset.UtcNow : null;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }
}
