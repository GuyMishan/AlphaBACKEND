using Alpha.Domain.Reporting;

namespace Alpha.Modules.Reporting.Features.EmployerInterface006;

public sealed record EmployerInterface006FileValidation(
    bool IsValid,
    EmployerInterfaceDocumentType? DocumentType,
    string? Version,
    string? SchemaFileName,
    IReadOnlyList<string> Issues);

public sealed record EmployerInterface006GeneratedAttachment(
    string FileName,
    string ContentType,
    byte[] Content,
    string Sha256);

public sealed record EmployerInterface006GeneratedDocument(
    byte[] Bytes,
    EmployerInterface006FileValidation Validation,
    string? PayloadFileName = null,
    IReadOnlyList<EmployerInterface006GeneratedAttachment>? AttachmentFiles = null);
