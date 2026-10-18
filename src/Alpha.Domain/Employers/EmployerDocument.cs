using System.Security.Cryptography;
using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public sealed class EmployerDocument : Entity
{
    private EmployerDocument() { }

    public EmployerDocument(Guid organizationId, Guid employerId, Guid? employmentId, Guid? employeePensionProductId, int documentTypeCode, string fileName, byte[] encryptedContent, long sizeBytes, string sha256)
    {
        if (organizationId == Guid.Empty || employerId == Guid.Empty) throw new ArgumentException("Employer scope is required.");
        if (documentTypeCode is not (3 or 4 or 5 or 6 or 7)) throw new ArgumentOutOfRangeException(nameof(documentTypeCode));
        if (documentTypeCode is (3 or 7) && (employmentId.HasValue || employeePensionProductId.HasValue))
            throw new ArgumentException("Employer-wide documents cannot be assigned to an employee or pension product.");
        if (documentTypeCode is not (3 or 7) && (employmentId is null || employeePensionProductId is null))
            throw new ArgumentException("A pension product assignment is required.", nameof(employeePensionProductId));
        if (string.IsNullOrWhiteSpace(fileName) || encryptedContent.Length == 0) throw new ArgumentException("PDF is required.");
        OrganizationId = organizationId;
        EmployerId = employerId;
        EmploymentId = employmentId;
        EmployeePensionProductId = employeePensionProductId;
        DocumentTypeCode = documentTypeCode;
        OriginalFileName = Path.GetFileName(fileName);
        Content = encryptedContent;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public void SetEncryptedContent(byte[] content)
    {
        if (content.Length == 0) throw new ArgumentException("Encrypted content required.");
        Content = content;
    }

    public void AssignProduct(Guid productId)
    {
        if (EmploymentId is null || DocumentTypeCode is 3 or 7)
            throw new InvalidOperationException("Employer-wide documents cannot be assigned to pension products.");
        if (productId == Guid.Empty) throw new ArgumentException("Product ID is required.", nameof(productId));
        if (EmployeePensionProductId.HasValue)
            throw new InvalidOperationException("A product-linked document cannot be reassigned.");
        EmployeePensionProductId = productId;
        Touch();
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public Guid? EmploymentId { get; private set; }
    public Guid? EmployeePensionProductId { get; private set; }
    public int DocumentTypeCode { get; private set; }
    public string OriginalFileName { get; private set; } = "";
    public byte[] Content { get; private set; } = [];
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
}
