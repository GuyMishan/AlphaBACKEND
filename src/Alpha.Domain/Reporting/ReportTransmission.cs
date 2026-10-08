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
    // Empty route key retains the legacy one-file-per-report contract.
    public string RoutingKey { get; private set; } = string.Empty;
    public string RoutedProductIdsJson { get; private set; } = "[]";

    public int AttemptNumber { get; private set; }
    public ReportTransmissionStatus Status { get; private set; } = ReportTransmissionStatus.Pending;
    public string ExternalId { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public string PayloadFileName { get; private set; } = string.Empty;
    public byte[] Payload { get; private set; } = [];
    public string AttachmentManifestJson { get; private set; } = "[]";
    public string ResponsePayload { get; private set; } = string.Empty;
    public string ErrorMessage { get; private set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void ConfigureRoute(string routingKey, IReadOnlyCollection<Guid> productIds)
    {
        if (Status != ReportTransmissionStatus.Pending || StartedAt.HasValue || Payload.Length > 0)
            throw new InvalidOperationException("Transmission route cannot be changed after preparation or dispatch.");
        if (string.IsNullOrWhiteSpace(routingKey) || routingKey.Length > 120)
            throw new ArgumentException("Routing key is required.", nameof(routingKey));
        if (productIds.Count == 0 || productIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("At least one valid routed product is required.", nameof(productIds));
        RoutingKey = routingKey.Trim();
        RoutedProductIdsJson = System.Text.Json.JsonSerializer.Serialize(productIds.Distinct().Order().ToArray());
        Touch();
    }

    public void Prepare(string payloadHash, string payloadFileName, byte[] payload, string? attachmentManifestJson = null)
    {
        if (Status != ReportTransmissionStatus.Pending)
            throw new InvalidOperationException("Cannot replace a dispatched transmission.");
        if (string.IsNullOrWhiteSpace(payloadFileName)) throw new ArgumentException("Payload file name is required.", nameof(payloadFileName));
        if (payload is null || payload.Length == 0) throw new ArgumentException("Payload is required.", nameof(payload));
        PayloadHash = payloadHash?.Trim() ?? string.Empty;
        PayloadFileName = payloadFileName.Trim();
        Payload = payload.ToArray();
        AttachmentManifestJson = string.IsNullOrWhiteSpace(attachmentManifestJson) ? "[]" : attachmentManifestJson.Trim();
        ErrorMessage = string.Empty;
        Touch();
    }

    public void BeginDispatch()
    {
        if (Status != ReportTransmissionStatus.Pending || Payload.Length == 0)
            throw new InvalidOperationException("Only a prepared pending transmission may be dispatched.");
        Status = ReportTransmissionStatus.Sending;
        StartedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Start(string payloadHash, string payloadFileName, byte[] payload, string? attachmentManifestJson = null)
    {
        Prepare(payloadHash, payloadFileName, payload, attachmentManifestJson);
        BeginDispatch();
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
