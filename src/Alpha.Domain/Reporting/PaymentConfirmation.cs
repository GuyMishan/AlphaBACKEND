using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

/// <summary>Immutable payment proof; distinct from Employer Interface 006 transmission documents.</summary>
public sealed class PaymentConfirmation : Entity
{
    private PaymentConfirmation() { }
    public PaymentConfirmation(Guid reportId, Guid reportProductId, string storagePath, string originalFileName,
        string contentType, long sizeBytes, string sha256, Guid uploadedByUserId)
    {
        if (reportId == Guid.Empty || reportProductId == Guid.Empty) throw new ArgumentException("Report and product are required.");
        ReportId = reportId;
        ReportProductId = reportProductId;
        StoragePath = storagePath;
        OriginalFileName = Path.GetFileName(originalFileName);
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        UploadedByUserId = uploadedByUserId;
    }
    public Guid ReportId { get; private set; }
    public Guid ReportProductId { get; private set; }
    public string StoragePath { get; private set; } = "";
    public string OriginalFileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public Guid UploadedByUserId { get; private set; }
}
