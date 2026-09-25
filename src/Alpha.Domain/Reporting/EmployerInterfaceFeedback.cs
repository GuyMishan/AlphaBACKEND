using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public enum EmployerInterfaceDocumentType
{
    CurrentReport = 1,
    NegativeReport = 2,
    SummaryFeedback = 3,
    AnnualSummaryFeedback = 4
}

public sealed class EmployerInterfaceFeedback : Entity
{
    private EmployerInterfaceFeedback() { }

    public EmployerInterfaceFeedback(
        Guid organizationId,
        Guid employerId,
        EmployerInterfaceDocumentType documentType,
        string interfaceVersion,
        string sourceFileName,
        string payloadHash,
        string rawXml,
        string? interfaceFileNumber = null)
    {
        if (documentType is not EmployerInterfaceDocumentType.SummaryFeedback and not EmployerInterfaceDocumentType.AnnualSummaryFeedback)
            throw new ArgumentOutOfRangeException(nameof(documentType), "Only clearinghouse feedback documents can be ingested as feedback.");

        OrganizationId = organizationId;
        EmployerId = employerId;
        DocumentType = documentType;
        InterfaceVersion = Require(interfaceVersion, nameof(interfaceVersion));
        SourceFileName = Require(sourceFileName, nameof(sourceFileName));
        PayloadHash = Require(payloadHash, nameof(payloadHash));
        RawXml = Require(rawXml, nameof(rawXml));
        InterfaceFileNumber = interfaceFileNumber?.Trim() ?? string.Empty;
        ReceivedAt = DateTimeOffset.UtcNow;
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public Guid? ReportId { get; private set; }
    public Guid? TransmissionId { get; private set; }
    public EmployerInterfaceDocumentType DocumentType { get; private set; }
    public string InterfaceVersion { get; private set; } = string.Empty;
    public string SourceFileName { get; private set; } = string.Empty;
    public string InterfaceFileNumber { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public string RawXml { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    public void Correlate(Guid reportId, Guid? transmissionId)
    {
        if (reportId == Guid.Empty) throw new ArgumentException("Report is required.", nameof(reportId));
        ReportId = reportId;
        TransmissionId = transmissionId;
        Touch();
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
