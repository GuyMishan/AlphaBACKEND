using System.Text;
using System.Xml;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Services;

public sealed class EmployerInterface006ExportService(
    IAlphaDbContext db,
    EmployerInterfaceSchemaRegistry schemas,
    IOptions<EmployerInterface006Options> options)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public async Task<EmployerInterfaceService.GeneratedDocument> ExportAsync(ManualReport report, CancellationToken ct)
    {
        if (report.ReportKind == ManualReportKind.Differences)
            return Invalid(EmployerInterfaceDocumentType.CurrentReport,
                "Difference reports cannot be transmitted directly. Materialize the difference as a current or negative report first.");

        var documentType = report.ReportKind == ManualReportKind.Negative
            ? EmployerInterfaceDocumentType.NegativeReport
            : EmployerInterfaceDocumentType.CurrentReport;

        var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == report.EmployerId, ct);
        var profileSettings = await db.EmployerProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == report.EmployerId, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var personIds = employees.Select(x => x.PersonId).Distinct().ToArray();
        var employmentIds = employees.Select(x => x.EmploymentId).Distinct().ToArray();
        var people = await db.People.AsNoTracking().Where(x => personIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var employments = await db.Employments.AsNoTracking().Where(x => employmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.AllocationOrder).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var attachments = await db.ManualReportAttachments.AsNoTracking()
            .Where(x => x.ReportId == report.Id).ToListAsync(ct);

        var annualEmployerAffidavitSatisfied = attachments.Any(x => x.DocumentTypeCode == 3);
        if (!annualEmployerAffidavitSatisfied && report.ReportKind == ManualReportKind.Negative)
        {
            var yearStart = new DateOnly(report.ReportingMonth.Year, 1, 1);
            var yearEnd = yearStart.AddYears(1);
            annualEmployerAffidavitSatisfied = await (
                from attachment in db.ManualReportAttachments.AsNoTracking()
                join previousReport in db.ManualReports.AsNoTracking() on attachment.ReportId equals previousReport.Id
                where attachment.DocumentTypeCode == 3
                      && previousReport.Id != report.Id
                      && previousReport.EmployerId == report.EmployerId
                      && previousReport.ReportingMonth >= yearStart
                      && previousReport.ReportingMonth < yearEnd
                      && (previousReport.Status == ManualReportStatus.Sent || previousReport.Status == ManualReportStatus.Completed)
                select attachment.Id).AnyAsync(ct);
        }

        var preparedAt = DateTimeOffset.UtcNow;
        var packageName = EmployerInterface006FileNaming.Build(employer,
            documentType == EmployerInterfaceDocumentType.NegativeReport, preparedAt);
        var attachmentNames = attachments
            .OrderBy(x => x.DocumentTypeCode).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select((attachment, index) => new
            {
                attachment.Id,
                FileName = EmployerInterface006FileNaming.BuildAttachmentFileName(packageName.BaseName, index + 1,
                    Path.GetExtension(attachment.OriginalFileName).TrimStart('.'))
            })
            .ToDictionary(x => x.Id, x => x.FileName);

        var context = new EmployerInterface006XmlBuilder.BuildContext(employer, employees, people, employments,
            products, contributions, payments, metadata, options.Value, profileSettings?.DefaultDepositorTypeCode ?? 1,
            attachments, annualEmployerAffidavitSatisfied, preparedAt, attachmentNames);
        var negative = documentType == EmployerInterfaceDocumentType.NegativeReport;
        var built = negative
            ? EmployerInterface006XmlBuilder.BuildNegative(context)
            : EmployerInterface006XmlBuilder.BuildCurrent(context);
        if (built.Document is null)
            return new([], new(false, documentType, EmployerInterfaceSchemaRegistry.Version, null, built.Issues));

        var workbookIssues = EmployerInterface006WorkbookRules.ValidateAndApply(built.Document, context, negative);
        if (workbookIssues.Count > 0)
            return new([], new(false, documentType, EmployerInterfaceSchemaRegistry.Version, null, workbookIssues));

        var bytes = Serialize(built.Document);
        var validation = schemas.Validate(bytes, documentType);
        var attachmentFiles = attachments
            .OrderBy(x => x.DocumentTypeCode).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new EmployerInterfaceService.GeneratedAttachment(
                attachmentNames[x.Id], x.ContentType, x.Content, x.Sha256))
            .ToArray();
        return new(bytes, new(validation.IsValid, documentType, EmployerInterfaceSchemaRegistry.Version,
            validation.SchemaFileName, validation.Issues), packageName.PayloadFileName, attachmentFiles);
    }

    private static byte[] Serialize(System.Xml.Linq.XDocument document)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = false,
            OmitXmlDeclaration = false,
            NewLineHandling = NewLineHandling.None
        };
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        return stream.ToArray();
    }

    private static EmployerInterfaceService.GeneratedDocument Invalid(EmployerInterfaceDocumentType type, string issue) =>
        new([], new(false, type, EmployerInterfaceSchemaRegistry.Version, null, [issue]));
}
