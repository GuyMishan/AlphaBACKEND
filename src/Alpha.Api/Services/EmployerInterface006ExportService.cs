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

    public async Task<EmployerInterfaceService.GeneratedDocument> ExportAsync(ManualReport report, CancellationToken ct,
        int fileSequence = 1, DateTimeOffset? preparedAtOverride = null)
    {
        if (report.ReportKind == ManualReportKind.Differences)
            return Invalid(EmployerInterfaceDocumentType.CurrentReport,
                "Difference reports cannot be transmitted directly. Materialize the difference as a current or negative report first.");

        var documentType = report.ReportKind == ManualReportKind.Negative
            ? EmployerInterfaceDocumentType.NegativeReport
            : EmployerInterfaceDocumentType.CurrentReport;

        var employer = new Alpha.Domain.Employers.Employer(report.OrganizationId,
            report.EmployerLegalNameSnapshot, report.EmployerRegistrationNumberSnapshot,
            report.EmployerWithholdingFileNumberSnapshot, report.EmployerContactFirstNameSnapshot,
            report.EmployerContactLastNameSnapshot, report.EmployerContactPhoneSnapshot,
            report.EmployerContactEmailSnapshot, report.EmployerContactMobileSnapshot);
        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var people = new Dictionary<Guid, Alpha.Domain.Employees.Person>();
        var employments = new Dictionary<Guid, Alpha.Domain.Employees.Employment>();
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);

        // Complete standard current-report metadata in memory so a normal deposit row does not
        // require opening/saving its payment modal before final validation or transmission.
        if (report.ReportKind == ManualReportKind.Current)
        {
            var employmentIds = employees.Select(x => x.EmploymentId).Distinct().ToArray();
            var employmentRows = await db.Employments.AsNoTracking()
                .Where(x => employmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var mandate = report.PaymentAccountId.HasValue
                ? await db.BankDebitMandates.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.EmployerPaymentAccountId == report.PaymentAccountId.Value, ct)
                : null;
            var managedDebit = mandate?.IsActive == true;
            var byProduct = metadata.ToDictionary(x => x.ReportProductId);

            foreach (var product in products)
            {
                var employee = employees.First(x => x.Id == product.ReportEmployeeId);
                employmentRows.TryGetValue(employee.EmploymentId, out var employment);
                var monthEnd = report.ReportingMonth.AddMonths(1).AddDays(-1);
                var endedByMonth = employment?.EndDate is DateOnly endDate && endDate <= monthEnd;
                var startedInMonth = employee.EmploymentStartDateSnapshot is DateOnly startDate
                    && startDate.Year == report.ReportingMonth.Year && startDate.Month == report.ReportingMonth.Month;

                if (!byProduct.TryGetValue(product.Id, out var item))
                {
                    item = new EmployerInterfaceReportProductData(product.Id);
                    metadata.Add(item);
                    byProduct[product.Id] = item;
                }

                item.Update(item.OperationCode ?? 1, item.DepositStatus ?? 1,
                    item.EmployeeStatus ?? (startedInMonth ? 14 : endedByMonth ? 2 : 1),
                    item.StatusStartDate ?? (endedByMonth ? employment!.EndDate : employee.EmploymentStartDateSnapshot ?? report.ReportingMonth),
                    item.EmploymentPercentage, item.WorkDaysInMonth, item.LastDeposit ?? (endedByMonth ? 1 : 2),
                    item.RefundReason, item.PaymentMethodCode ?? (managedDebit ? 6 : null),
                    item.EmployerAccountType ?? (managedDebit ? 1 : null), item.ReceiverAccountType ?? (managedDebit ? 1 : null),
                    item.PreviousIdentifier, item.PreviousClearingIdentifier, item.PreviousReferenceExceptionCode, item.OldPensionTypeCode);
            }
        }

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

        var preparedAt = preparedAtOverride ?? IsraelNow();
        var senderIdentifier = string.IsNullOrWhiteSpace(options.Value.SenderIdentifier) && options.Value.SenderCode == 5
            ? new string(employer.RegistrationNumber.Where(char.IsDigit).ToArray())
            : options.Value.SenderIdentifier.Trim();
        if (string.IsNullOrWhiteSpace(senderIdentifier))
            return Invalid(documentType, "Employer Interface sender identity is not configured. Configure the actual vault/sender identifier before generating a transmission package.");

        EmployerInterface006FileNaming.PackageName packageName;
        Dictionary<Guid, string> attachmentNames;
        try
        {
            packageName = EmployerInterface006FileNaming.Build(senderIdentifier, options.Value.FileDirectionCode,
                documentType == EmployerInterfaceDocumentType.NegativeReport, preparedAt, fileSequence,
                testFile: options.Value.EnvironmentCode == 1);
            attachmentNames = attachments
                .OrderBy(x => x.DocumentTypeCode).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select((attachment, index) => new
                {
                    attachment.Id,
                    FileName = EmployerInterface006FileNaming.BuildAttachmentFileName(packageName.BaseName, index + 1,
                        Path.GetExtension(attachment.OriginalFileName).TrimStart('.'))
                })
                .ToDictionary(x => x.Id, x => x.FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Invalid(documentType, $"Employer Interface transmission package naming is invalid: {ex.Message}");
        }

        var context = new EmployerInterface006XmlBuilder.BuildContext(employer, employees, people, employments,
            products, contributions, payments, metadata, options.Value,
            report.DepositorTypeCodeSnapshot,
            report.EmployerIdentifierTypeCodeSnapshot,
            attachments, annualEmployerAffidavitSatisfied, preparedAt, attachmentNames, fileSequence);
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

    private static DateTimeOffset IsraelNow()
    {
        try
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem"));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTimeOffset.UtcNow;
        }
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
