using System.Security.Cryptography;
using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public sealed class EmployerDocument : Entity
{
    private EmployerDocument() { }

    public EmployerDocument(Guid organizationId, Guid employerId, Guid? employmentId, int documentTypeCode, string fileName, byte[] encryptedContent, long sizeBytes, string sha256)
    {
        if (organizationId == Guid.Empty || employerId == Guid.Empty) throw new ArgumentException("Employer scope is required.");
        if (documentTypeCode is not (3 or 4 or 5 or 6)) throw new ArgumentOutOfRangeException(nameof(documentTypeCode));
        if (documentTypeCode != 3 && employmentId is null) throw new ArgumentException("Employee assignment is required.", nameof(employmentId));
        if (string.IsNullOrWhiteSpace(fileName) || encryptedContent.Length == 0) throw new ArgumentException("PDF is required.");
        OrganizationId = organizationId;
        EmployerId = employerId;
        EmploymentId = employmentId;
        DocumentTypeCode = documentTypeCode;
        OriginalFileName = Path.GetFileName(fileName);
        Content = encryptedContent;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public Guid? EmploymentId { get; private set; }
    public int DocumentTypeCode { get; private set; }
    public string OriginalFileName { get; private set; } = "";
    public byte[] Content { get; private set; } = [];
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
}
