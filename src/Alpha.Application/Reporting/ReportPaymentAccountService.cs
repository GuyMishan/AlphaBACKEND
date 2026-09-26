using Alpha.Application.Abstractions;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Reporting;

public sealed class ReportPaymentAccountService(IAlphaDbContext db)
{
    public async Task<EmployerPaymentAccount?> ResolveForReportAsync(Guid employerId, Guid? requestedAccountId, CancellationToken ct)
    {
        var employer = await db.Employers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == employerId, ct);
        if (employer is null) return null;

        var settings = await db.EmployerProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employerId, ct);
        var directAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == employer.OrganizationId && x.EmployerId == employerId && x.IsActive, ct);
        var mode = settings?.PensionPaymentMode
            ?? (directAccount is not null ? EmployerPensionPaymentMode.EmployerDirect : EmployerPensionPaymentMode.InheritOrganization);

        var effective = mode == EmployerPensionPaymentMode.EmployerDirect
            ? directAccount
            : await db.EmployerPaymentAccounts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == employer.OrganizationId && x.EmployerId == null && x.IsActive, ct);

        if (!requestedAccountId.HasValue) return effective;
        return effective?.Id == requestedAccountId.Value ? effective : null;
    }

    public async Task ApplySnapshotAsync(ManualReport report, EmployerPaymentAccount account, CancellationToken ct)
    {
        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);

        report.SetPaymentAccountSnapshot(
            account.Id,
            account.BankId,
            account.BranchId,
            Mask(account.AccountNumber),
            mandate is { IsActive: true } ? (mandate.ExternalMandateId ?? mandate.Id.ToString()) : string.Empty);
    }

    public async Task<(bool IsValid, string? Error)> ValidateForTransmissionAsync(ManualReport report, CancellationToken ct)
    {
        if (!report.PaymentAccountId.HasValue)
            return (false, "payment_account_required");

        var snapshotAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.Id == report.PaymentAccountId.Value &&
                x.OrganizationId == report.OrganizationId &&
                x.IsActive &&
                (x.EmployerId == report.EmployerId || x.EmployerId == null), ct);
        if (snapshotAccount is null)
            return (false, "payment_account_required");

        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == snapshotAccount.Id, ct);
        if (mandate is null || !mandate.IsActive)
            return (false, "bank_mandate_required");

        return (true, null);
    }

    private static string Mask(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var visible = value.Length <= 4 ? value : value[^4..];
        return $"••••{visible}";
    }
}
