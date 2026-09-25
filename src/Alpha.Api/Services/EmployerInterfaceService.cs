using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Alpha.Application.Abstractions;
using Alpha.Application.Reporting;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceService(IAlphaDbContext db, EmployerInterfaceSchemaRegistry schemas, ReportPaymentAccountService paymentAccounts)
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
            var businessIssues = ValidateIncomingBusinessRules(document, detected.DocumentType.Value);
            return new(businessIssues.Count == 0, detected.DocumentType, version, detected.SchemaFileName, businessIssues);
        }
        catch (Exception ex)
        {
            return new(false, detected.DocumentType, null, detected.SchemaFileName, [ex.Message]);
        }
    }

    private static IReadOnlyList<string> ValidateIncomingBusinessRules(XDocument document, EmployerInterfaceDocumentType type)
    {
        if (type is not (EmployerInterfaceDocumentType.CurrentReport or EmployerInterfaceDocumentType.NegativeReport))
            return [];

        var issues = new List<string>();
        var negative = type == EmployerInterfaceDocumentType.NegativeReport;
        var allowedByOperation = new Dictionary<int, HashSet<int>>
        {
            [1] = [1, 3, 5, 6, 7, 9],
            [2] = [1],
            [3] = [1, 3, 5, 6, 7, 9],
            [5] = [1, 3, 6, 7, 9],
            [7] = [1]
        };

        foreach (var transfer in Desc(document, "PirteiHaavaratKsafim"))
        {
            var mobile = Digits(Value(transfer, "MISPAR-CELLULARI-ISH-KESHER-MAASIK"));
            if (mobile.Length != 10 || !mobile.StartsWith("05", StringComparison.Ordinal))
                issues.Add("MISPAR-CELLULARI-ISH-KESHER-MAASIK must be a 10-digit Israeli mobile number beginning with 05; use 0500000000 only when the employer has no mobile.");

            var operation = IntValue(transfer, "SUG-PEULA");
            var paymentMethod = IntValue(transfer, "KOD-EMTZAI-TASHLUM");
            var deposit = DecimalValue(transfer, "SACH-HAFKADA-KUPA-H-P") ?? 0m;

            if (operation.HasValue)
            {
                if (negative && operation is not (5 or 6))
                    issues.Add($"Negative report contains invalid SUG-PEULA={operation}.");
                if (!negative && operation is not (1 or 2 or 3 or 7))
                    issues.Add($"Current report contains invalid SUG-PEULA={operation}.");

                if (operation == 6)
                {
                    if (paymentMethod.HasValue)
                        issues.Add("SUG-PEULA=6 must not contain a KOD-EMTZAI-TASHLUM value.");
                }
                else if (!paymentMethod.HasValue || !allowedByOperation.TryGetValue(operation.Value, out var allowed) || !allowed.Contains(paymentMethod.Value))
                {
                    issues.Add($"KOD-EMTZAI-TASHLUM is missing or incompatible with SUG-PEULA={operation}.");
                }
            }

            if (!negative)
            {
                var zeroEmployer = deposit == 0m || paymentMethod is 3 or 5 or 6 or 9;
                var employerBranch = Digits(Value(transfer, "MISPAR-SNIF-MAASIK"));
                var employerAccount = Digits(Value(transfer, "MISPAR-CHESHBON-MAASIK"));
                if (zeroEmployer && ((employerBranch.Length > 0 && employerBranch.Any(ch => ch != '0'))
                    || (employerAccount.Length > 0 && employerAccount.Any(ch => ch != '0'))))
                    issues.Add("Employer branch/account must be zero when no money is transferred or payment method is 3, 5, 6 or 9.");

                var receiverRequired = (paymentMethod == 1 && deposit > 0m) || paymentMethod == 7;
                var receiverBank = IntValue(transfer, "MISPAR-BANK-KOLET");
                var receiverBranch = Digits(Value(transfer, "MISPAR-SNIF-KOLET"));
                var receiverAccount = Digits(Value(transfer, "MISPAR-CHESHBON-KOLET"));
                if (receiverRequired && (receiverBank is null or <= 0 || receiverBranch.Length == 0
                    || receiverBranch.All(ch => ch == '0') || receiverAccount.Length == 0 || receiverAccount.All(ch => ch == '0')))
                    issues.Add("Receiving bank, branch and account are required for bank transfer with money and for MASAV payment method 7.");

                var ids = Desc(transfer, "PirteiOved")
                    .Select(x => Digits(Value(x, "MISPAR-MEZAHE"))).Where(x => x.Length > 0).ToArray();
                if (ids.GroupBy(x => x, StringComparer.Ordinal).Any(g => g.Count() > 1))
                    issues.Add("PirteiOved must appear only once per employee within a transfer batch.");
            }
        }

        return issues;
    }

    public async Task<GeneratedDocument> ExportAsync(ManualReport report, CancellationToken ct)
    {
        if (report.ReportKind == ManualReportKind.Differences)
            return InvalidGenerated(EmployerInterfaceDocumentType.CurrentReport,
                "Difference reports do not have a dedicated Employer Interface 006 schema. Materialize the difference as a current or negative report before transmission.");

        var type = report.ReportKind == ManualReportKind.Negative
            ? EmployerInterfaceDocumentType.NegativeReport
            : EmployerInterfaceDocumentType.CurrentReport;
        var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == report.EmployerId, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.AllocationOrder).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);

        var document = BuildEmployerReportXml(employer.LegalName, employer.RegistrationNumber, employer.WithholdingFileNumber,
            employees, products, contributions, payments, type);
        var bytes = Serialize(document);
        var schemaValidation = schemas.Validate(bytes, type);
        return new(bytes, new(schemaValidation.IsValid, type, CurrentVersion, schemaValidation.SchemaFileName, schemaValidation.Issues));
    }

    public async Task<IngestResult> IngestAsync(Guid organizationId, Guid employerId, string sourceFileName, byte[] xmlBytes,
        Guid? paymentAccountId, DateOnly? salaryPaymentDate, CancellationToken ct)
    {
        var validation = Validate(xmlBytes);
        if (!validation.IsValid || validation.DocumentType is null)
            return new(null, null, validation, 0, 0);
        return validation.DocumentType is EmployerInterfaceDocumentType.SummaryFeedback or EmployerInterfaceDocumentType.AnnualSummaryFeedback
            ? await IngestFeedbackAsync(organizationId, employerId, sourceFileName, xmlBytes, validation, ct)
            : await ImportReportAsync(organizationId, employerId, xmlBytes, validation, paymentAccountId, salaryPaymentDate, ct);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task<IngestResult> IngestFeedbackAsync(Guid organizationId, Guid employerId, string sourceFileName,
        byte[] bytes, FileValidation validation, CancellationToken ct)
    {
        var hash = Hash(bytes);
        var existingId = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.EmployerId == employerId && x.PayloadHash == hash)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (existingId is not null) return new(null, existingId, validation, 0, 0);

        var doc = EmployerInterfaceSchemaRegistry.LoadXml(bytes);
        var feedback = new EmployerInterfaceFeedback(organizationId, employerId, validation.DocumentType!.Value,
            validation.Version ?? CurrentVersion, sourceFileName, hash, DecodeXml(bytes), Value(doc, "MISPAR-HAKOVETZ"));
        db.EmployerInterfaceFeedback.Add(feedback);
        await db.SaveChangesAsync(ct);
        return new(null, feedback.Id, validation, 0, 0);
    }

    private async Task<IngestResult> ImportReportAsync(Guid organizationId, Guid employerId, byte[] bytes,
        FileValidation validation, Guid? paymentAccountId, DateOnly? salaryPaymentDate, CancellationToken ct)
    {
        var doc = EmployerInterfaceSchemaRegistry.LoadXml(bytes);
        var nodes = Desc(doc, "PirteiOved").ToList();
        if (nodes.Count == 0) return InvalidIngest(validation, "No PirteiOved records were found.");

        var month = nodes.SelectMany(n => Desc(n, "ChodeshMaskoretVestatusOved"))
            .Select(n => ParseDate(Value(n, "CHODESH-MASKORET"))).FirstOrDefault(d => d is not null);
        if (month is null) return InvalidIngest(validation, "CHODESH-MASKORET is required.");
        var reportingMonth = new DateOnly(month.Value.Year, month.Value.Month, 1);
        var kind = validation.DocumentType == EmployerInterfaceDocumentType.NegativeReport
            ? ManualReportKind.Negative : ManualReportKind.Current;

        var previousIdentifiers = Desc(doc, "PirteiHaavaratKsafim")
            .SelectMany(x => new[] { Value(x, "MISPAR-ZIHUI-KODEM"), Value(x, "MISPAR-MISLAKA-KODEM") })
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Guid? sourceId = null;
        if (kind == ManualReportKind.Negative)
        {
            var previousProductIds = previousIdentifiers
                .Select(x => Guid.TryParse(x, out var parsed) ? (Guid?)parsed : null)
                .Where(x => x.HasValue).Select(x => x!.Value).ToArray();

            if (previousProductIds.Length > 0)
            {
                sourceId = await (
                    from product in db.ManualReportProducts.AsNoTracking()
                    join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                    join sourceReport in db.ManualReports.AsNoTracking() on employee.ReportId equals sourceReport.Id
                    where previousProductIds.Contains(product.Id)
                        && sourceReport.OrganizationId == organizationId && sourceReport.EmployerId == employerId
                    select (Guid?)sourceReport.Id).FirstOrDefaultAsync(ct);
            }

            if (sourceId is null && previousIdentifiers.Length > 0)
            {
                sourceId = await (
                    from transmission in db.ReportTransmissions.AsNoTracking()
                    join sourceReport in db.ManualReports.AsNoTracking() on transmission.ReportId equals sourceReport.Id
                    where sourceReport.OrganizationId == organizationId && sourceReport.EmployerId == employerId
                        && transmission.ExternalId != null && previousIdentifiers.Contains(transmission.ExternalId)
                    orderby transmission.CreatedAt descending
                    select (Guid?)sourceReport.Id).FirstOrDefaultAsync(ct);
            }

            // An externally-created valid 006 negative report may legitimately reference a report
            // that is not stored in Alpha. Preserve the official previous identifiers when present,
            // but do not invent a local source report merely because one cannot be resolved.
        }

        var report = new ManualReport(organizationId, employerId, reportingMonth, salaryPaymentDate, kind, sourceId,
            externalSourceReference: kind == ManualReportKind.Negative && sourceId is null);
        var paymentAccount = await paymentAccounts.ResolveForReportAsync(employerId, paymentAccountId, ct);
        if (paymentAccount is null)
            return InvalidIngest(validation, "payment_account_required");
        await paymentAccounts.ApplySnapshotAsync(report, paymentAccount, ct);
        db.ManualReports.Add(report);

        var imported = 0;
        var unmatched = 0;
        var employeeMap = new Dictionary<string, ManualReportEmployee>(StringComparer.Ordinal);
        var orderMap = new Dictionary<Guid, int>();

        foreach (var node in nodes)
        {
            var identifierType = IntValue(node, "SUG-MEZAHE-OVED") ?? 1;
            var rawIdentifier = Value(node, "MISPAR-MEZAHE", "MISPAR-ZEHUT", "MISPAR-ZIHUI-OVED")?.Trim() ?? string.Empty;
            var normalizedIdentifier = identifierType == 1 ? Digits(rawIdentifier) : rawIdentifier;
            if (normalizedIdentifier.Length == 0) { unmatched++; continue; }
            var employeeMapKey = $"{identifierType}:{normalizedIdentifier}";

            var firstName = Value(node, "SHEM-PRATI") ?? string.Empty;
            var lastName = Value(node, "SHEM-MISHPACHA") ?? string.Empty;
            var employeeNumber = Value(node, "MISPAR-OVED-ETZEL-MAASIK", "MISPAR-OVED") ?? normalizedIdentifier;
            var startDate = ParseDate(Value(node, "MOED-TCHILAT-AHASAKAT-OVED")) ?? reportingMonth;
            var birthDate = ParseDate(Value(node, "TAARICH-LEIDA"));
            var gender = IntValue(node, "MIN") switch { 1 => PersonGender.Male, 2 => PersonGender.Female, _ => (PersonGender?)null };
            var email = Value(node, "E-MAIL") ?? string.Empty;
            var mobile = Digits(Value(node, "MISPAR-CELLULARI"));
            var city = Value(node, "SHEM-YISHUV") ?? string.Empty;
            var street = Value(node, "SHEM-RECHOV") ?? string.Empty;
            var houseNumber = Value(node, "MISPAR-BAIT") ?? string.Empty;
            var apartment = Value(node, "MISPAR-DIRA") ?? string.Empty;
            var postalCode = Value(node, "MIKUD") ?? string.Empty;
            var postOfficeBox = Value(node, "TA-DOAR") ?? string.Empty;

            if (!employeeMap.TryGetValue(employeeMapKey, out var reportEmployee))
            {
                // Importing a report must not mutate existing employee master data. The incoming
                // values are preserved on ManualReportEmployee as an immutable report snapshot.
                var person = await db.People.FirstOrDefaultAsync(x =>
                    x.OrganizationId == organizationId && x.NationalId == normalizedIdentifier, ct);
                if (person is null)
                {
                    if (firstName.Length == 0 || lastName.Length == 0) { unmatched++; continue; }
                    person = new Person(organizationId, normalizedIdentifier, firstName, lastName, birthDate, gender, email, mobile,
                        city, street, houseNumber, apartment, postalCode, postOfficeBox);
                    db.People.Add(person);
                }

                var employment = await db.Employments.FirstOrDefaultAsync(x =>
                    x.OrganizationId == organizationId && x.EmployerId == employerId && x.PersonId == person.Id, ct);
                var xmlSalary = Desc(node, "ChodeshMaskoretVestatusOved")
                    .Select(x => Number(Value(x, "SACHAR-MEDUVACH"))).DefaultIfEmpty(0m).Max();
                if (employment is null)
                {
                    employment = new Employment(organizationId, employerId, person.Id, startDate, employeeNumber, xmlSalary);
                    db.Employments.Add(employment);
                }

                reportEmployee = new ManualReportEmployee(report.Id, organizationId, employerId, employment.Id, person.Id,
                    normalizedIdentifier, firstName.Length == 0 ? person.FirstName : firstName,
                    lastName.Length == 0 ? person.LastName : lastName, employeeNumber,
                    xmlSalary > 0 ? xmlSalary : employment.MonthlySalary);
                reportEmployee.SetInterfaceSnapshot(identifierType, rawIdentifier, birthDate,
                    gender.HasValue ? (int)gender.Value : null, email, mobile, city, street, houseNumber, apartment,
                    postalCode, postOfficeBox, startDate);
                db.ManualReportEmployees.Add(reportEmployee);
                employeeMap[employeeMapKey] = reportEmployee;
                imported++;
            }

            foreach (var salaryNode in Desc(node, "ChodeshMaskoretVestatusOved").ToList())
                AddProduct(node, salaryNode, reportEmployee, reportingMonth, orderMap, db);
        }

        if (imported == 0) return InvalidIngest(validation, "No employee records could be mapped into Alpha.", unmatched);
        await db.SaveChangesAsync(ct);
        return new(report.Id, null, validation, imported, unmatched);
    }

    private static void AddProduct(XElement employeeNode, XElement salaryNode, ManualReportEmployee reportEmployee,
        DateOnly reportingMonth, IDictionary<Guid, int> orderMap, IAlphaDbContext context)
    {
        var fundNode = employeeNode.Ancestors().FirstOrDefault(x => NameIs(x, "PirteiKupa"));
        var paymentNode = employeeNode.Ancestors().FirstOrDefault(x => NameIs(x, "PirteiHaavaratKsafim"));
        var salary = Number(Value(salaryNode, "SACHAR-MEDUVACH"));
        var order = orderMap.TryGetValue(reportEmployee.Id, out var previous) ? previous + 1 : 1;
        orderMap[reportEmployee.Id] = order;
        var fundCode = Value(fundNode, "KOD-MEZAHE-KUPA-H-P") ?? string.Empty;
        var section14Code = IntValue(employeeNode, "SEIF-ARBA-ESRE-LAOVED") ?? 3;
        var section14Date = ParseDate(Value(employeeNode, "SEIF-ARBA-ESRE-TAHRIH-KNISA-LETOKEF"));

        var product = new ManualReportProduct(reportEmployee.Id, MapProductType(Value(fundNode, "SUG-KUPA")),
            Value(salaryNode, "MISPAR-POLISA-O-HESHBON") ?? Value(fundNode, "MISPAR-KUPA-ETZEL-MAASIK") ?? string.Empty,
            ParseDate(Value(salaryNode, "CHODESH-MASKORET")) ?? reportingMonth, salary,
            Value(salaryNode, "SUG-TAKBUL") ?? string.Empty, Value(salaryNode, "ROVED-SACHAR") ?? string.Empty,
            section14Code is 1 or 2, section14Date,
            fundExternalKey: fundCode, fundCode: fundCode,
            fundName: Value(fundNode, "SHEM-KUPA-ETZEL-MAASIK") ?? string.Empty,
            salaryAllocationType: SalaryAllocationType.Fixed, salaryAllocationValue: salary, allocationOrder: order,
            section14Code: section14Code);
        context.ManualReportProducts.Add(product);

        foreach (var contributionNode in Desc(salaryNode, "PizulHafrashotOvedBeKupa"))
        {
            var mapped = MapContribution(Value(contributionNode, "SUG-HAFRASHA"));
            context.ManualContributions.Add(new ManualContribution(product.Id, mapped.Item1, mapped.Item2,
                Number(Value(contributionNode, "SCHUM-HAFRASHA")),
                Number(Value(contributionNode, "SHIUR-HAFRASHA")),
                Number(Value(contributionNode, "SACH-TASHLUMIM-PTURIM")),
                Value(contributionNode, "MISPAR-MEZAHE-RESHUMA-KODEM")));
        }

        var metadata = new EmployerInterfaceReportProductData(product.Id);
        metadata.Update(
            IntValue(paymentNode, "SUG-PEULA"),
            IntValue(salaryNode, "MAHAMAD-HAFKADA-BEKUPA"),
            IntValue(salaryNode, "STATUS-OVED-BECHODESH-MASKORET"),
            ParseDate(Value(salaryNode, "TAARICH-TCHILAT-STATUS")),
            DecimalValue(salaryNode, "CHELKIUT-MISRA"),
            IntValue(salaryNode, "YEMEI-AVODA-BECHODESH"),
            IntValue(salaryNode, "HAFKADA-ACHRONA"),
            IntValue(salaryNode, "SIBAT-BAKASH-LECHZER-KSAFIM"),
            IntValue(paymentNode, "KOD-EMTZAI-TASHLUM"),
            IntValue(paymentNode, "SUG-CHESHBON-MAASIK"),
            IntValue(paymentNode, "SUG-CHESHBON-KOLET-TASHLUM"),
            Value(paymentNode, "MISPAR-ZIHUI-KODEM"),
            Value(paymentNode, "MISPAR-MISLAKA-KODEM"),
            null,
            IntValue(fundNode, "SUG-KEREN-PENSIA"));
        context.EmployerInterfaceReportProductData.Add(metadata);

        if (paymentNode is null || metadata.OperationCode == 6) return;
        var payment = new ManualReportPayment(product.Id);
        var reportedDeposit = DecimalValue(paymentNode, "SACH-HAFKADA-KUPA-H-P");
        payment.Update(Value(fundNode, "SHEM-KUPA-ETZEL-MAASIK"), BuildReceiverAccountText(paymentNode),
            Value(paymentNode, "KOD-EMTZAI-TASHLUM"),
            ParseDate(Value(paymentNode, "TAARICH-ERECH-HAFKADA-LEKUPA")),
            ParseDate(Value(paymentNode, "TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT")),
            Value(paymentNode, "MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM"), null,
            Value(paymentNode, "MISPAR-BANK-MAASIK"), Value(paymentNode, "MISPAR-SNIF-MAASIK"),
            Value(paymentNode, "MISPAR-CHESHBON-MAASIK"), null,
            metadata.OperationCode == 3 ? reportedDeposit : null, Value(paymentNode, "KOD-MASAV"));
        context.ManualReportPayments.Add(payment);
    }

    private static string BuildReceiverAccountText(XContainer paymentNode)
    {
        var bank = Value(paymentNode, "MISPAR-BANK-KOLET");
        var branch = Value(paymentNode, "MISPAR-SNIF-KOLET");
        var account = Value(paymentNode, "MISPAR-CHESHBON-KOLET");
        return new[] { bank, branch, account }.All(x => !string.IsNullOrWhiteSpace(x))
            ? $"{bank} - {branch} - {account}"
            : account ?? string.Empty;
    }

    private static XDocument BuildEmployerReportXml(string employerName, string registrationNumber, string withholdingFileNumber,
        IReadOnlyList<ManualReportEmployee> employees, IReadOnlyList<ManualReportProduct> products,
        IReadOnlyList<ManualContribution> contributions, IReadOnlyList<ManualReportPayment> payments,
        EmployerInterfaceDocumentType type)
    {
        var now = DateTimeOffset.UtcNow;
        var senderId = Digits(registrationNumber);
        var root = new XElement("MimshakMaasikim", new XAttribute(XNamespace.Xmlns + "xsi", Xsi));
        root.Add(new XElement("KoteretKovetz",
            E("SUG-MIMSHAK", type == EmployerInterfaceDocumentType.NegativeReport ? "13" : "12"),
            E("MISPAR-GIRSAT-XML", CurrentVersion), E("TAARICH-BITZUA", now.ToString("yyyyMMddHHmmss")),
            E("KOD-SVIVAT-AVODA", "2"), E("MISPAR-HAKOVETZ", BuildFileNumber(now, senderId)), E("MISPAR-SIDURI", "1"),
            new XElement("NetuneiGoremSholech", E("KOD-SHOLECH", "3"), E("SUG-MEZAHE-SHOLECH", "1"),
                E("MISPAR-ZIHUI-SHOLECH", senderId), E("SHEM-GOREM-SHOLECH", employerName))));

        var body = new XElement("GufHamimshak");
        foreach (var group in products.GroupBy(p => new { p.FundCode, p.FundName }))
            body.Add(BuildRecipient(group.ToList(), employerName, senderId, withholdingFileNumber, employees, contributions, payments, type));
        root.Add(body);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement BuildRecipient(IReadOnlyList<ManualReportProduct> products, string employerName, string senderId,
        string withholdingFileNumber, IReadOnlyList<ManualReportEmployee> employees,
        IReadOnlyList<ManualContribution> contributions, IReadOnlyList<ManualReportPayment> payments,
        EmployerInterfaceDocumentType type)
    {
        var productIds = products.Select(x => x.Id).ToHashSet();
        var first = products[0];
        var payment = payments.FirstOrDefault(x => productIds.Contains(x.ReportProductId));
        var fund = new XElement("PirteiKupa", E("SUG-KUPA", MapProductCode(first.ProductType)), E("SHEM-KUPA-ETZEL-MAASIK", first.FundName));
        foreach (var product in products)
        {
            var employee = employees.Single(x => x.Id == product.ReportEmployeeId);
            var salary = new XElement("ChodeshMaskoretVestatusOved", E("CHODESH-MASKORET", product.SalaryMonth.ToString("yyyy-MM-dd")),
                E("SUG-TAKBUL", product.ReportingType), E("ROVED-SACHAR", product.SalaryLayer), E("SACHAR-MEDUVACH", product.Salary),
                E("MISPAR-POLISA-O-HESHBON", product.PolicyNumber));
            foreach (var c in contributions.Where(x => x.ReportProductId == product.Id))
                salary.Add(new XElement("PizulHafrashotOvedBeKupa", E("SUG-HAFRASHA", MapContributionCode(c)),
                    E("SHIUR-HAFRASHA", c.Percentage), E("SCHUM-HAFRASHA", c.Amount), E("SACH-TASHLUMIM-PTURIM", c.ExemptPayments)));
            fund.Add(new XElement("PirteiOved", E("SUG-MEZAHE-OVED", "1"), E("MISPAR-MEZAHE", employee.NationalId),
                E("SHEM-PRATI", employee.FirstName), E("SHEM-MISHPACHA", employee.LastName),
                E("MISPAR-OVED-ETZEL-MAASIK", employee.EmployeeNumber), salary));
        }

        var transfer = new XElement("PirteiHaavaratKsafim", E("KOD-MEZAHE-KUPA-H-P", first.FundCode), E("SUG-MAFKID", "1"),
            E("SUG-MEZAHE-MAASIK", "1"), E("MISPAR-ZIHUY-MAASIK", senderId), E("MISPAR-TIK-NIKUIM-MAASIK", withholdingFileNumber),
            E("SCHUM-HAFKADA-KOLEL", contributions.Where(c => productIds.Contains(c.ReportProductId)).Sum(c => c.Amount)), E("SHEM-MAASIK", employerName),
            E("SUG-PEULA", type == EmployerInterfaceDocumentType.NegativeReport ? "2" : "1"), E("KOD-EMTZAI-TASHLUM", payment?.PaymentMethod),
            E("TAARICH-ERECH-HAFKADA-LEKUPA", payment?.ValueDate?.ToString("yyyy-MM-dd")), E("MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM", payment?.ReferenceNumber),
            E("MISPAR-BANK-MAASIK", payment?.EmployerBankCode), E("MISPAR-SNIF-MAASIK", payment?.EmployerBranch),
            E("MISPAR-CHESHBON-MAASIK", payment?.EmployerAccount), fund);
        return new XElement("YeshutGoremPoneLemislaka", E("SUG-PONE", "5"), E("SUG-KOD-MEZAHE-PONE", "1"),
            E("MISPAR-MEZAHE-PONE", senderId), E("SHEM-GOREM-PONE", employerName), transfer);
    }

    private static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = Utf8NoBom, Indent = false, OmitXmlDeclaration = false, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        return stream.ToArray();
    }

    private static GeneratedDocument InvalidGenerated(EmployerInterfaceDocumentType type, string issue) => new([], new(false, type, CurrentVersion, null, [issue]));
    private static IngestResult InvalidIngest(FileValidation validation, string issue, int unmatched = 0) => new(null, null, validation with { IsValid = false, Issues = [.. validation.Issues, issue] }, 0, unmatched);
    private static XElement E(string name, object? value) => new(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    private static IEnumerable<XElement> Desc(XContainer? x, params string[] names) => x?.Descendants().Where(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)) ?? [];
    private static string? Value(XContainer? x, params string[] names) => Desc(x, names).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
    private static bool NameIs(XElement x, string name) => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static decimal Number(string? value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? number : 0m;
    private static int? IntValue(XContainer? x, params string[] names) =>
        int.TryParse(Value(x, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static decimal? DecimalValue(XContainer? x, params string[] names) =>
        decimal.TryParse(Value(x, names), NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? number : null;
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
        return $"{now:yyyyMMddHHmmss}{normalized}0001";
    }
    private static PensionProductType MapProductType(string? code) => code switch { "1" => PensionProductType.ManagersInsurance, "2" => PensionProductType.PensionFund, "3" => PensionProductType.ProvidentFund, "4" => PensionProductType.StudyFund, _ => PensionProductType.Other };
    private static string MapProductCode(PensionProductType type) => type switch { PensionProductType.ManagersInsurance => "1", PensionProductType.PensionFund => "2", PensionProductType.ProvidentFund => "3", PensionProductType.StudyFund => "4", _ => "99" };
    private static (ContributionParty, ContributionComponent) MapContribution(string? code) => code switch
    {
        "1" => (ContributionParty.Employer, ContributionComponent.Severance),
        "2" => (ContributionParty.Employee, ContributionComponent.Severance),
        "3" => (ContributionParty.Employer, ContributionComponent.Benefits),
        "4" => (ContributionParty.Employee, ContributionComponent.Benefits),
        "5" => (ContributionParty.Employee, ContributionComponent.Disability),
        "6" => (ContributionParty.Employer, ContributionComponent.Disability),
        "7" => (ContributionParty.Employee, ContributionComponent.Other),
        "8" => (ContributionParty.Employer, ContributionComponent.Other),
        _ => throw new InvalidDataException($"Unsupported SUG-HAFRASHA code '{code}'.")
    };
    private static string MapContributionCode(ManualContribution c) => (c.Party, c.Component) switch
    {
        (ContributionParty.Employer, ContributionComponent.Severance) => "1",
        (ContributionParty.Employee, ContributionComponent.Severance) => "2",
        (ContributionParty.Employer, ContributionComponent.Benefits) => "3",
        (ContributionParty.Employee, ContributionComponent.Benefits) => "4",
        (ContributionParty.Employee, ContributionComponent.Disability) => "5",
        (ContributionParty.Employer, ContributionComponent.Disability) => "6",
        (ContributionParty.Employee, ContributionComponent.Other) => "7",
        (ContributionParty.Employer, ContributionComponent.Other) => "8",
        _ => throw new InvalidOperationException("Unsupported contribution mapping.")
    };

    public sealed record FileValidation(bool IsValid, EmployerInterfaceDocumentType? DocumentType, string? Version, string? SchemaFileName, IReadOnlyList<string> Issues);
    public sealed record GeneratedAttachment(string FileName, string ContentType, byte[] Content, string Sha256);
    public sealed record GeneratedDocument(byte[] Bytes, FileValidation Validation, string? PayloadFileName = null,
        IReadOnlyList<GeneratedAttachment>? AttachmentFiles = null);
    public sealed record IngestResult(Guid? ReportId, Guid? FeedbackId, FileValidation Validation, int ImportedEmployees, int UnmatchedRows);
}
