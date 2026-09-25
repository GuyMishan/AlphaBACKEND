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
    private static readonly int[] IdentifierTypeCodes = [1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13];

    public static BuildResult BuildCurrent(BuildContext context) => Build(context, false);
    public static BuildResult BuildNegative(BuildContext context) => Build(context, true);

    private static BuildResult Build(BuildContext c, bool negative)
    {
        var issues = Validate(c, negative);
        if (issues.Count > 0) return new(null, issues);

        var now = c.PreparedAt ?? DateTimeOffset.UtcNow;
        var sender = ResolveSender(c);
        var senderId = sender.Identifier;
        var groups = c.Products.GroupBy(x => x.FundCode, StringComparer.Ordinal).ToList();
        var root = new XElement("MimshakMaasikim", new XAttribute(XNamespace.Xmlns + "xsi", Xsi));
        root.Add(BuildHeader(c, negative, now, senderId));

        var body = new XElement("GufHamimshak");
        foreach (var group in groups) body.Add(BuildRequester(c, group.ToList(), negative, senderId));
        root.Add(body);

        var emittedContributions = c.Products.SelectMany(product => EffectiveContributions(c, product, negative)).ToList();
        var totalContributions = emittedContributions.Sum(x => x.Amount);
        var totalDeposits = groups.Sum(group => ReportedDepositAmount(c, group.ToList(), negative));
        root.Add(new XElement("ReshumatSgira",
            E("MISPAR-KUPOT-YATZRANIM-BAKOVETZ", groups.Count),
            E("MISPAR-MAASIKIM", groups.Count),
            E("MISPAR-RESHUMOT", emittedContributions.Count),
            E("MISPAR-AMITIM", groups.Sum(group => group.Select(x => x.ReportEmployeeId).Distinct().Count())),
            E("SACH-HAFRASHOT-BAKOVETZ", Money(totalContributions)),
            E("SACH-HAFKADOT-BAKOVETZ", Money(totalDeposits))));

        return new(new XDocument(new XDeclaration("1.0", "utf-8", null), root), []);
    }

    private static XElement BuildHeader(BuildContext c, bool negative, DateTimeOffset now, string senderId)
    {
        var o = c.Options;
        var sender = ResolveSender(c);
        return new XElement("KoteretKovetz",
            E("SUG-MIMSHAK", negative ? 13 : 12),
            E("MISPAR-GIRSAT-XML", EmployerInterfaceSchemaRegistry.Version),
            E("TAARICH-BITZUA", now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)),
            E("KOD-SVIVAT-AVODA", o.EnvironmentCode),
            E("MISPAR-HAKOVETZ", BuildFileNumber(now, senderId, c.FileSequence)),
            E("MISPAR-SIDURI", c.FileSequence),
            new XElement("NetuneiGoremSholech",
                E("KOD-SHOLECH", o.SenderCode),
                E("SUG-MEZAHE-SHOLECH", o.SenderIdentifierType),
                E("MISPAR-ZIHUI-SHOLECH", sender.Identifier),
                E("SHEM-GOREM-SHOLECH", sender.Name),
                E("SHEM-PRATI-ISH-KESHER-SHOLECH", sender.ContactFirstName),
                E("SHEM-MISHPACHA-ISH-KESHER-SHOLECH", sender.ContactLastName),
                E("MISPAR-TELEPHONE-KAVI-ISH-KESHER-SHOLECH", sender.ContactPhone),
                E("E-MAIL-ISH-KESHER-SHOLECH", sender.ContactEmail),
                Nil("MISPAR-CELLULARI-ISH-KESHER-SHOLECH", sender.ContactMobile)),
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
        var reportedDeposit = ReportedDepositAmount(c, products, negative);
        var receiverAccount = ParseReceiverAccount(payment?.ProviderAccount);

        var transfer = new XElement("PirteiHaavaratKsafim",
            E("KOD-MEZAHE-KUPA-H-P", Digits(first.FundCode)),
            E("SUG-MAFKID", c.DepositorTypeCode),
            E("SUG-MEZAHE-MAASIK", c.EmployerIdentifierTypeCode),
            E("MISPAR-ZIHUY-MAASIK", c.Employer.RegistrationNumber.Trim()),
            negative ? Nil("MISPAR-TIK-NIKUIM-MAASIK", null) : E("MISPAR-TIK-NIKUIM-MAASIK", EmployerWithholdingFile(c)),
            Nil("KOD-MEZAHE-MAASIK-ETZEL-YATZRAN", null),
            !negative && metadata.PaymentMethodCode == 7 ? E("KOD-MASAV", payment!.MasavSenderCode) : Nil("KOD-MASAV", null),
            E("SCHUM-HAFKADA-KOLEL", Money(reportedDeposit)),
            E("SHEM-MAASIK", c.Employer.LegalName),
            E("SHEM-PRATI-ISH-KESHER-MAASIK", c.Employer.ContactFirstName),
            E("SHEM-MISHPACHA-ISH-KESHER-MAASIK", c.Employer.ContactLastName),
            E("MISPAR-TELEPHONE-KAVI-ISH-KESHER-MAASIK", Digits(c.Employer.ContactPhone)),
            E("E-MAIL-ISH-KESHER-MAASIK", c.Employer.ContactEmail),
            E("MISPAR-CELLULARI-ISH-KESHER-MAASIK", EmployerContactMobile(c.Employer.ContactMobile)),
            E("SUG-PEULA", metadata.OperationCode!.Value));

        if (!negative)
        {
            var operationCode = metadata.OperationCode!.Value;
            var paymentMethod = metadata.PaymentMethodCode!.Value;
            var correctionWithoutMoney = operationCode is 2 or 7;
            var zeroEmployerAccount = correctionWithoutMoney || reportedDeposit == 0 || paymentMethod is 3 or 5 or 6 or 9;
            // Clearinghouse V6: receiver account is mandatory for bank transfer only when money is actually
            // transferred, and for MASAV (7). No receiver bank details are sent for no-money corrections.
            var requiresReceiverAccount = !correctionWithoutMoney
                && ((paymentMethod == 1 && reportedDeposit > 0) || paymentMethod == 7);
            var trustDateRelevant = !correctionWithoutMoney
                && (metadata.EmployerAccountType == 2 || metadata.ReceiverAccountType == 2);
            var fileDate = (c.PreparedAt ?? DateTimeOffset.UtcNow).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var valueDateText = correctionWithoutMoney || paymentMethod is 6 or 9
                ? fileDate
                : metadata.ReceiverAccountType == 1
                    ? payment?.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            var reference = correctionWithoutMoney || reportedDeposit == 0 || paymentMethod is 6 or 9
                ? "000"
                : string.IsNullOrWhiteSpace(payment?.ReferenceNumber) ? "000" : payment!.ReferenceNumber;
            transfer.Add(
                E("KOD-EMTZAI-TASHLUM", paymentMethod),
                E("SACH-HAFKADA-KUPA-H-P", Money(reportedDeposit)),
                Nil("TAARICH-ERECH-HAFKADA-LEKUPA", valueDateText),
                Nil("TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT",
                    trustDateRelevant ? payment?.TrustAccountValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null),
                E("MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM", reference),
                E("MISPAR-ZIHUI", CurrentTransferIdentifier(metadata, first.Id)),
                E("MISPAR-BANK-MAASIK", zeroEmployerAccount ? 0 : int.Parse(Digits(payment!.EmployerBankCode), CultureInfo.InvariantCulture)),
                E("MISPAR-SNIF-MAASIK", zeroEmployerAccount ? "000" : FixedDigits(payment!.EmployerBranch, 3)),
                E("MISPAR-CHESHBON-MAASIK", zeroEmployerAccount ? new string('0', 20) : FixedDigits(payment!.EmployerAccount, 20)),
                Nil("SUG-CHESHBON", null),
                E("SUG-CHESHBON-MAASIK", metadata.EmployerAccountType!.Value),
                E("SUG-CHESHBON-KOLET-TASHLUM", metadata.ReceiverAccountType!.Value),
                requiresReceiverAccount ? E("MISPAR-BANK-KOLET", receiverAccount.BankCode!.Value) : Nil("MISPAR-BANK-KOLET", null),
                requiresReceiverAccount ? E("MISPAR-SNIF-KOLET", FixedDigits(receiverAccount.BranchCode, 3)) : Nil("MISPAR-SNIF-KOLET", null),
                requiresReceiverAccount ? E("MISPAR-CHESHBON-KOLET", FixedDigits(receiverAccount.AccountNumber, 20)) : Nil("MISPAR-CHESHBON-KOLET", null));
        }
        else
        {
            if (metadata.OperationCode == 5)
                transfer.Add(E("KOD-EMTZAI-TASHLUM", metadata.PaymentMethodCode!.Value));
            else
                transfer.Add(Nil("KOD-EMTZAI-TASHLUM", null));
            transfer.Add(E("SACH-HAFKADA-KUPA-H-P", Money(reportedDeposit)));
            transfer.Add(E("MISPAR-ZIHUI", CurrentTransferIdentifier(metadata, first.Id)));
            if (metadata.OperationCode == 5 && metadata.PaymentMethodCode == 1)
            {
                transfer.Add(
                    E("MISPAR-BANK-MAASIK", int.Parse(Digits(payment!.EmployerBankCode), CultureInfo.InvariantCulture)),
                    E("MISPAR-SNIF-MAASIK", FixedDigits(payment.EmployerBranch, 3)),
                    E("MISPAR-CHESHBON-MAASIK", FixedDigits(payment.EmployerAccount, 20)));
            }
        }

        transfer.Add(Nil("MISPAR-ZIHUI-KODEM", null), Nil("MISPAR-MISLAKA", null), Nil("MISPAR-MISLAKA-KODEM", null));

        var attachments = c.Attachments
            .Where(x => x.ReportProductId is null || productIds.Contains(x.ReportProductId.Value))
            .OrderBy(x => x.DocumentTypeCode).ThenBy(x => x.CreatedAt).ToList();
        foreach (var attachment in attachments)
        {
            transfer.Add(new XElement("ZihuiShemMismachBeramatEirua",
                E("SHEM-KOVETZ-SHEL-MISMACH-BERAMAT-EIRUA-VEBERAMAT-LAKOACH",
                    c.AttachmentTransmissionNames.TryGetValue(attachment.Id, out var transmissionName)
                        ? transmissionName
                        : attachment.TransmissionFileName),
                E("SUG-MISMACH", attachment.DocumentTypeCode)));
        }

        transfer.Add(BuildFund(c, products, negative));

        var directEmployerSender = c.Options.SenderCode == 5;
        var requester = new XElement("YeshutGoremPoneLemislaka");
        if (directEmployerSender)
        {
            requester.Add(
                Nil("SUG-PONE", null),
                Nil("SUG-KOD-MEZAHE-PONE", null),
                Nil("MISPAR-MEZAHE-PONE", null),
                Nil("SHEM-GOREM-PONE", null),
                Nil("MISPAR-MEZAHE-METAFEL", null),
                Nil("SHEM-PRATI-PONE-LEMISLAKA", null),
                Nil("SHEM-MISHPACHA-PONE-LEMISLAKA", null),
                Nil("MISPAR-TELEPHONE-KAVI-PONE-LEMISLAKA", null),
                Nil("E-MAIL-PONE-LEMISLAKA", null),
                Nil("MISPAR-CELLULARI", null));
        }
        else
        {
            requester.Add(
                E("SUG-PONE", 5),
                E("SUG-KOD-MEZAHE-PONE", c.EmployerIdentifierTypeCode),
                E("MISPAR-MEZAHE-PONE", c.Employer.RegistrationNumber.Trim()),
                E("SHEM-GOREM-PONE", c.Employer.LegalName),
                Nil("MISPAR-MEZAHE-METAFEL", senderId),
                Nil("SHEM-PRATI-PONE-LEMISLAKA", c.Employer.ContactFirstName),
                Nil("SHEM-MISHPACHA-PONE-LEMISLAKA", c.Employer.ContactLastName),
                Nil("MISPAR-TELEPHONE-KAVI-PONE-LEMISLAKA", Digits(c.Employer.ContactPhone)),
                Nil("E-MAIL-PONE-LEMISLAKA", c.Employer.ContactEmail),
                Nil("MISPAR-CELLULARI", EmployerContactMobile(c.Employer.ContactMobile)));
        }
        requester.Add(transfer);
        return requester;
    }

    private static XElement BuildFund(BuildContext c, IReadOnlyList<ManualReportProduct> products, bool negative)
    {
        var first = products[0];
        var firstMetadata = c.ProductMetadata.First(x => x.ReportProductId == first.Id);
        var oldPensionTypeCode = !negative ? firstMetadata.OldPensionTypeCode : null;
        var fund = new XElement("PirteiKupa",
            E("SUG-KUPA", MapProductCode(first.ProductType)),
            Nil("SUG-KEREN-PENSIA", oldPensionTypeCode),
            // Internal employer fund name is a different field from the official fund name.
            // Alpha does not currently store an employer-specific internal fund name, so Version 006 emits xsi:nil.
            Nil("SHEM-KUPA-ETZEL-MAASIK", null),
            Nil("MISPAR-KUPA-ETZEL-MAASIK", null));

        // Version 006 clearinghouse rules require one PirteiOved block per employee in a batch.
        // Multiple products/policies for the same employee are represented by multiple
        // ChodeshMaskoretVestatusOved blocks under the same PirteiOved.
        foreach (var employeeProducts in products.GroupBy(x => x.ReportEmployeeId))
            fund.Add(BuildEmployee(c, employeeProducts.OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).ToList(), negative));

        var ids = products.Select(x => x.Id).ToHashSet();
        var total = products.SelectMany(product => EffectiveContributions(c, product, negative)).Sum(x => x.Amount);
        var reportedDeposit = ReportedDepositAmount(c, products, negative);
        if (total > 0)
            fund.Add(new XElement("SachHafrashaLeKupaMaasik",
                E("SACH-HAFRASHA-LEKUPA-BERAMAT-MAASIK", Money(total)),
                E("SACH-HAFKADA-LEKUPA-BERAMAT-MAASIK", Money(reportedDeposit)),
                E("MISPAR-AMITIM-BERAMAT-MAASIK", products.Select(x => c.Employees.Single(e => e.Id == x.ReportEmployeeId).PersonId).Distinct().Count())));
        return fund;
    }

    private static XElement BuildEmployee(BuildContext c, IReadOnlyList<ManualReportProduct> products, bool negative)
    {
        var firstProduct = products[0];
        var employee = c.Employees.Single(x => x.Id == firstProduct.ReportEmployeeId);

        var node = new XElement("PirteiOved",
            E("SUG-MEZAHE-OVED", employee.InterfaceIdentifierType),
            E("MISPAR-MEZAHE", employee.InterfaceIdentifier),
            E("SHEM-PRATI", employee.FirstName),
            E("SHEM-MISHPACHA", employee.LastName));

        if (!negative) node.Add(E("TAARICH-LEIDA", employee.BirthDateSnapshot!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        node.Add(Nil("MISPAR-OVED-ETZEL-MAASIK", employee.EmployeeNumber));

        if (!negative)
        {
            node.Add(
                Nil("SHEM-YISHUV", employee.CitySnapshot),
                Nil("SHEM-RECHOV", employee.StreetSnapshot),
                Nil("MISPAR-BAIT", employee.HouseNumberSnapshot),
                Nil("MISPAR-DIRA", string.IsNullOrWhiteSpace(employee.ApartmentSnapshot) ? null : employee.ApartmentSnapshot),
                Nil("MIKUD", string.IsNullOrWhiteSpace(employee.PostalCodeSnapshot) ? null : Digits(employee.PostalCodeSnapshot)),
                Nil("TA-DOAR", string.IsNullOrWhiteSpace(employee.PostOfficeBoxSnapshot) ? null : Digits(employee.PostOfficeBoxSnapshot)),
                E("E-MAIL", employee.EmailSnapshot.Trim()),
                E("MISPAR-CELLULARI", Digits(employee.MobileSnapshot)),
                E("MIN", employee.GenderSnapshot!.Value),
                E("MOED-TCHILAT-AHASAKAT-OVED", employee.EmploymentStartDateSnapshot!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                E("SEIF-ARBA-ESRE-LAOVED", firstProduct.Section14Code),
                Nil("SEIF-ARBA-ESRE-TAHRIH-KNISA-LETOKEF", firstProduct.Section14StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        else
        {
            var productIds = products.Select(x => x.Id).ToHashSet();
            var operation5 = products.Any(p => c.ProductMetadata.Single(x => x.ReportProductId == p.Id).OperationCode == 5);
            var employeeAttachments = c.Attachments.Where(x => x.ReportProductId.HasValue && productIds.Contains(x.ReportProductId.Value)).ToList();
            node.Add(
                operation5 ? E("HASHAVA-KIBUTZI", employeeAttachments.Any(x => x.DocumentTypeCode == 6) ? 1 : 2) : Nil("HASHAVA-KIBUTZI", null),
                operation5 ? E("HATZHARAT-OVED", employeeAttachments.Any(x => x.DocumentTypeCode == 4) ? 1 : 2) : Nil("HATZHARAT-OVED", null),
                Nil("SEIF-ARBA-ESRE-LAOVED", null),
                Nil("SEIF-ARBA-ESRE-TAHRIH-KNISA-LETOKEF", null));
        }

        decimal employeeTotal = 0;
        foreach (var product in products)
        {
            var metadata = c.ProductMetadata.Single(x => x.ReportProductId == product.Id);
            var contributions = EffectiveContributions(c, product, negative);
            var total = contributions.Sum(x => x.Amount);
            employeeTotal += total;

            var salary = new XElement("ChodeshMaskoretVestatusOved",
                E("CHODESH-MASKORET", product.SalaryMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            if (!negative) salary.Add(E("MAHAMAD-HAFKADA-BEKUPA", metadata.DepositStatus!.Value));
            salary.Add(E("SUG-TAKBUL", ParseRequiredCode(product.ReportingType)), E("ROVED-SACHAR", ParseRequiredCode(product.SalaryLayer)));
            if (negative) salary.Add(E("SIBAT-BAKASH-LECHZER-KSAFIM", metadata.RefundReason!.Value));
            if (!negative)
            {
                salary.Add(metadata.OperationCode == 7 ? Nil("SACHAR-MEDUVACH", null) : E("SACHAR-MEDUVACH", Money(product.Salary)),
                    E("STATUS-OVED-BECHODESH-MASKORET", metadata.EmployeeStatus!.Value),
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
                if (!negative)
                    split.Add(HasMoneyTransfer(c, product) && contribution.Percentage > 0
                        ? E("SHIUR-HAFRASHA", contribution.Percentage)
                        : Nil("SHIUR-HAFRASHA", null));
                split.Add(E("SCHUM-HAFRASHA", Money(contribution.Amount)));
                if (!negative) split.Add(E("SACH-TASHLUMIM-PTURIM", Money(contribution.ExemptPayments)));
                split.Add(E("MISPAR-MEZAHE-RESHUMA", CurrentRecordIdentifier(contribution)), Nil("MISPAR-MEZAHE-RESHUMA-KODEM", contribution.PreviousRecordIdentifier));
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
        var sender = ResolveSender(c);
        if (c.DepositorTypeCode is < 1 or > 3) issues.Add("EmployerInterface006:DepositorTypeCode must be 1, 2 or 3.");
        if (!IdentifierTypeCodes.Contains(c.EmployerIdentifierTypeCode))
            issues.Add("EmployerInterface006: EmployerIdentifierTypeCode must be one of 1,2,3,4,5,7,8,9,10,11,12,13.");
        if (o.EnvironmentCode is not (1 or 2)) issues.Add("EmployerInterface006:EnvironmentCode must be 1 (TEST) or 2 (PRODUCTION).");
        if (o.FileDirectionCode is < 1 or > 999) issues.Add("EmployerInterface006:FileDirectionCode must contain a valid Annex VI 3-digit direction code.");
        if (o.SenderCode is < 2 or > 6)
            issues.Add("EmployerInterface006:SenderCode must be one of 2-6; Version 006 marks sender code 1 as not relevant for employer reports.");
        if (sender.Identifier.Length is 0 or > 16) issues.Add("EmployerInterface006: actual sender identifier is required and cannot exceed 16 characters.");
        if (string.IsNullOrWhiteSpace(sender.Name) || sender.Name.Length > 100) issues.Add("EmployerInterface006: actual sender name is required and cannot exceed 100 characters.");
        if (string.IsNullOrWhiteSpace(sender.ContactFirstName) || sender.ContactFirstName.Length > 20) issues.Add("EmployerInterface006: sender contact first name is required and cannot exceed 20 characters.");
        if (string.IsNullOrWhiteSpace(sender.ContactLastName) || sender.ContactLastName.Length > 20) issues.Add("EmployerInterface006: sender contact last name is required and cannot exceed 20 characters.");
        if (!IsDigits(sender.ContactPhone) || sender.ContactPhone.Length > 11) issues.Add("EmployerInterface006: sender contact phone must contain digits only and cannot exceed 11 digits.");
        if (string.IsNullOrWhiteSpace(sender.ContactEmail) || sender.ContactEmail.Length > 50 || !sender.ContactEmail.Contains('@')) issues.Add("EmployerInterface006: sender contact email is required and must be a valid address up to 50 characters.");
        if (sender.ContactMobile.Length > 0 && (!IsDigits(sender.ContactMobile) || sender.ContactMobile.Length > 15)) issues.Add("EmployerInterface006: sender contact mobile must contain digits only and cannot exceed 15 digits.");
        if (!IdentifierTypeCodes.Contains(o.SenderIdentifierType))
            issues.Add("EmployerInterface006:SenderIdentifierType must be one of 1,2,3,4,5,7,8,9,10,11,12,13.");
        if (o.RecipientCode is not (1 or 2 or 3 or 6)) issues.Add("EmployerInterface006:RecipientCode is required and must be 1, 2, 3 or 6.");
        if (!IdentifierTypeCodes.Contains(o.RecipientIdentifierType))
            issues.Add("EmployerInterface006:RecipientIdentifierType must be one of 1,2,3,4,5,7,8,9,10,11,12,13.");
        if (string.IsNullOrWhiteSpace(o.RecipientIdentifier)) issues.Add("EmployerInterface006:RecipientIdentifier is required.");
        if (Digits(c.Employer.RegistrationNumber).Length is 0 or > 16) issues.Add("Employer registration number must contain 1-16 digits for Version 006.");
        if (!negative)
        {
            var withholding = Digits(c.Employer.WithholdingFileNumber);
            if (withholding.Length > 0 && (withholding.Length != 9 || !withholding.StartsWith("9", StringComparison.Ordinal)))
                issues.Add("Employer withholding file number must contain exactly 9 digits and start with 9.");
            if (withholding.Length == 0 && c.DepositorTypeCode == 3)
                issues.Add("Small-employer depositor type 3 requires a real withholding-file number; the 900000000 fallback is explicitly defined for depositor types 1 and 2.");
        }
        if ((c.Employer.ContactFirstName?.Trim().Length ?? 0) is < 2 or > 20) issues.Add("Employer Interface contact first name must contain 2-20 characters.");
        if ((c.Employer.ContactLastName?.Trim().Length ?? 0) is < 2 or > 20) issues.Add("Employer Interface contact last name must contain 2-20 characters.");
        if (Digits(c.Employer.ContactPhone).Length is < 9 or > 20) issues.Add("Employer Interface contact phone must contain 9-20 digits.");
        if (string.IsNullOrWhiteSpace(c.Employer.ContactEmail) || c.Employer.ContactEmail.Length > 50 || !c.Employer.ContactEmail.Contains('@')) issues.Add("Employer Interface contact email must be a valid address up to 50 characters.");
        var employerMobile = c.Employer.ContactMobile?.Trim() ?? string.Empty;
        if (employerMobile.Length > 0 && (!IsDigits(employerMobile) || employerMobile.Length != 10 || !employerMobile.StartsWith("05", StringComparison.Ordinal)))
            issues.Add("Employer Interface contact mobile must contain digits only and match ^05\\d\\d{7}$; when no mobile exists, Version 006 requires 0500000000.");
        if (c.Products.Count == 0) issues.Add("The report has no pension products to export.");

        if (!negative)
        {
            foreach (var attachment in c.Attachments)
            {
                if (attachment.DocumentTypeCode != 5)
                    issues.Add($"Attachment {attachment.Id}: current Version 006 supports document type 5 only.");
                if (attachment.ReportProductId is null)
                {
                    issues.Add($"Attachment {attachment.Id}: document type 5 must be linked to a report product.");
                }
                else
                {
                    var product = c.Products.FirstOrDefault(x => x.Id == attachment.ReportProductId.Value);
                    if (product is null)
                    {
                        issues.Add($"Attachment {attachment.Id}: linked report product was not found.");
                    }
                    else
                    {
                        if (product.ProductType != PensionProductType.PensionFund)
                            issues.Add($"Attachment {attachment.Id}: Version 006 document type 5 is relevant only to a pension fund.");
                        var productMetadata = c.ProductMetadata.FirstOrDefault(x => x.ReportProductId == product.Id);
                        if (productMetadata?.EmployeeStatus == 14)
                            issues.Add($"Attachment {attachment.Id}: Version 006 document type 5 is for an existing employee and must not be used with employee status 14 (new employee).");
                    }
                }
                if (attachment.TransmissionFileName.Length is 0 or > 100)
                    issues.Add($"Attachment {attachment.Id}: transmission file name must contain 1-100 characters.");
            }
        }
        if (negative)
        {
            foreach (var attachment in c.Attachments)
            {
                if (attachment.DocumentTypeCode is not (3 or 4 or 6))
                    issues.Add($"Attachment {attachment.Id}: negative Version 006 supports document types 3, 4 and 6 only.");
                if (attachment.TransmissionFileName.Length is 0 or > 100)
                    issues.Add($"Attachment {attachment.Id}: transmission file name must contain 1-100 characters.");
                if (attachment.DocumentTypeCode is 4 or 6 && attachment.ReportProductId is null)
                    issues.Add($"Attachment {attachment.Id}: document type {attachment.DocumentTypeCode} must be linked to a report product.");
            }

            var hasOperation5 = c.ProductMetadata.Any(x => x.OperationCode == 5);
            if (hasOperation5 && !c.AnnualEmployerAffidavitSatisfied)
                issues.Add("Negative operation 5 requires the annual employer affidavit (SUG-MISMACH=3) at least once per calendar year.");

        }

        foreach (var product in c.Products)
        {
            var label = $"Product {product.Id}";
            if (product.ProductType == PensionProductType.Other)
                issues.Add($"{label}: ProductType Other cannot be serialized to Employer Interface 006 because SUG-KUPA only allows codes 1-4.");
            if (product.FundCode.Length != 30 || !IsDigits(product.FundCode))
                issues.Add($"{label}: fund code must be exactly 30 digits (KOD-MEZAHE-KUPA-H-P).");
            if (!TryCode(product.ReportingType, CurrentReceiptCodes, out _)) issues.Add($"{label}: ReportingType must be one of 1,2,4,6,8 for Version 006.");
            if (!TryCode(product.SalaryLayer, SalaryLayerCodes, out _)) issues.Add($"{label}: SalaryLayer must be one of 1,3,5,6,7 for Version 006.");
            if (!negative)
            {
                if (product.Section14Code is < 1 or > 5) issues.Add($"{label}: Section14Code must be one of 1,2,3,4,5.");
                if (product.Section14Code is 2 or 4 && !product.Section14StartDate.HasValue)
                    issues.Add($"{label}: Section14StartDate is required for Section14Code 2 or 4.");
                if (product.ProductType == PensionProductType.StudyFund && product.Section14Code != 3)
                    issues.Add($"{label}: study funds must report Section14Code 3 because severance funds are not managed in this product.");
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
                var oldPension = IsOldPensionFund(product) || meta.OldPensionTypeCode.HasValue;
                if (oldPension && meta.OldPensionTypeCode is not (1 or 2))
                    issues.Add($"{label}: old pension funds require SUG-KEREN-PENSIA code 1 (מקיפה) or 2 (יסוד).");
                if (oldPension && !meta.EmploymentPercentage.HasValue && !meta.WorkDaysInMonth.HasValue)
                    issues.Add($"{label}: old pension funds require either employment percentage or work days in month.");
                if (!IsOldPensionFund(product) && meta.OldPensionTypeCode.HasValue && !string.IsNullOrWhiteSpace(product.FundClassification))
                    issues.Add($"{label}: SUG-KEREN-PENSIA is relevant only to an old pension fund.");
                if (meta.EmployeeStatus == 14 && product.Section14Code == 5)
                    issues.Add($"{label}: Section14Code 5 must not be used for a new employee/status 14.");
                if (!meta.PaymentMethodCode.HasValue || !PaymentMethodCodes.Contains(meta.PaymentMethodCode.Value)) issues.Add($"{label}: PaymentMethodCode must be one of 1,3,4,5,6,7,9.");
                if (meta.EmployerAccountType is not (1 or 2)) issues.Add($"{label}: EmployerAccountType must be 1 or 2 for a current report.");
                if (meta.ReceiverAccountType is not (1 or 2)) issues.Add($"{label}: ReceiverAccountType must be 1 or 2 for a current report.");
                if (meta.OperationCode is 2 or 7)
                {
                    if (meta.PaymentMethodCode != 1)
                        issues.Add($"{label}: no-money correction operation {meta.OperationCode} must use payment method 1.");
                    if (meta.OperationCode == 7 && meta.EmployerAccountType != 1)
                        issues.Add($"{label}: operation 7 has no payment and must report employer account type 1.");
                    if (meta.OperationCode == 7)
                    {
                        var exemptCorrectionRows = EffectiveContributions(c, product, false);
                        if (exemptCorrectionRows.Any(x => x.Amount != 0))
                            issues.Add($"{label}: operation 7 corrects exempt payments only; SCHUM-HAFRASHA must be 0 for every row.");
                        if (exemptCorrectionRows.All(x => x.ExemptPayments == 0))
                            issues.Add($"{label}: operation 7 requires at least one non-zero SACH-TASHLUMIM-PTURIM correction.");
                    }
                    // For operation 2, and for the receiver account on any no-money correction,
                    // Version 006 requires the account type used by the previous report being corrected.
                }
                if (meta.PaymentMethodCode == 9
                    && (meta.EmployerAccountType != 1 || meta.ReceiverAccountType != 1))
                    issues.Add($"{label}: payment method 9 requires employer and receiver account types to both be 1.");

                if (meta.OperationCode == 3)
                {
                    var groupIds = c.Products.Where(x => string.Equals(x.FundCode, product.FundCode, StringComparison.Ordinal))
                        .Select(x => x.Id).ToHashSet();
                    var actualAmounts = c.Payments.Where(x => groupIds.Contains(x.ReportProductId) && x.ActualDepositAmount.HasValue)
                        .Select(x => x.ActualDepositAmount!.Value).Distinct().ToArray();
                    if (actualAmounts.Length != 1 || actualAmounts[0] <= 0)
                        issues.Add($"{label}: operation 3 requires one consistent actual additional deposit amount greater than zero for the fund transfer.");
                }
                ValidatePaymentAccount(c, product, label, requirePayment: true, issues);
            }
            else
            {
                if (!meta.RefundReason.HasValue || meta.RefundReason is < 1 or > 10) issues.Add($"{label}: RefundReason 1-10 is required for a negative report.");
                if (meta.OperationCode == 5)
                {
                    if (!meta.PaymentMethodCode.HasValue || !PaymentMethodCodes.Contains(meta.PaymentMethodCode.Value))
                        issues.Add($"{label}: negative Version 006 operation 5 requires a valid PaymentMethodCode.");
                    ValidatePaymentAccount(c, product, label, requirePayment: meta.PaymentMethodCode == 1, issues);
                }
                else if (meta.OperationCode == 6 && meta.PaymentMethodCode.HasValue)
                {
                    issues.Add($"{label}: negative Version 006 operation 6 must not carry PaymentMethodCode.");
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

            var contributions = EffectiveContributions(c, product, negative);
            var contributionCodes = contributions.Select(MapContributionCode).ToList();
            if (contributionCodes.GroupBy(x => x).Any(g => g.Key != "4" && g.Count() > 1))
                issues.Add($"{label}: the same SUG-HAFRASHA contribution type cannot be reported more than once in one salary/status block, except code 4.");

            if (product.ProductType == PensionProductType.StudyFund)
            {
                if (contributions.Any(x => MapContributionCode(x) is not ("2" or "3")))
                    issues.Add($"{label}: study funds may report only employee contributions under code 2 and employer contributions under code 3.");
            }
            else if (product.ProductType is PensionProductType.PensionFund or PensionProductType.ProvidentFund)
            {
                if (contributions.Any(x => int.Parse(MapContributionCode(x), CultureInfo.InvariantCulture) >= 5))
                    issues.Add($"{label}: pension and provident funds must not report SUG-HAFRASHA codes 5-8.");
            }

            if (!negative && meta.DepositStatus == 1 && ParseRequiredCode(product.ReportingType) == 1 && HasMoneyTransfer(c, product)
                && contributions.Any(x => x.Percentage <= 0))
                issues.Add($"{label}: routine salaried deposits require SHIUR-HAFRASHA for every reported contribution component.");
            if (negative && contributions.Count == 0) issues.Add($"{label}: negative Version 006 requires at least one contribution record.");
            if (negative && contributions.Any(x => x.Amount <= 0)) issues.Add($"{label}: negative contribution amounts must be greater than zero.");
        }

        if (!negative)
        {
            foreach (var employee in c.Employees)
            {
                if (employee.InterfaceIdentifierType is not (1 or 2))
                    issues.Add($"Employee {employee.Id}: SUG-MEZAHE-OVED must be 1 or 2.");
                if (string.IsNullOrWhiteSpace(employee.InterfaceIdentifier) || employee.InterfaceIdentifier.Length > 16)
                    issues.Add($"Employee {employee.Id}: MISPAR-MEZAHE is required and cannot exceed 16 characters.");
                if (!employee.BirthDateSnapshot.HasValue) issues.Add($"Employee {employee.Id}: BirthDate snapshot is required.");
                if (employee.GenderSnapshot is not (1 or 2)) issues.Add($"Employee {employee.Id}: Gender snapshot is required.");
                if (string.IsNullOrWhiteSpace(employee.EmailSnapshot))
                    issues.Add($"Employee {employee.Id}: Email snapshot is required by the current Version 006 employee block.");
                else if (employee.EmailSnapshot.Length > 50 || !employee.EmailSnapshot.Contains('@') || employee.EmailSnapshot.StartsWith('@') || employee.EmailSnapshot.EndsWith('@'))
                    issues.Add($"Employee {employee.Id}: Email snapshot must be a real valid address containing up to 50 characters.");
                var employeeMobile = Digits(employee.MobileSnapshot);
                if (employeeMobile.Length is < 7 or > 15)
                    issues.Add($"Employee {employee.Id}: Mobile snapshot is required and must contain 7-15 digits.");
                if (!employee.EmploymentStartDateSnapshot.HasValue)
                    issues.Add($"Employee {employee.Id}: Employment start-date snapshot is required.");

                var employeeProducts = c.Products.Where(x => x.ReportEmployeeId == employee.Id).ToList();
                var isNewEmployee = employeeProducts.Any(p => c.ProductMetadata.FirstOrDefault(x => x.ReportProductId == p.Id)?.EmployeeStatus == 14);
                if (isNewEmployee)
                {
                    var hasPostalBox = Digits(employee.PostOfficeBoxSnapshot).Length > 0;
                    var hasStreetAddress = !string.IsNullOrWhiteSpace(employee.CitySnapshot)
                        && !string.IsNullOrWhiteSpace(employee.StreetSnapshot)
                        && !string.IsNullOrWhiteSpace(employee.HouseNumberSnapshot)
                        && Digits(employee.PostalCodeSnapshot).Length > 0;
                    if (!hasPostalBox && !hasStreetAddress)
                        issues.Add($"Employee {employee.Id}: status 14 requires either a postal box or the required street-address fields.");
                }
            }
        }
        return issues;
    }

    private static void ValidatePaymentAccount(BuildContext c, ManualReportProduct product, string label, bool requirePayment, List<string> issues)
    {
        var payment = c.Payments.FirstOrDefault(x => x.ReportProductId == product.Id);
        if (!requirePayment) return;

        var metadata = c.ProductMetadata.FirstOrDefault(x => x.ReportProductId == product.Id);
        var correctionWithoutMoney = metadata?.OperationCode is 2 or 7;
        if (payment is null)
        {
            if (correctionWithoutMoney) return;
            issues.Add($"{label}: payment details are required.");
            return;
        }
        var effectiveDeposit = metadata is null ? 0m : ReportedDepositAmount(c, [product], false);
        var paymentMethod = metadata?.PaymentMethodCode;

        if (!correctionWithoutMoney && metadata?.ReceiverAccountType == 1 && paymentMethod is not (6 or 9) && payment.ValueDate is null)
            issues.Add($"{label}: receiver account type 1 requires TAARICH-ERECH-HAFKADA-LEKUPA.");
        if (!correctionWithoutMoney
            && (metadata?.EmployerAccountType == 2 || metadata?.ReceiverAccountType == 2)
            && payment.TrustAccountValueDate is null)
            issues.Add($"{label}: any transfer to or from a trust account requires TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT.");
        if (paymentMethod == 7 && (payment.MasavSenderCode.Length < 8 || payment.MasavSenderCode.Length > 16))
            issues.Add($"{label}: MASAV payment method 7 requires KOD-MASAV containing 8-16 characters.");
        if (!correctionWithoutMoney && effectiveDeposit > 0 && paymentMethod is 1 or 3 && string.IsNullOrWhiteSpace(payment.ReferenceNumber))
            issues.Add($"{label}: payment method {paymentMethod} requires the actual transfer/clearing reference number.");
        if (correctionWithoutMoney && payment.TrustAccountValueDate is not null)
            issues.Add($"{label}: operation {metadata?.OperationCode} must not include a trust-account value date.");

        // Employer bank details are emitted as zero when no money is transferred or for methods 3/5/6/9.
        var requiresEmployerBranchAccount = effectiveDeposit > 0 && paymentMethod is not (3 or 5 or 6 or 9);
        if (requiresEmployerBranchAccount)
        {
            if (!IsDigits(payment.EmployerBankCode))
                issues.Add($"{label}: employer bank code must contain digits only.");
            var branch = payment.EmployerBranch.Trim();
            if (branch.Length is < 1 or > 3 || !IsDigits(branch)) issues.Add($"{label}: employer bank branch must contain 1-3 digits only.");
            var account = payment.EmployerAccount.Trim();
            if (account.Length is < 1 or > 20 || !IsDigits(account)) issues.Add($"{label}: employer bank account must contain 1-20 digits only.");
        }

        // Receiver details: method 1 only with an actual deposit; method 7 when not a no-money correction.
        var requiresReceiver = !correctionWithoutMoney && ((paymentMethod == 1 && effectiveDeposit > 0) || paymentMethod == 7);
        if (requiresReceiver)
        {
            var receiver = ParseReceiverAccount(payment.ProviderAccount);
            if (!receiver.IsValid)
                issues.Add($"{label}: payment method {paymentMethod} requires receiving bank, branch and account details from the selected pension product.");
        }

        if ((payment.ReferenceNumber?.Length ?? 0) > 50)
            issues.Add($"{label}: transfer reference number cannot exceed 50 characters in Employer Interface 006.");
    }

    private static SenderIdentity ResolveSender(BuildContext c)
    {
        var o = c.Options;
        var directEmployer = o.SenderCode == 5;
        var identifier = string.IsNullOrWhiteSpace(o.SenderIdentifier)
            ? directEmployer ? Digits(c.Employer.RegistrationNumber) : string.Empty
            : o.SenderIdentifier.Trim();
        return new SenderIdentity(
            identifier,
            string.IsNullOrWhiteSpace(o.SenderName) && directEmployer ? c.Employer.LegalName : o.SenderName.Trim(),
            string.IsNullOrWhiteSpace(o.SenderContactFirstName) && directEmployer ? c.Employer.ContactFirstName : o.SenderContactFirstName.Trim(),
            string.IsNullOrWhiteSpace(o.SenderContactLastName) && directEmployer ? c.Employer.ContactLastName : o.SenderContactLastName.Trim(),
            Digits(string.IsNullOrWhiteSpace(o.SenderContactPhone) && directEmployer ? c.Employer.ContactPhone : o.SenderContactPhone),
            string.IsNullOrWhiteSpace(o.SenderContactEmail) && directEmployer ? c.Employer.ContactEmail : o.SenderContactEmail.Trim(),
            Digits(string.IsNullOrWhiteSpace(o.SenderContactMobile) && directEmployer ? c.Employer.ContactMobile : o.SenderContactMobile));
    }

    private static decimal ReportedDepositAmount(BuildContext c, IReadOnlyList<ManualReportProduct> products, bool negative)
    {
        var ids = products.Select(x => x.Id).ToHashSet();
        var total = products.SelectMany(product => EffectiveContributions(c, product, negative)).Sum(x => x.Amount);
        var operation = c.ProductMetadata.First(x => ids.Contains(x.ReportProductId)).OperationCode;
        if (negative && operation == 6) return 0m;
        if (!negative && operation is 2 or 7) return 0m;
        if (!negative && operation == 3)
        {
            var amounts = c.Payments.Where(x => ids.Contains(x.ReportProductId) && x.ActualDepositAmount.HasValue)
                .Select(x => x.ActualDepositAmount!.Value).Distinct().ToArray();
            return amounts.Length == 1 ? amounts[0] : 0m;
        }
        return total;
    }

    private static bool IsOldPensionFund(ManualReportProduct product) =>
        product.ProductType == PensionProductType.PensionFund
        && product.FundClassification.Contains("ותיק", StringComparison.Ordinal);

    private static readonly HashSet<int> NoContributionEmployeeStatuses = [3, 4, 5, 8, 9, 10, 11, 12, 17];

    private static List<ManualContribution> EffectiveContributions(BuildContext c, ManualReportProduct product, bool negative)
    {
        var items = c.Contributions.Where(x => x.ReportProductId == product.Id).ToList();
        if (negative) return items;
        var metadata = c.ProductMetadata.FirstOrDefault(x => x.ReportProductId == product.Id);
        return metadata?.EmployeeStatus is int status && NoContributionEmployeeStatuses.Contains(status) ? [] : items;
    }

    private static bool HasMoneyTransfer(BuildContext c, ManualReportProduct product)
    {
        var metadata = c.ProductMetadata.First(x => x.ReportProductId == product.Id);
        if (metadata.OperationCode is 2 or 7) return false;
        if (metadata.OperationCode == 3)
            return c.Payments.FirstOrDefault(x => x.ReportProductId == product.Id)?.ActualDepositAmount > 0;
        return EffectiveContributions(c, product, false).Sum(x => x.Amount) > 0;
    }

    private static string CurrentTransferIdentifier(EmployerInterfaceReportProductData metadata, Guid fallbackId) =>
        string.IsNullOrWhiteSpace(metadata.InterfaceTransferIdentifier)
            ? UpperGuid(fallbackId)
            : metadata.InterfaceTransferIdentifier.Trim().ToUpperInvariant();

    private static string CurrentRecordIdentifier(ManualContribution contribution) =>
        string.IsNullOrWhiteSpace(contribution.InterfaceRecordIdentifier)
            ? UpperGuid(contribution.Id)
            : contribution.InterfaceRecordIdentifier.Trim().ToUpperInvariant();

    private static string EmployeeMobile(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 0 ? "0500000000" : digits;
    }

    private static string EmployerWithholdingFile(BuildContext c)
    {
        var value = Digits(c.Employer.WithholdingFileNumber);
        if (value.Length == 9) return value;
        return c.DepositorTypeCode is 1 or 2 ? "900000000" : value;
    }

    private static int ParseRequiredCode(string value) => int.Parse(value.Trim(), CultureInfo.InvariantCulture);
    private static bool TryCode(string? value, int[] allowed, out int code) => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code) && allowed.Contains(code);
    private static string MapProductCode(PensionProductType type) => type switch
    {
        PensionProductType.ManagersInsurance => "1",
        PensionProductType.PensionFund => "2",
        PensionProductType.ProvidentFund => "3",
        PensionProductType.StudyFund => "4",
        _ => throw new InvalidOperationException($"Pension product type {type} has no SUG-KUPA mapping in Employer Interface 006.")
    };

    private static string MapContributionCode(ManualContribution c) => (c.Party, c.Component) switch
    {
        // Alpha's component numbers are party-relative in the editor:
        // employee component 1 = תג 45 / regular employee benefits (official code 2),
        // employee component 2 = תג 47 (official code 4).
        (ContributionParty.Employer, ContributionComponent.Severance) => "1",
        (ContributionParty.Employee, ContributionComponent.Severance) => "2",
        (ContributionParty.Employer, ContributionComponent.Benefits) => "3",
        (ContributionParty.Employee, ContributionComponent.Benefits) => "4",
        (ContributionParty.Employee, ContributionComponent.Disability) => "5",
        (ContributionParty.Employer, ContributionComponent.Disability) => "6",
        (ContributionParty.Employee, ContributionComponent.Other) => "7",
        (ContributionParty.Employer, ContributionComponent.Other) => "8",
        _ => throw new InvalidOperationException($"Contribution pair {c.Party}/{c.Component} has no SUG-HAFRASHA mapping.")
    };
    private static XElement E(string name, object? value) => new(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
    private static XElement Nil(string name, object? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))) return E(name, value);
        return new XElement(name, new XAttribute(Xsi + "nil", "true"));
    }
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string FixedDigits(string? value, int width)
    {
        var digits = Digits(value);
        return digits.Length >= width ? digits[^width..] : digits.PadLeft(width, '0');
    }

    private static ReceiverAccount ParseReceiverAccount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return default;
        var bankDigits = parts[0].Trim();
        var branchDigits = parts[1].Trim();
        var accountDigits = parts[2].Trim();
        if (!IsDigits(bankDigits) || !IsDigits(branchDigits) || !IsDigits(accountDigits)
            || !int.TryParse(bankDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var bankCode)
            || bankCode is <= 0 or > 999
            || branchDigits.Length is < 1 or > 3
            || accountDigits.Length is < 1 or > 20)
            return default;
        return new ReceiverAccount(bankCode, branchDigits, accountDigits);
    }

    private readonly record struct SenderIdentity(string Identifier, string Name, string ContactFirstName,
        string ContactLastName, string ContactPhone, string ContactEmail, string ContactMobile);

    private readonly record struct ReceiverAccount(int? BankCode, string? BranchCode, string? AccountNumber)
    {
        public bool IsValid => BankCode.HasValue && !string.IsNullOrWhiteSpace(BranchCode) && !string.IsNullOrWhiteSpace(AccountNumber);
    }

    private static string EmployerContactMobile(string? value)
    {
        var normalized = Digits(value);
        return normalized.Length == 0 ? "0500000000" : normalized;
    }

    private static bool IsDigits(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsDigit);
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string UpperGuid(Guid value) => value.ToString("D").ToUpperInvariant();
    private static string BuildFileNumber(DateTimeOffset now, string senderId, int sequence)
    {
        var normalized = senderId.Length > 16 ? senderId[^16..] : senderId.PadLeft(16, '0');
        return $"{now:yyyyMMddHHmmss}{normalized}{sequence:0000}";
    }

    public sealed record BuildContext(Employer Employer, IReadOnlyList<ManualReportEmployee> Employees,
        IReadOnlyDictionary<Guid, Person> People, IReadOnlyDictionary<Guid, Employment> Employments,
        IReadOnlyList<ManualReportProduct> Products, IReadOnlyList<ManualContribution> Contributions,
        IReadOnlyList<ManualReportPayment> Payments, IReadOnlyList<EmployerInterfaceReportProductData> ProductMetadata,
        EmployerInterface006Options Options, int DepositorTypeCode = 1, int EmployerIdentifierTypeCode = 1,
        IReadOnlyList<ManualReportAttachment>? AttachmentItems = null, bool AnnualEmployerAffidavitSatisfied = false,
        DateTimeOffset? PreparedAt = null, IReadOnlyDictionary<Guid, string>? AttachmentFileNames = null,
        int FileSequence = 1)
    {
        public IReadOnlyList<ManualReportAttachment> Attachments { get; init; } = AttachmentItems ?? [];
        public IReadOnlyDictionary<Guid, string> AttachmentTransmissionNames { get; init; } = AttachmentFileNames ?? new Dictionary<Guid, string>();
    }
    public sealed record BuildResult(XDocument? Document, IReadOnlyList<string> Issues);
}
