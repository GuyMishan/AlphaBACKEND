using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceService(IAlphaDbContext db)
{
    public const string CurrentVersion = "006";
    public const string CurrentInterfaceCode = "12";
    public const string NegativeInterfaceCode = "13";

    public async Task<string> ExportAsync(ManualReport report, CancellationToken ct)
    {
        var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == report.EmployerId, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.AllocationOrder).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var senderId = Digits(employer.RegistrationNumber);
        var fileNumber = $"{now:yyyyMMddHHmmss}{senderId.PadLeft(16, '0')[..Math.Min(16, Math.Max(16, senderId.Length))]}0001";
        if (fileNumber.Length > 34) fileNumber = fileNumber[..34];
        var interfaceCode = report.ReportKind == ManualReportKind.Negative ? NegativeInterfaceCode : CurrentInterfaceCode;

        // Version 006 envelope and header names follow the published Employer Interface specification.
        // The AlphaData block is the normalized Alpha payload. It is deliberately kept separate so a
        // clearinghouse-specific mapper can replace it without changing the domain/report snapshot.
        var root = new XElement("MimshakMaasikim",
            new XElement("KoteretKovetz",
                E("SUG-MIMSHAK", interfaceCode),
                E("MISPAR-GIRSAT-XML", CurrentVersion),
                E("TAARICH-BITZUA", now.ToString("yyyyMMddHHmmss")),
                E("KOD-SVIVAT-AVODA", "2"),
                E("MISPAR-HAKOVETZ", fileNumber),
                E("MISPAR-SIDURI", "001"),
                new XElement("NetuneiGoremSholech",
                    E("KOD-SHOLECH", "3"),
                    E("SUG-MEZAHE-SHOLECH", "1"),
                    E("MISPAR-ZIHUI-SHOLECH", senderId),
                    E("SHEM-GOREM-SHOLECH", employer.LegalName))),
            new XElement("AlphaData",
                E("REPORT-ID", report.Id), E("EMPLOYER-ID", employer.RegistrationNumber),
                E("REPORTING-MONTH", report.ReportingMonth.ToString("yyyyMM")),
                E("SALARY-PAYMENT-DATE", report.SalaryPaymentDate?.ToString("yyyyMMdd") ?? ""),
                new XElement("Employees", employees.Select(e => new XElement("Employee",
                    E("MISPAR-ZEHUT", e.NationalId), E("MISPAR-OVED", e.EmployeeNumber), E("SHEM-PRATI", e.FirstName), E("SHEM-MISHPACHA", e.LastName), E("SACHAR-CHODSHI", e.MonthlySalary),
                    new XElement("Products", products.Where(p => p.ReportEmployeeId == e.Id).Select(p => new XElement("Product",
                        E("PRODUCT-TYPE", (int)p.ProductType), E("MISPAR-POLISA-O-HESHBON", p.PolicyNumber), E("FUND-CODE", p.FundCode), E("FUND-NAME", p.FundName),
                        E("CHODESH-MASKORET", p.SalaryMonth.ToString("yyyyMM")), E("SACHAR-MEVUTACH", p.Salary), E("REPORTING-TYPE", p.ReportingType), E("SALARY-LAYER", p.SalaryLayer),
                        E("SECTION-14", p.Section14 ? 1 : 0), E("SECTION-14-START-DATE", p.Section14StartDate?.ToString("yyyyMMdd") ?? ""),
                        new XElement("Contributions", contributions.Where(c => c.ReportProductId == p.Id).Select(c => new XElement("Contribution", E("PARTY", (int)c.Party), E("COMPONENT", (int)c.Component), E("AMOUNT", c.Amount), E("PERCENTAGE", c.Percentage), E("EXEMPT-PAYMENTS", c.ExemptPayments)))),
                        payments.Where(m => m.ReportProductId == p.Id).Select(m => new XElement("Payment", E("PROVIDER-NAME", m.ProviderName), E("PROVIDER-ACCOUNT", m.ProviderAccount), E("PAYMENT-METHOD", m.PaymentMethod), E("VALUE-DATE", m.ValueDate?.ToString("yyyyMMdd") ?? ""), E("REFERENCE-NUMBER", m.ReferenceNumber), E("EMPLOYER-BANK-CODE", m.EmployerBankCode), E("EMPLOYER-BRANCH", m.EmployerBranch), E("EMPLOYER-ACCOUNT", m.EmployerAccount))).FirstOrDefault()
                    ))))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
    }

    public FileValidation Validate(string xml, string expectedType)
    {
        var issues = new List<string>(); XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20 * 1024 * 1024 };
            using var sr = new StringReader(xml); using var reader = XmlReader.Create(sr, settings); doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) { return new(false, null, null, ["הקובץ אינו XML תקין או מכיל מבנה XML אסור: " + ex.Message]); }
        if (doc.Root?.Name.LocalName != "MimshakMaasikim") issues.Add("אלמנט השורש חייב להיות MimshakMaasikim.");
        var type = Value(doc, "SUG-MIMSHAK");
        var version = Value(doc, "MISPAR-GIRSAT-XML");
        if (expectedType == "employer-interface" && type is not CurrentInterfaceCode and not NegativeInterfaceCode) issues.Add("SUG-MIMSHAK חייב להיות 12 לדיווח שוטף או 13 לדיווח שלילי.");
        if (version != CurrentVersion) issues.Add($"המערכת תומכת כרגע בממשק מעסיקים גרסה {CurrentVersion}. בקובץ התקבלה גרסה {version ?? "חסרה"}.");
        Required(doc, issues, "TAARICH-BITZUA", 14); Required(doc, issues, "KOD-SVIVAT-AVODA", 1); Required(doc, issues, "MISPAR-HAKOVETZ", 34); Required(doc, issues, "MISPAR-SIDURI", 4);
        Required(doc, issues, "KOD-SHOLECH", 1); Required(doc, issues, "SUG-MEZAHE-SHOLECH", 2); Required(doc, issues, "MISPAR-ZIHUI-SHOLECH", 16);
        var env = Value(doc, "KOD-SVIVAT-AVODA"); if (env is not "1" and not "2") issues.Add("KOD-SVIVAT-AVODA חייב להיות 1 (TEST) או 2 (PRODUCTION).");
        return new(issues.Count == 0, type, version, issues);
    }

    public async Task<ImportResult> ImportAsync(Guid organizationId, Guid employerId, string xml, CancellationToken ct)
    {
        var validation = Validate(xml, "employer-interface");
        if (!validation.IsValid) return new(null, validation, 0, 0);
        // Import is intentionally non-destructive until the full 006 reporting-body mapping is verified.
        // We validate and identify the official envelope/version here; the existing Excel importer remains operational.
        return new(null, validation with { IsValid = false, Issues = [.. validation.Issues, "כותרת ממשק 006 תקינה. קליטת גוף הדיווח תופעל לאחר השלמת מיפוי כל שדות גרסה 006."] }, 0, 0);
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static XElement E(string n, object? v) => new(n, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
    private static IEnumerable<XElement> Desc(XContainer x, params string[] names) => x.Descendants().Where(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase));
    private static string? Value(XContainer x, params string[] names) => Desc(x, names).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
    private static void Required(XDocument doc, List<string> issues, string name, int maxLength) { var value = Value(doc, name); if (string.IsNullOrWhiteSpace(value)) issues.Add($"שדה חובה {name} חסר."); else if (value.Length > maxLength) issues.Add($"{name} ארוך מהגודל המותר ({maxLength})."); }

    public sealed record FileValidation(bool IsValid, string? InterfaceType, string? Version, IReadOnlyList<string> Issues);
    public sealed record ImportResult(Guid? ReportId, FileValidation Validation, int ImportedEmployees, int UnmatchedRows);
}
