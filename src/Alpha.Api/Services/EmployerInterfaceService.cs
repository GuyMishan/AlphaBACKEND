using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Alpha.Application.Abstractions;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceService(IAlphaDbContext db, EmployerInterfaceSchemaRegistry schemas)
{
    public const string CurrentVersion = EmployerInterfaceSchemaRegistry.Version;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public FileValidation Validate(byte[] xmlBytes)
    {
        var detected = schemas.DetectAndValidate(xmlBytes);
        if (!detected.IsValid || detected.DocumentType is null)
            return new(false, detected.DocumentType, null, detected.SchemaFileName, detected.Issues);

        try
        {
            var document = EmployerInterfaceSchemaRegistry.LoadXml(xmlBytes);
            var version = Value(document, "MISPAR-GIRSAT-XML");
            if (!string.Equals(version, CurrentVersion, StringComparison.Ordinal))
                return new(false, detected.DocumentType, version, detected.SchemaFileName,
                    [$"Expected Employer Interface version {CurrentVersion}, received {version ?? "missing"}."]);
            return new(true, detected.DocumentType, version, detected.SchemaFileName, []);
        }
        catch (Exception ex)
        {
            return new(false, detected.DocumentType, null, detected.SchemaFileName, [ex.Message]);
        }
    }

    public async Task<GeneratedDocument> ExportAsync(ManualReport report, CancellationToken ct)
    {
        if (report.ReportKind == ManualReportKind.Differences)
            return InvalidGenerated(EmployerInterfaceDocumentType.CurrentReport, "Difference reports do not have a dedicated Employer Interface 006 schema. Materialize the difference as a current or negative report before transmission.");

        var documentType = report.ReportKind == ManualReportKind.Negative
            ? EmployerInterfaceDocumentType.NegativeReport
            : EmployerInterfaceDocumentType.CurrentReport;

        var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == report.EmployerId, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.AllocationOrder).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);

        var xml = BuildEmployerReportXml(report, employer.LegalName, employer.RegistrationNumber, employer.WithholdingFileNumber, employees, products, contributions, payments, documentType);
        var bytes = Serialize(xml);
        var validation = ValidateAgainstExpected(bytes, documentType);
        return new(bytes, validation);
    }

    public async Task<IngestResult> IngestAsync(Guid organizationId, Guid employerId, string sourceFileName, byte[] xmlBytes, CancellationToken ct)
    {
        var validation = Validate(xmlBytes);
        if (!validation.IsValid || validation.DocumentType is null)
            return new(null, null, validation, 0, 0);

        if (validation.DocumentType is EmployerInterfaceDocumentType.SummaryFeedback or EmployerInterfaceDocumentType.AnnualSummaryFeedback)
            return await IngestFeedbackAsync(organizationId, employerId, sourceFileName, xmlBytes, validation, ct);

        return await ImportReportAsync(organizationId, employerId, xmlBytes, validation, ct);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private FileValidation ValidateAgainstExpected(byte[] bytes, EmployerInterfaceDocumentType expected)
    {
        var schemaValidation = schemas.Validate(bytes, expected);
        string? version = null;
        try { version = Value(EmployerInterfaceSchemaRegistry.LoadXml(bytes), "MISPAR-GIRSAT-XML"); } catch { }
        return new(schemaValidation.IsValid, expected, version, schemaValidation.SchemaFileName, schemaValidation.Issues);
    }

    private async Task<IngestResult> IngestFeedbackAsync(Guid organizationId, Guid employerId, string sourceFileName, byte[] bytes, FileValidation validation, CancellationToken ct)
    {
        var hash = Hash(bytes);
        var existing = await db.EmployerInterfaceFeedback.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EmployerId == employerId && x.PayloadHash == hash, ct);
        if (existing is not null)
            return new(null, existing.Id, validation, 0, 0);

        var doc = EmployerInterfaceSchemaRegistry.LoadXml(bytes);
        var feedback = new EmployerInterfaceFeedback(
            organizationId,
            employerId,
            validation.DocumentType!.Value,
            validation.Version ?? CurrentVersion,
            sourceFileName,
            hash,
            DecodeXml(bytes),
            Value(doc, "MISPAR-HAKOVETZ"));
        db.EmployerInterfaceFeedback.Add(feedback);
        await db.SaveChangesAsync(ct);
        return new(null, feedback.Id, validation, 0, 0);
    }

    private async Task<IngestResult> ImportReportAsync(Guid organizationId, Guid employerId, byte[] bytes, FileValidation validation, CancellationToken ct)
    {
        var doc = EmployerInterfaceSchemaRegistry.LoadXml(bytes);
        var employeeNodes = Desc(doc, "PirteiOved").ToList();
        if (employeeNodes.Count == 0)
            return InvalidIngest(validation, "The validated report contains no PirteiOved records that can be mapped into Alpha.");

        var month = employeeNodes.SelectMany(x => Desc(x, "ChodeshMaskoretVestatusOved"))
            .Select(x => ParseDate(Value(x, "CHODESH-MASKORET")))
            .FirstOrDefault(x => x is not null);
        if (month is null)
            return InvalidIngest(validation, "CHODESH-MASKORET is required to create an Alpha report.");
        var reportingMonth = new DateOnly(month.Value.Year, month.Value.Month, 1);

        var kind = validation.DocumentType == EmployerInterfaceDocumentType.NegativeReport ? ManualReportKind.Negative : ManualReportKind.Current;
        Guid? sourceReportId = null;
        if (kind == ManualReportKind.Negative)
        {
            sourceReportId = await db.ManualReports.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.ReportingMonth == reportingMonth && x.ReportKind == ManualReportKind.Current)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (sourceReportId is null)
                return InvalidIngest(validation, "A negative Employer Interface file must be linked to an existing current Alpha report for the same employer and reporting month.");
        }

        var salaryPaymentDate = Desc(doc, "PirteiHaavaratKsafim")
            .Select(x => ParseDate(Value(x, "TAARICH-ERECH-HAFKADA-LEKUPA")))
            .FirstOrDefault(x => x is not null);
        var report = new ManualReport(organizationId, employerId, reportingMonth, salaryPaymentDate, kind, sourceReportId);
        db.ManualReports.Add(report);

        var importedEmployees = 0;
        var unmatched = 0;
        var reportEmployeeByNationalId = new Dictionary<string, ManualReportEmployee>(StringComparer.Ordinal);
        var allocationOrders = new Dictionary<Guid, int>();

        foreach (var employeeNode in employeeNodes)
        {
            var nationalId = Digits(Value(employeeNode, "MISPAR-MEZAHE", "MISPAR-ZEHUT", "MISPAR-ZIHUI-OVED"));
            if (string.IsNullOrWhiteSpace(nationalId)) { unmatched++; continue; }
            var firstName = Value(employeeNode, "SHEM-PRATI") ?? string.Empty;
            var lastName = Value(employeeNode, "SHEM-MISHPACHA") ?? string.Empty;
            var employeeNumber = Value(employeeNode, "MISPAR-OVED-ETZEL-MAASIK", "MISPAR-OVED") ?? nationalId;
            var startDate = ParseDate(Value(employeeNode, "MOED-TCHILAT-AHASAKAT-OVED")) ?? reportingMonth;

            if (!reportEmployeeByNationalId.TryGetValue(nationalId, out var reportEmployee))
            {
                var person = await db.People.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.NationalId == nationalId, ct);
                if (person is null)
                {
                    if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName)) { unmatched++; continue; }
                    person = new Person(organizationId, nationalId, firstName, lastName);
                    db.People.Add(person);
                }

                var employment = await db.Employments.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.PersonId == person.Id, ct);
                var productSalaries = Desc(employeeNode, "ChodeshMaskoretVestatusOved").Select(x => Decimal(Value(x, "SACHAR-MEDUVACH"))).Where(x => x > 0).ToArray();
                var monthlySalary = productSalaries.DefaultIfEmpty(0m).Max();
                if (employment is null)
                {
                    employment = new Employment(organizationId, employerId, person.Id, startDate, employeeNumber, monthlySalary);
                    db.Employments.Add(employment);
                }

                reportEmployee = new ManualReportEmployee(report.Id, organizationId, employerId, employment.Id, person.Id, nationalId,
                    string.IsNullOrWhiteSpace(firstName) ? person.FirstName : firstName,
                    string.IsNullOrWhiteSpace(lastName) ? person.LastName : lastName,
                    employeeNumber,
                    employment.MonthlySalary > 0 ? employment.MonthlySalary : monthlySalary);
                db.ManualReportEmployees.Add(reportEmployee);
                reportEmployeeByNationalId[nationalId] = reportEmployee;
                importedEmployees++;
            }

            var salaryNode = Desc(employeeNode, "ChodeshMaskoretVestatusOved").FirstOrDefault();
            if (salaryNode is null) continue;
            var fundNode = employeeNode.Ancestors().FirstOrDefault(x => NameIs(x, "PirteiKupa"));
            var paymentNode = employeeNode.Ancestors().FirstOrDefault(x => NameIs(x, "PirteiHaavaratKsafim"));
            var salary = Decimal(Value(salaryNode, "SACHAR-MEDUVACH"));
            var order = allocationOrders.TryGetValue(reportEmployee.Id, out var currentOrder) ? currentOrder + 1 : 1;
            allocationOrders[reportEmployee.Id] = order;
            var product = new ManualReportProduct(
                reportEmployee.Id,
                MapProductType(Value(fundNode, "SUG-KUPA")),
                Value(salaryNode, "MISPAR-POLISA-O-HESHBON") ?? Value(fundNode, "MISPAR-KUPA-ETZEL-MAASIK") ?? string.Empty,
                ParseDate(Value(salaryNode, "CHODESH-MASKORET")) ?? reportingMonth,
                salary,
                Value(salaryNode, "SUG-TAKBUL") ?? string.Empty,
                Value(salaryNode, "ROVED-SACHAR") ?? string.Empty,
                Value(employeeNode, "SEIF-ARBA-ESRE-LAOVED") is "1" or "2" or "3",
                ParseDate(Value(employeeNode, "SEIF-ARBA-ESRE-TAHRIH-KNISA-LETOKEF")),
                fundCode: Value(fundNode, "KOD-MEZAHE-KUPA-H-P"),
                fundName: Value(fundNode, "SHEM-KUPA-ETZEL-MAASIK"),
                salaryAllocationType: SalaryAllocationType.Fixed,
                salaryAllocationValue: salary,
                allocationOrder: order);
            db.ManualReportProducts.Add(product);

            foreach (var contributionNode in Desc(salaryNode, "PizulHafrashotOvedBeKupa"))
            {
                var mapping = MapContribution(Value(contributionNode, "SUG-HAFRASHA"));
                db.ManualContributions.Add(new ManualContribution(product.Id, mapping.Party, mapping.Component,
                    Decimal(Value(contributionNode, "SCHUM-HAFRASHA")), Decimal(Value(contributionNode, "SHIUR-HAFRASHA")), Decimal(Value(contributionNode, "SACH-TASHLUMIM-PTURIM"))));
            }

            if (paymentNode is not null)
            {
                var payment = new ManualReportPayment(product.Id);
                payment.Update(
                    Value(fundNode, "SHEM-KUPA-ETZEL-MAASIK"), Value(paymentNode, "MISPAR-CHESHBON-KOLET"), Value(paymentNode, "KOD-EMTZAI-TASHLUM"),
                    ParseDate(Value(paymentNode, "TAARICH-ERECH-HAFKADA-LEKUPA")), Value(paymentNode, "MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM"),
                    null, Value(paymentNode, "MISPAR-BANK-MAASIK"), Value(paymentNode, "MISPAR-SNIF-MAASIK"), Value(paymentNode, "MISPAR-CHESHBON-MAASIK"), null);
                db.ManualReportPayments.Add(payment);
            }
        }

        if (importedEmployees == 0)
            return InvalidIngest(validation, "No employee records could be mapped to Alpha.", unmatched);
        await db.SaveChangesAsync(ct);
        return new(report.Id, null, validation, importedEmployees, unmatched);
    }

    private static XDocument BuildEmployerReportXml(ManualReport report, string employerName, string registrationNumber, string withholdingFileNumber,
        IReadOnlyList<ManualReportEmployee> employees, IReadOnlyList<ManualReportProduct> products, IReadOnlyList<ManualContribution> contributions,
        IReadOnlyList<ManualReportPayment> payments, EmployerInterfaceDocumentType documentType)
    {
        var now = DateTimeOffset.UtcNow;
        var senderId = Digits(registrationNumber);
        var fileNumber = BuildFileNumber(now, senderId);
        var interfaceCode = documentType == EmployerInterfaceDocumentType.NegativeReport ? "13" : "12";
        var root = new XElement("MimshakMaasikim", new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XElement("KoteretKovetz",
                E("SUG-MIMSHAK", interfaceCode), E("MISPAR-GIRSAT-XML", CurrentVersion), E("TAARICH-BITZUA", now.ToString("yyyyMMddHHmmss")),
                E("KOD-SVIVAT-AVODA", "2"), E("MISPAR-HAKOVETZ", fileNumber), E("MISPAR-SIDURI", "1"),
                new XElement("NetuneiGoremSholech", E("KOD-SHOLECH", "3"), E("SUG-MEZAHE-SHOLECH", "1"), E("MISPAR-ZIHUI-SHOLECH", senderId), E("SHEM-GOREM-SHOLECH", employerName))),
            new XElement("GufHamimshak",
                products.GroupBy(p => new { p.FundCode, p.FundName }).Select(group =>
                {
                    var firstPayment = payments.FirstOrDefault(x => group.Select(p => p.Id).Contains(x.ReportProductId));
                    return new XElement("YeshutGoremPoneLemislaka",
                        E("SUG-PONE", "5"), E("SUG-KOD-MEZAHE-PONE", "1"), E("MISPAR-MEZAHE-PONE", senderId), E("SHEM-GOREM-PONE", employerName),
                        new XElement("PirteiHaavaratKsafim",
                            E("KOD-MEZAHE-KUPA-H-P", group.Key.FundCode), E("SUG-MAFKID", "1"), E("SUG-MEZAHE-MAASIK", "1"), E("MISPAR-ZIHUY-MAASIK", senderId),
                            E("MISPAR-TIK-NIKUIM-MAASIK", withholdingFileNumber), E("SCHUM-HAFKADA-KOLEL", group.SelectMany(p => contributions.Where(c => c.ReportProductId == p.Id)).Sum(c => c.Amount)),
                            E("SHEM-MAASIK", employerName), E("SUG-PEULA", documentType == EmployerInterfaceDocumentType.NegativeReport ? "2" : "1"),
                            E("KOD-EMTZAI-TASHLUM", firstPayment?.PaymentMethod), E("TAARICH-ERECH-HAFKADA-LEKUPA", firstPayment?.ValueDate?.ToString("yyyy-MM-dd")),
                            E("MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM", firstPayment?.ReferenceNumber), E("MISPAR-BANK-MAASIK", firstPayment?.EmployerBankCode),
                            E("MISPAR-SNIF-MAASIK", firstPayment?.EmployerBranch), E("MISPAR-CHESHBON-MAASIK", firstPayment?.EmployerAccount),
                            new XElement("PirteiKupa", E("SUG-KUPA", MapProductCode(group.First().ProductType)), E("SHEM-KUPA-ETZEL-MAASIK", group.Key.FundName),
                                group.Select(product =>
                                {
                                    var employee = employees.Single(e => e.Id == product.ReportEmployeeId);
                                    return new XElement("PirteiOved",
                                        E("SUG-MEZAHE-OVED", "1"), E("MISPAR-MEZAHE", employee.NationalId), E("SHEM-PRATI", employee.FirstName), E("SHEM-MISHPACHA", employee.LastName), E("MISPAR-OVED-ETZEL-MAASIK", employee.EmployeeNumber),
                                        new XElement("ChodeshMaskoretVestatusOved", E("CHODESH-MASKORET", product.SalaryMonth.ToString("yyyy-MM-dd")), E("SUG-TAKBUL", product.ReportingType), E("ROVED-SACHAR", product.SalaryLayer),
                                            E("SACHAR-MEDUVACH", product.Salary), E("MISPAR-POLISA-O-HESHBON", product.PolicyNumber),
                                            contributions.Where(c => c.ReportProductId == product.Id).Select(c => new XElement("PizulHafrashotOvedBeKupa", E("SUG-HAFRASHA", MapContributionCode(c)), E("SHIUR-HAFRASHA", c.Percentage), E("SCHUM-HAFRASHA", c.Amount), E("SACH-TASHLUMIM-PTURIM", c.ExemptPayments)))));
                                }))));
                }))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = Utf8NoBom, Indent = false, OmitXmlDeclaration = false, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        return stream.ToArray();
    }

    private static GeneratedDocument InvalidGenerated(EmployerInterfaceDocumentType type, string issue) =>
        new([], new(false, type, CurrentVersion, null, [issue]));
    private static IngestResult InvalidIngest(FileValidation validation, string issue, int unmatched = 0) =>
        new(null, null, validation with { IsValid = false, Issues = [.. validation.Issues, issue] }, 0, unmatched);
    private static XElement E(string name, object? value) => new(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    private static IEnumerable<XElement> Desc(XContainer? x, params string[] names) => x?.Descendants().Where(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)) ?? [];
    private static string? Value(XContainer? x, params string[] names) => Desc(x, names).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
    private static bool NameIs(XElement x, string name) => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static decimal Decimal(string? value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var format in new[] { "yyyy-MM-dd", "yyyyMMdd", "yyyy-MM", "yyyyMM" })
            if (DateOnly.TryParseExact(value.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        return null;
    }
    private static string DecodeXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    private static string BuildFileNumber(DateTimeOffset now, string senderId)
    {
        var normalized = senderId.Length > 16 ? senderId[^16..] : senderId.PadLeft(16, '0');
        return $"{now:yyyyMMddHHmmss}{normalized}0001"[..34];
    }
    private static PensionProductType MapProductType(string? code) => code switch { "1" => PensionProductType.ManagersInsurance, "2" => PensionProductType.PensionFund, "3" => PensionProductType.ProvidentFund, "4" => PensionProductType.StudyFund, _ => PensionProductType.Other };
    private static string MapProductCode(PensionProductType type) => type switch { PensionProductType.ManagersInsurance => "1", PensionProductType.PensionFund => "2", PensionProductType.ProvidentFund => "3", PensionProductType.StudyFund => "4", _ => "99" };
    private static (ContributionParty Party, ContributionComponent Component) MapContribution(string? code) => code switch
    {
        "1" => (ContributionParty.Employee, ContributionComponent.Benefits),
        "2" => (ContributionParty.Employer, ContributionComponent.Benefits),
        "3" => (ContributionParty.Employer, ContributionComponent.Severance),
        "4" => (ContributionParty.Employer, ContributionComponent.Disability),
        _ => (ContributionParty.Employer, ContributionComponent.Other)
    };
    private static string MapContributionCode(ManualContribution c) => (c.Party, c.Component) switch
    {
        (ContributionParty.Employee, ContributionComponent.Benefits) => "1",
        (ContributionParty.Employer, ContributionComponent.Benefits) => "2",
        (ContributionParty.Employer, ContributionComponent.Severance) => "3",
        (ContributionParty.Employer, ContributionComponent.Disability) => "4",
        _ => "9"
    };

    public sealed record FileValidation(bool IsValid, EmployerInterfaceDocumentType? DocumentType, string? Version, string? SchemaFileName, IReadOnlyList<string> Issues);
    public sealed record GeneratedDocument(byte[] Bytes, FileValidation Validation);
    public sealed record IngestResult(Guid? ReportId, Guid? FeedbackId, FileValidation Validation, int ImportedEmployees, int UnmatchedRows);
}
