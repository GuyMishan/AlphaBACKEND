using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Alpha.Application.Abstractions;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceService(IAlphaDbContext db)
{
    public const string CurrentVersion = "5";
    private static readonly XNamespace Ns = "urn:alpha:employer-interface:v5";

    public async Task<string> ExportAsync(ManualReport report, CancellationToken ct)
    {
        var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == report.EmployerId, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.AllocationOrder).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);

        var root = new XElement(Ns + "EmployerInterface",
            new XAttribute("version", CurrentVersion),
            new XAttribute("interfaceType", report.ReportKind == ManualReportKind.Negative ? "negative" : "current"),
            new XElement(Ns + "Header",
                E("ReportId", report.Id), E("EmployerRegistrationNumber", employer.RegistrationNumber),
                E("ReportingMonth", report.ReportingMonth.ToString("yyyy-MM")), E("SalaryPaymentDate", report.SalaryPaymentDate?.ToString("yyyy-MM-dd") ?? ""),
                E("GeneratedAt", DateTimeOffset.UtcNow.ToString("O")), E("DataEnvironment", "PRODUCTION")),
            new XElement(Ns + "Employees", employees.Select(e => new XElement(Ns + "Employee",
                E("NationalId", e.NationalId), E("EmployeeNumber", e.EmployeeNumber), E("FirstName", e.FirstName), E("LastName", e.LastName), E("MonthlySalary", e.MonthlySalary),
                new XElement(Ns + "Products", products.Where(p => p.ReportEmployeeId == e.Id).Select(p => new XElement(Ns + "Product",
                    E("ProductType", (int)p.ProductType), E("PolicyNumber", p.PolicyNumber), E("FundCode", p.FundCode), E("FundName", p.FundName),
                    E("SalaryMonth", p.SalaryMonth.ToString("yyyy-MM")), E("InsuredSalary", p.Salary), E("ReportingType", p.ReportingType), E("SalaryLayer", p.SalaryLayer),
                    E("Section14", p.Section14 ? 1 : 0), E("Section14StartDate", p.Section14StartDate?.ToString("yyyy-MM-dd") ?? ""),
                    new XElement(Ns + "Contributions", contributions.Where(c => c.ReportProductId == p.Id).Select(c => new XElement(Ns + "Contribution", E("Party", (int)c.Party), E("Component", (int)c.Component), E("Amount", c.Amount), E("Percentage", c.Percentage), E("ExemptPayments", c.ExemptPayments)))),
                    payments.Where(m => m.ReportProductId == p.Id).Select(m => new XElement(Ns + "Payment", E("ProviderName", m.ProviderName), E("ProviderAccount", m.ProviderAccount), E("PaymentMethod", m.PaymentMethod), E("ValueDate", m.ValueDate?.ToString("yyyy-MM-dd") ?? ""), E("ReferenceNumber", m.ReferenceNumber), E("EmployerBankCode", m.EmployerBankCode), E("EmployerBranch", m.EmployerBranch), E("EmployerAccount", m.EmployerAccount))).FirstOrDefault()
                )))
            ))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting);
    }

    public FileValidation Validate(string xml, string expectedType)
    {
        var issues = new List<string>(); XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) { return new(false, null, null, ["הקובץ אינו XML תקין: " + ex.Message]); }
        var root = doc.Root; if (root is null) return new(false, null, null, ["לא נמצא אלמנט ראשי בקובץ."]);
        var type = Attr(root, "interfaceType") ?? Value(doc, "InterfaceType", "MISPAR-SUG-MIMSHAK");
        if (expectedType == "employer-interface" && !LooksLikeEmployerInterface(doc)) issues.Add("הקובץ אינו מזוהה כממשק מעסיקים.");
        var employees = Desc(doc, "Employee", "ReshumatOved", "OVED").Count();
        if (employees == 0) issues.Add("לא נמצאו רשומות עובדים בקובץ.");
        return new(issues.Count == 0, type, CurrentVersion, issues);
    }

    public async Task<ImportResult> ImportAsync(Guid organizationId, Guid employerId, string xml, CancellationToken ct)
    {
        var validation = Validate(xml, "employer-interface"); if (!validation.IsValid) return new(null, validation, 0, 0);
        var doc = XDocument.Parse(xml); var headerMonth = Value(doc, "ReportingMonth", "CHODESH-MASKORET", "CHODESH-DIVUACH");
        if (!TryMonth(headerMonth, out var month)) return new(null, validation with { IsValid = false, Issues = [.. validation.Issues, "חודש הדיווח בקובץ אינו תקין."] }, 0, 0);
        DateOnly? salaryDate = TryDate(Value(doc, "SalaryPaymentDate", "TAARICH-TASHLUM-MASKORET"));
        var report = new ManualReport(organizationId, employerId, month, salaryDate);
        db.ManualReports.Add(report);

        var imported = 0; var unmatched = 0;
        foreach (var node in Desc(doc, "Employee", "ReshumatOved", "OVED"))
        {
            var nationalId = Digits(Value(node, "NationalId", "MISPAR-ZIHUY", "MISPAR-ZEHUT")); if (string.IsNullOrWhiteSpace(nationalId)) { unmatched++; continue; }
            var person = await db.People.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.NationalId == nationalId, ct);
            if (person is null) { person = new Person(organizationId, nationalId, Value(node, "FirstName", "SHEM-PRATI") ?? "לא ידוע", Value(node, "LastName", "SHEM-MISHPACHA") ?? "לא ידוע"); db.People.Add(person); }
            var employeeNumber = Value(node, "EmployeeNumber", "MISPAR-OVED") ?? nationalId;
            var employment = await db.Employments.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.PersonId == person.Id, ct);
            var salary = Decimal(Value(node, "MonthlySalary", "SACHAR-CHODSHI"));
            if (employment is null) { employment = new Employment(organizationId, employerId, person.Id, month, employeeNumber, salary); db.Employments.Add(employment); }
            var re = new ManualReportEmployee(report.Id, organizationId, employerId, employment.Id, person.Id, nationalId, person.FirstName, person.LastName, employeeNumber, salary); db.ManualReportEmployees.Add(re);
            foreach (var p in Desc(node, "Product", "ReshumatMutzar", "MUTZAR"))
            {
                var product = new ManualReportProduct(re.Id, Enum.TryParse<PensionProductType>(Value(p, "ProductType"), true, out var pt) ? pt : (PensionProductType)(int.TryParse(Value(p, "ProductType"), out var pn) ? pn : 99), Value(p, "PolicyNumber", "MISPAR-POLISA-O-HESHBON") ?? "", month, Decimal(Value(p, "InsuredSalary", "SACHAR-MEVUTACH")), Value(p, "ReportingType") ?? "שוטף", Value(p, "SalaryLayer") ?? "רגיל", Value(p, "Section14") == "1", TryDate(Value(p, "Section14StartDate")), fundCode: Value(p, "FundCode"), fundName: Value(p, "FundName"));
                db.ManualReportProducts.Add(product);
                foreach (var c in Desc(p, "Contribution")) db.ManualContributions.Add(new ManualContribution(product.Id, (ContributionParty)Int(Value(c, "Party"), 1), (ContributionComponent)Int(Value(c, "Component"), 2), Decimal(Value(c, "Amount")), Decimal(Value(c, "Percentage")), Decimal(Value(c, "ExemptPayments"))));
                var pay = Desc(p, "Payment").FirstOrDefault(); if (pay is not null) { var payment = new ManualReportPayment(product.Id); payment.Update(Value(pay,"ProviderName"), Value(pay,"ProviderAccount"), Value(pay,"PaymentMethod"), TryDate(Value(pay,"ValueDate")), Value(pay,"ReferenceNumber"), null, Value(pay,"EmployerBankCode"), Value(pay,"EmployerBranch"), Value(pay,"EmployerAccount"), null); db.ManualReportPayments.Add(payment); }
            }
            imported++;
        }
        await db.SaveChangesAsync(ct); return new(report.Id, validation, imported, unmatched);
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static XElement E(string n, object? v) => new(Ns + n, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
    private static IEnumerable<XElement> Desc(XContainer x, params string[] names) => x.Descendants().Where(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase));
    private static string? Value(XContainer x, params string[] names) => Desc(x, names).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
    private static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    private static bool LooksLikeEmployerInterface(XDocument d) => d.Root?.Name.LocalName.Contains("Employer", StringComparison.OrdinalIgnoreCase) == true || Value(d,"MISPAR-SUG-MIMSHAK") is not null || Desc(d,"Employee","ReshumatOved","OVED").Any();
    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
    private static decimal Decimal(string? s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || decimal.TryParse(s, out v) ? v : 0;
    private static int Int(string? s, int fallback) => int.TryParse(s, out var v) ? v : fallback;
    private static DateOnly? TryDate(string? s) => DateOnly.TryParse(s, out var d) ? d : null;
    private static bool TryMonth(string? s, out DateOnly d) { d = default; if (string.IsNullOrWhiteSpace(s)) return false; return DateOnly.TryParseExact(s.Length == 7 ? s + "-01" : s, "yyyy-MM-dd", out d) || DateOnly.TryParseExact(s, "yyyyMM", out d); }

    public sealed record FileValidation(bool IsValid, string? InterfaceType, string? Version, IReadOnlyList<string> Issues);
    public sealed record ImportResult(Guid? ReportId, FileValidation Validation, int ImportedEmployees, int UnmatchedRows);
}
