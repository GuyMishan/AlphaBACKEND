using System.Globalization;
using System.Xml.Linq;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

public static class EmployerInterface006XmlBuilder
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly int[] CurrentReceiptCodes = [1, 2, 4, 6, 8];
    private static readonly int[] SalaryLayerCodes = [1, 3, 5, 6, 7];
    private static readonly int[] PaymentMethodCodes = [1, 3, 4, 5, 6, 7, 9];

    public static BuildResult BuildCurrent(BuildContext context) => Build(context, false);
    public static BuildResult BuildNegative(BuildContext context) => Build(context, true);

    private static BuildResult Build(BuildContext c, bool negative)
    {
        var issues = Validate(c, negative);
        if (issues.Count > 0) return new(null, issues);

        var now = DateTimeOffset.UtcNow;
        var senderId = Digits(c.Employer.RegistrationNumber);
        var groups = c.Products.GroupBy(x => new { x.FundCode, x.FundName }).ToList();
        var root = new XElement("MimshakMaasikim", new XAttribute(XNamespace.Xmlns + "xsi", Xsi));
        root.Add(BuildHeader(c, negative, now, senderId));

        var body = new XElement("GufHamimshak");
        foreach (var group in groups) body.Add(BuildRequester(c, group.ToList(), negative, senderId));
        root.Add(body);

        var totalContributions = c.Contributions.Sum(x => x.Amount);
        root.Add(new XElement("ReshumatSgira",
            E("MISPAR-KUPOT-YATZRANIM-BAKOVETZ", groups.Count),
            E("MISPAR-MAASIKIM", 1),
            E("MISPAR-RESHUMOT", c.Contributions.Count),
            E("MISPAR-AMITIM", c.Employees.Select(x => x.PersonId).Distinct().Count()),
            E("SACH-HAFRASHOT-BAKOVETZ", Money(totalContributions)),
            E("SACH-HAFKADOT-BAKOVETZ", Money(totalContributions))));

        return new(new XDocument(new XDeclaration("1.0", "utf-8", null), root), []);
    }

    private static XElement BuildHeader(BuildContext c, bool negative, DateTimeOffset now, string senderId)
    {
        var o = c.Options;
        return new XElement("KoteretKovetz",
            E("SUG-MIMSHAK", negative ? 13 : 12),
            E("MISPAR-GIRSAT-XML", EmployerInterfaceSchemaRegistry.Version),
            E("TAARICH-BITZUA", now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)),
            E("KOD-SVIVAT-AVODA", o.EnvironmentCode),
            E("MISPAR-HAKOVETZ", BuildFileNumber(now, senderId)),
            E("MISPAR-SIDURI", 1),
            new XElement("NetuneiGoremSholech",
                E("KOD-SHOLECH", o.SenderCode),
                E("SUG-MEZAHE-SHOLECH", o.SenderIdentifierType),
                E("MISPAR-ZIHUI-SHOLECH", senderId),
                E("SHEM-GOREM-SHOLECH", c.Employer.LegalName),
                E("SHEM-PRATI-ISH-KESHER-SHOLECH", c.Employer.ContactFirstName),
                E("SHEM-MISHPACHA-ISH-KESHER-SHOLECH", c.Employer.ContactLastName),
                E("MISPAR-TELEPHONE-KAVI-ISH-KESHER-SHOLECH", Digits(c.Employer.ContactPhone)),
                E("E-MAIL-ISH-KESHER-SHOLECH", c.Employer.ContactEmail),
                Nil("MISPAR-CELLULARI-ISH-KESHER-SHOLECH", Digits(c.Employer.ContactMobile))),
            new XElement("NetuneiGoremNimaan",
                E("KOD-NIMAAN", o.RecipientCode),
                E("SUG-MEZAHE-NIMAAN", o.RecipientIdentifierType),
                E("MISPAR-ZIHUI-NIMAAN", o.RecipientIdentifier),
                Nil("MISPAR-ZIHUI-ETZEL-YATZRAN-NIMAAN", o.RecipientInternalIdentifier)));
    }

    private static XElement BuildRequester(BuildContext c, IReadOnlyList<ManualReportProduct> products, bool negative, string senderId)
    {
        var productIds = products.Select(x => x.Id).ToHashSet();
        var first = products[0];
        var payment = c.Payments.FirstOrDefault(x => productIds.Contains(x.ReportProductId));
        var metadata = c.ProductMetadata.First(x => x.ReportProductId == first.Id);
        var total = c.Contributions.Where(x => productIds.Contains(x.ReportProductId)).Sum(x => x.Amount);

        var transfer = new XElement("PirteiHaavaratKsafim",
            E("KOD-MEZAHE-KUPA-H-P", Digits(first.FundCode)),
            E("SUG-MAFKID", 1),
            E("SUG-MEZAHE-MAASIK", 1),
            E("MISPAR-ZIHUY-MAASIK", senderId),
            E("MISPAR-TIK-NIKUIM-MAASIK", Digits(c.Employer.WithholdingFileNumber)),
            Nil("KOD-MEZAHE-MAASIK-ETZEL-YATZRAN", null),
            Nil("KOD-MASAV", null),
            E("SCHUM-HAFKADA-KOLEL", negative && metadata.OperationCode == 6 ? Money(0) : Money(total)),
            E("SHEM-MAASIK", c.Employer.LegalName),
            E("SHEM-PRATI-ISH-KESHER-MAASIK", c.Employer.ContactFirstName),
            E("SHEM-MISHPACHA-ISH-KESHER-MAASIK", c.Employer.ContactLastName),
            E("MISPAR-TELEPHONE-KAVI-ISH-KESHER-MAASIK", Digits(c.Employer.ContactPhone)),
            E("E-MAIL-ISH-KESHER-MAASIK", c.Employer.ContactEmail),
            E("MISPAR-CELLULARI-ISH-KESHER-MAASIK", EmployerContactMobile(c.Employer.ContactMobile)),
            E("SUG-PEULA", metadata.OperationCode!.Value));

        if (!negative)
        {
            transfer.Add(
                E("KOD-EMTZAI-TASHLUM", metadata.PaymentMethodCode!.Value),
                E("SACH-HAFKADA-KUPA-H-P", Money(total)),
                Nil("TAARICH-ERECH-HAFKADA-LEKUPA", payment?.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Nil("TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT", null),
                E("MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM", payment?.ReferenceNumber ?? string.Empty),
                E("MISPAR-ZIHUI", UpperGuid(first.Id)),
                E("MISPAR-BANK-MAASIK", int.Parse(Digits(payment!.EmployerBankCode), CultureInfo.InvariantCulture)),
                E("MISPAR-SNIF-MAASIK", Digits(payment.EmployerBranch)),
                E("MISPAR-CHESHBON-MAASIK", Digits(payment.EmployerAccount)),
                Nil("SUG-CHESHBON", null),
                E("SUG-CHESHBON-MAASIK", metadata.EmployerAccountType!.Value),
                E("SUG-CHESHBON-KOLET-TASHLUM", metadata.ReceiverAccountType!.Value),
                Nil("MISPAR-BANK-KOLET", null),
                Nil("MISPAR-SNIF-KOLET", null),
                Nil("MISPAR-CHESHBON-KOLET", null));
        }
        else
        {
            if (metadata.OperationCode == 5) transfer.Add(E("KOD-EMTZAI-TASHLUM", metadata.PaymentMethodCode!.Value));
            transfer.Add(E("SACH-HAFKADA-KUPA-H-P", metadata.OperationCode == 6 ? Money(0) : Money(total)));
            transfer.Add(E("MISPAR-ZIHUI", UpperGuid(first.Id)));
            if (metadata.OperationCode == 5 && metadata.PaymentMethodCode == 1)
            {
                transfer.Add(
                    E("MISPAR-BANK-MAASIK", int.Parse(Digits(payment!.EmployerBankCode), CultureInfo.InvariantCulture)),
                    E("MISPAR-SNIF-MAASIK", Digits(payment.EmployerBranch)),
                    E("MISPAR-CHESHBON-MAASIK", Digits(payment.EmployerAccount)));
            }
        }

        transfer.Add(Nil("MISPAR-ZIHUI-KODEM", null), Nil("MISPAR-MISLAKA", null), Nil("MISPAR-MISLAKA-KODEM", null));
        transfer.Add(BuildFund(c, products, negative));

        return new XElement("YeshutGoremPoneLemislaka",
            E("SUG-PONE", 5),
            E("SUG-KOD-MEZAHE-PONE", 1),
            E("MISPAR-MEZAHE-PONE", senderId),
            E("SHEM-GOREM-PONE", c.Employer.LegalName),
            Nil("MISPAR-MEZAHE-METAFEL", null),
            Nil("SHEM-PRATI-PONE-LEMISLAKA", c.Employer.ContactFirstName),
            Nil("SHEM-MISHPACHA-PONE-LEMISLAKA", c.Employer.ContactLastName),
            Nil("MISPAR-TELEPHONE-KAVI-PONE-LEMISLAKA", Digits(c.Employer.ContactPhone)),
            Nil("E-MAIL-PONE-LEMISLAKA", c.Employer.ContactEmail),
            Nil("MISPAR-CELLULARI", Digits(c.Employer.ContactMobile)),
            transfer);
    }

    private static XElement BuildFund(BuildContext c, IReadOnlyList<ManualReportProduct> products, bool negative)
    {
        var first = products[0];
        var fund = new XElement("PirteiKupa",
            E("SUG-KUPA", MapProductCode(first.ProductType)),
            Nil("SUG-KEREN-PENSIA", null),
            Nil("SHEM-KUPA-ETZEL-MAASIK", first.FundName),
            Nil("MISPAR-KUPA-ETZEL-MAASIK", null));

        // Version 006 clearinghouse rules require one PirteiOved block per employee in a batch.
        // Multiple products/policies for the same employee are represented by multiple
        // ChodeshMaskoretVestatusOved blocks under the same PirteiOved.
        foreach (var employeeProducts in products.GroupBy(x => x.ReportEmployeeId))
            fund.Add(BuildEmployee(c, employeeProducts.OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).ToList(), negative));

        var ids = products.Select(x => x.Id).ToHashSet();
        var total = c.Contributions.Where(x => ids.Contains(x.ReportProductId)).Sum(x => x.Amount);
        if (total > 0)
            fund.Add(new XElement("SachHafrashaLeKupaMaasik",
                E("SACH-HAFRASHA-LEKUPA-BERAMAT-MAASIK", Money(total)),
                E("SACH-HAFKADA-LEKUPA-BERAMAT-MAASIK", Money(total)),
                E("MISPAR-AMITIM-BERAMAT-MAASIK", products.Select(x => c.Employees.Single(e => e.Id == x.ReportEmployeeId).PersonId).Distinct().Count())));
        return fund;
    }

    private static XElement BuildEmployee(BuildContext c, IReadOnlyList<ManualReportProduct> products, bool negative)
    {
        var firstProduct = products[0];
        var employee = c.Employees.Single(x => x.Id == firstProduct.ReportEmployeeId);
        var person = c.People[employee.PersonId];
        var employment = c.Employments[employee.EmploymentId];

        var node = new XElement("PirteiOved",
            E("SUG-MEZAHE-OVED", 1),
            E("MISPAR-MEZAHE", Digits(employee.NationalId)),
            E("SHEM-PRATI", employee.FirstName),
            E("SHEM-MISHPACHA", employee.LastName));

        if (!negative) node.Add(E("TAARICH-LEIDA", person.BirthDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        else if (person.BirthDate.HasValue) node.Add(E("TAARICH-LEIDA", person.BirthDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        node.Add(Nil("MISPAR-OVED-ETZEL-MAASIK", employee.EmployeeNumber));

        if (!negative)
        {
            node.Add(
                E("SHEM-YISHUV", person.City),
                E("SHEM-RECHOV", person.Street),
                E("MISPAR-BAIT", person.HouseNumber),
                E("MISPAR-DIRA", person.Apartment),
                E("MIKUD", person.PostalCode),
                E("TA-DOAR", person.PostOfficeBox),
                E("E-MAIL", person.Email),
                E("MISPAR-CELLULARI", Digits(person.Mobile)),
                E("MIN", (int)person.Gender!.Value),
                E("MOED-TCHILAT-AHASAKAT-OVED", employment.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                E("SEIF-ARBA-ESRE-LAOVED", firstProduct.Section14Code),
                Nil("SEIF-ARBA-ESRE-TAHRIH-KNISA-LETOKEF", firstProduct.Section14StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        else
        {
            node.Add(
                Nil("HASHAVA-KIBUTZI", null),
                Nil("HATZHARAT-OVED", null),
                Nil("SEIF-ARBA-ESRE-LAOVED", null),
                Nil("SEIF-ARBA-ESRE-TAHRIH-KNISA-LETOKEF", null));
        }

        decimal employeeTotal = 0;
        foreach (var product in products)
        {
            var metadata = c.ProductMetadata.Single(x => x.ReportProductId == product.Id);
            var contributions = c.Contributions.Where(x => x.ReportProductId == product.Id).ToList();
            var total = contributions.Sum(x => x.Amount);
            employeeTotal += total;

            var salary = new XElement("ChodeshMaskoretVestatusOved",
                E("CHODESH-MASKORET", product.SalaryMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            if (!negative) salary.Add(E("MAHAMAD-HAFKADA-BEKUPA", metadata.DepositStatus!.Value));
            salary.Add(E("SUG-TAKBUL", ParseRequiredCode(product.ReportingType)), E("ROVED-SACHAR", ParseRequiredCode(product.SalaryLayer)));
            if (negative) salary.Add(E("SIBAT-BAKASH-LECHZER-KSAFIM", metadata.RefundReason!.Value));
            if (!negative)
            {
                salary.Add(E("SACHAR-MEDUVACH", Money(product.Salary)), E("STATUS-OVED-BECHODESH-MASKORET", metadata.EmployeeStatus!.Value),
                    Nil("TAARICH-TCHILAT-STATUS", metadata.StatusStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Nil("CHELKIUT-MISRA", metadata.EmploymentPercentage), Nil("YEMEI-AVODA-BECHODESH", metadata.WorkDaysInMonth),
                    Nil("MISPAR-POLISA-O-HESHBON", product.PolicyNumber), E("HAFKADA-ACHRONA", metadata.LastDeposit!.Value));
            }
            else
            {
                salary.Add(Nil("TAARICH-TCHILAT-STATUS", null));
            }

            foreach (var contribution in contributions)
            {
                var split = new XElement("PizulHafrashotOvedBeKupa", E("SUG-HAFRASHA", MapContributionCode(contribution)));
                if (!negative) split.Add(contribution.Percentage > 0 ? E("SHIUR-HAFRASHA", contribution.Percentage) : Nil("SHIUR-HAFRASHA", null));
                else if (contribution.Percentage > 0) split.Add(E("SHIUR-HAFRASHA", contribution.Percentage));
                split.Add(E("SCHUM-HAFRASHA", Money(contribution.Amount)));
                if (!negative) split.Add(E("SACH-TASHLUMIM-PTURIM", Money(contribution.ExemptPayments)));
                split.Add(E("MISPAR-MEZAHE-RESHUMA", UpperGuid(contribution.Id)), Nil("MISPAR-MEZAHE-RESHUMA-KODEM", null));
                salary.Add(split);
            }
            salary.Add(new XElement("SachHafrashaLeKupaBechodeshMaskoretOved",
                total > 0 ? E("SACH-HAFRASHA-BECHODESH-MASKORET", Money(total)) : Nil("SACH-HAFRASHA-BECHODESH-MASKORET", null)));
            node.Add(salary);
        }

        node.Add(new XElement("SachHafrashaLeOvedBekupa",
            employeeTotal > 0 ? E("SACH-HAFRASHA-LEOVED-BEKUPA", Money(employeeTotal)) : Nil("SACH-HAFRASHA-LEOVED-BEKUPA", null)));
        return node;
    }

    private static List<string> Validate(BuildContext c, bool negative)
    {
        var issues = new List<string>();
        var o = c.Options;
        if (o.EnvironmentCode is not (1 or 2)) issues.Add("EmployerInterface006:EnvironmentCode must be 1 or 2.");
        if (o.SenderCode is < 1 or > 6) issues.Add("EmployerInterface006:SenderCode must be a valid Version 006 sender code.");
        if (o.SenderIdentifierType <= 0) issues.Add("EmployerInterface006:SenderIdentifierType is required.");
        if (o.RecipientCode is not (1 or 2 or 3 or 6)) issues.Add("EmployerInterface006:RecipientCode is required and must be 1, 2, 3 or 6.");
        if (o.RecipientIdentifierType <= 0) issues.Add("EmployerInterface006:RecipientIdentifierType is required.");
        if (string.IsNullOrWhiteSpace(o.RecipientIdentifier)) issues.Add("EmployerInterface006:RecipientIdentifier is required.");
        if (Digits(c.Employer.RegistrationNumber).Length is 0 or > 16) issues.Add("Employer registration number must contain 1-16 digits for Version 006.");
        if (Digits(c.Employer.WithholdingFileNumber).Length is 0 or > 9) issues.Add("Employer withholding file number must contain 1-9 digits for Version 006.");
        if (string.IsNullOrWhiteSpace(c.Employer.ContactFirstName)) issues.Add("Employer Interface contact first name is required.");
        if (string.IsNullOrWhiteSpace(c.Employer.ContactLastName)) issues.Add("Employer Interface contact last name is required.");
        if (Digits(c.Employer.ContactPhone).Length is 0 or > 20) issues.Add("Employer Interface contact phone must contain 1-20 digits.");
        if (string.IsNullOrWhiteSpace(c.Employer.ContactEmail)) issues.Add("Employer Interface contact email is required.");
        var employerMobile = Digits(c.Employer.ContactMobile);
        if (employerMobile.Length > 0 && (employerMobile.Length != 10 || !employerMobile.StartsWith("05", StringComparison.Ordinal)))
            issues.Add("Employer Interface contact mobile must match ^05\\d\\d{7}$; when no mobile exists, Version 006 requires 0500000000.");
        if (c.Products.Count == 0) issues.Add("The report has no pension products to export.");

        foreach (var product in c.Products)
        {
            var label = $"Product {product.Id}";
            if (Digits(product.FundCode).Length != 30) issues.Add($"{label}: fund code must be exactly 30 digits (KOD-MEZAHE-KUPA-H-P).");
            if (!TryCode(product.ReportingType, CurrentReceiptCodes, out _)) issues.Add($"{label}: ReportingType must be one of 1,2,4,6,8 for Version 006.");
            if (!TryCode(product.SalaryLayer, SalaryLayerCodes, out _)) issues.Add($"{label}: SalaryLayer must be one of 1,3,5,6,7 for Version 006.");
            if (!negative)
            {
                if (product.Section14Code is < 1 or > 5) issues.Add($"{label}: Section14Code must be one of 1,2,3,4,5.");
                if (product.Section14Code is 2 or 4 && !product.Section14StartDate.HasValue)
                    issues.Add($"{label}: Section14StartDate is required for Section14Code 2 or 4.");
            }
            var meta = c.ProductMetadata.FirstOrDefault(x => x.ReportProductId == product.Id);
            if (meta is null) { issues.Add($"{label}: Employer Interface 006 metadata is missing."); continue; }
            if (!meta.OperationCode.HasValue) issues.Add($"{label}: OperationCode is required.");
            else if (negative && meta.OperationCode is not (5 or 6)) issues.Add($"{label}: negative Version 006 requires OperationCode 5 or 6.");
            else if (!negative && meta.OperationCode is not (1 or 2 or 3 or 7)) issues.Add($"{label}: current Version 006 requires OperationCode 1, 2, 3 or 7.");

            if (!negative)
            {
                if (!meta.DepositStatus.HasValue) issues.Add($"{label}: DepositStatus is required for a current report.");
                if (!meta.EmployeeStatus.HasValue) issues.Add($"{label}: EmployeeStatus is required for a current report.");
                if (!meta.StatusStartDate.HasValue) issues.Add($"{label}: StatusStartDate is required for a current report.");
                if (!meta.LastDeposit.HasValue) issues.Add($"{label}: LastDeposit is required for a current report.");
                if (!meta.PaymentMethodCode.HasValue || !PaymentMethodCodes.Contains(meta.PaymentMethodCode.Value)) issues.Add($"{label}: PaymentMethodCode must be one of 1,3,4,5,6,7,9.");
                if (meta.EmployerAccountType is not (1 or 2)) issues.Add($"{label}: EmployerAccountType must be 1 or 2 for a current report.");
                if (meta.ReceiverAccountType is not (1 or 2)) issues.Add($"{label}: ReceiverAccountType must be 1 or 2 for a current report.");
                ValidatePaymentAccount(c, product, label, requireBankAccount: true, issues);
            }
            else
            {
                if (!meta.RefundReason.HasValue || meta.RefundReason is < 1 or > 10) issues.Add($"{label}: RefundReason 1-10 is required for a negative report.");
                if (meta.OperationCode == 5)
                {
                    if (!meta.PaymentMethodCode.HasValue || !PaymentMethodCodes.Contains(meta.PaymentMethodCode.Value)) issues.Add($"{label}: OperationCode 5 requires a valid refund PaymentMethodCode.");
                    ValidatePaymentAccount(c, product, label, requireBankAccount: meta.PaymentMethodCode == 1, issues);
                }
                else if (meta.OperationCode == 6 && meta.PaymentMethodCode.HasValue)
                {
                    issues.Add($"{label}: OperationCode 6 must not include PaymentMethodCode according to Employer Interface Version 6.");
                }
            }

            if (!negative)
            {
                var sameEmployeeFundProducts = c.Products.Where(x => x.ReportEmployeeId == product.ReportEmployeeId
                    && string.Equals(x.FundCode, product.FundCode, StringComparison.Ordinal)
                    && string.Equals(x.FundName, product.FundName, StringComparison.Ordinal)).ToList();
                if (sameEmployeeFundProducts.Any(x => x.Section14Code != product.Section14Code
                    || x.Section14StartDate != product.Section14StartDate))
                    issues.Add($"{label}: products for the same employee and fund must use the same Section 14 code/effective date because Version 006 permits one PirteiOved block per employee in a batch.");
            }

            var contributions = c.Contributions.Where(x => x.ReportProductId == product.Id).ToList();
            if (negative && contributions.Count == 0) issues.Add($"{label}: negative Version 006 requires at least one contribution record.");
            if (negative && contributions.Any(x => x.Amount <= 0)) issues.Add($"{label}: negative contribution amounts must be greater than zero.");
        }

        if (!negative)
        {
            foreach (var employee in c.Employees)
            {
                if (!c.People.TryGetValue(employee.PersonId, out var person)) { issues.Add($"Employee {employee.Id}: person profile was not found."); continue; }
                if (!person.BirthDate.HasValue) issues.Add($"Employee {employee.Id}: BirthDate is required.");
                if (!person.Gender.HasValue) issues.Add($"Employee {employee.Id}: Gender is required.");
                if (string.IsNullOrWhiteSpace(person.Email)) issues.Add($"Employee {employee.Id}: Email is required.");
                if (Digits(person.Mobile).Length == 0) issues.Add($"Employee {employee.Id}: Mobile is required.");
                if (string.IsNullOrWhiteSpace(person.City)) issues.Add($"Employee {employee.Id}: City is required.");
                if (string.IsNullOrWhiteSpace(person.Street)) issues.Add($"Employee {employee.Id}: Street is required.");
                if (string.IsNullOrWhiteSpace(person.HouseNumber)) issues.Add($"Employee {employee.Id}: HouseNumber is required.");
                if (string.IsNullOrWhiteSpace(person.Apartment)) issues.Add($"Employee {employee.Id}: Apartment is required.");
                if (Digits(person.PostalCode).Length == 0) issues.Add($"Employee {employee.Id}: PostalCode is required.");
                if (string.IsNullOrWhiteSpace(person.PostOfficeBox)) issues.Add($"Employee {employee.Id}: PostOfficeBox is required.");
                if (!c.Employments.ContainsKey(employee.EmploymentId)) issues.Add($"Employee {employee.Id}: employment profile was not found.");
            }
        }
        return issues;
    }

    private static void ValidatePaymentAccount(BuildContext c, ManualReportProduct product, string label, bool requireBankAccount, List<string> issues)
    {
        var payment = c.Payments.FirstOrDefault(x => x.ReportProductId == product.Id);
        if (!requireBankAccount) return;
        if (payment is null) { issues.Add($"{label}: payment/bank details are required."); return; }
        if (!int.TryParse(Digits(payment.EmployerBankCode), out _)) issues.Add($"{label}: employer bank code must be numeric.");
        if (Digits(payment.EmployerBranch).Length != 3) issues.Add($"{label}: employer bank branch must be exactly 3 digits.");
        if (Digits(payment.EmployerAccount).Length != 20) issues.Add($"{label}: employer bank account must be exactly 20 digits.");
    }

    private static int ParseRequiredCode(string value) => int.Parse(value.Trim(), CultureInfo.InvariantCulture);
    private static bool TryCode(string? value, int[] allowed, out int code) => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code) && allowed.Contains(code);
    private static string MapProductCode(PensionProductType type) => type switch
    {
        PensionProductType.ManagersInsurance => "1", PensionProductType.PensionFund => "2",
        PensionProductType.ProvidentFund => "3", PensionProductType.StudyFund => "4", _ => "4"
    };
    private static string MapContributionCode(ManualContribution c) => (c.Party, c.Component) switch
    {
        (ContributionParty.Employer, ContributionComponent.Severance) => "1",
        (ContributionParty.Employee, ContributionComponent.Benefits) => "2",
        (ContributionParty.Employer, ContributionComponent.Benefits) => "3",
        (ContributionParty.Employee, ContributionComponent.Disability) => "5",
        (ContributionParty.Employer, ContributionComponent.Disability) => "6",
        (ContributionParty.Employee, ContributionComponent.Other) => "7",
        (ContributionParty.Employer, ContributionComponent.Other) => "8",
        _ => "8"
    };
    private static XElement E(string name, object? value) => new(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    private static XElement Nil(string name, object? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))) return E(name, value);
        return new XElement(name, new XAttribute(Xsi + "nil", "true"));
    }
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string EmployerContactMobile(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 0 ? "0500000000" : digits;
    }
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string UpperGuid(Guid value) => value.ToString("D").ToUpperInvariant();
    private static string BuildFileNumber(DateTimeOffset now, string senderId)
    {
        var normalized = senderId.Length > 16 ? senderId[^16..] : senderId.PadLeft(16, '0');
        return $"{now:yyyyMMddHHmmss}{normalized}0001";
    }

    public sealed record BuildContext(Employer Employer, IReadOnlyList<ManualReportEmployee> Employees,
        IReadOnlyDictionary<Guid, Person> People, IReadOnlyDictionary<Guid, Employment> Employments,
        IReadOnlyList<ManualReportProduct> Products, IReadOnlyList<ManualContribution> Contributions,
        IReadOnlyList<ManualReportPayment> Payments, IReadOnlyList<EmployerInterfaceReportProductData> ProductMetadata,
        EmployerInterface006Options Options);
    public sealed record BuildResult(XDocument? Document, IReadOnlyList<string> Issues);
}
