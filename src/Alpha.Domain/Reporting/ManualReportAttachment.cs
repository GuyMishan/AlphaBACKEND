using System.Security.Cryptography;
using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public sealed class ManualReportAttachment : Entity
{
    private ManualReportAttachment() { }

    public ManualReportAttachment(Guid reportId, Guid? reportProductId, int documentTypeCode,
        string originalFileName, string transmissionFileName, string contentType, byte[] content)
    {
        if (reportId == Guid.Empty) throw new ArgumentException("Report is required.", nameof(reportId));
        if (documentTypeCode is not (3 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(documentTypeCode), "Negative Employer Interface 006 attachments support document types 3, 4 and 6.");
        if (documentTypeCode is 4 or 6 && reportProductId is null)
            throw new ArgumentException("Document types 4 and 6 must be linked to a report product.", nameof(reportProductId));
        if (string.IsNullOrWhiteSpace(originalFileName)) throw new ArgumentException("Original file name is required.", nameof(originalFileName));
        if (string.IsNullOrWhiteSpace(transmissionFileName) || transmissionFileName.Length > 100)
            throw new ArgumentException("Transmission file name is required and cannot exceed 100 characters.", nameof(transmissionFileName));
        if (content is null || content.Length == 0) throw new ArgumentException("Attachment content is required.", nameof(content));

        ReportId = reportId;
        ReportProductId = documentTypeCode == 3 ? null : reportProductId;
        DocumentTypeCode = documentTypeCode;
        OriginalFileName = Path.GetFileName(originalFileName.Trim());
        TransmissionFileName = Path.GetFileName(transmissionFileName.Trim());
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType.Trim();
        Content = content;
        SizeBytes = content.LongLength;
        Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    public Guid ReportId { get; private set; }
    public Guid? ReportProductId { get; private set; }
    public int DocumentTypeCode { get; private set; }
    public string OriginalFileName { get; private set; } = string.Empty;
    public string TransmissionFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = "application/pdf";
    public byte[] Content { get; private set; } = [];
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
}