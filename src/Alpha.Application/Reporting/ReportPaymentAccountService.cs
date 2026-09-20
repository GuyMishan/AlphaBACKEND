using Alpha.Application.Abstractions;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Reporting;

public sealed class ReportPaymentAccountService(IAlphaDbContext db)
{
    public async Task<EmployerPaymentAccount?> ResolveForReportAsync(Guid employerId, Guid? requestedAccountId, CancellationToken ct)
    {
        if (requestedAccountId.HasValue)
            return await db.EmployerPaymentAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == requestedAccountId.Value && x.EmployerId == employerId && x.IsActive, ct);

        return await db.EmployerPaymentAccounts.AsNoTracking()
            .Where(x => x.EmployerId == employerId && x.IsActive)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
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
            mandate?.ExternalMandateId ?? mandate?.Id.ToString() ?? string.Empty);
    }

    public async Task<(bool IsValid, string? Error)> ValidateForTransmissionAsync(ManualReport report, CancellationToken ct)
    {
        if (!report.PaymentAccountId.HasValue)
            return (false, "payment_account_required");

        var account = await db.EmployerPaymentAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == report.PaymentAccountId.Value &&
            x.EmployerId == report.EmployerId &&
            x.OrganizationId == report.OrganizationId &&
            x.IsActive, ct);
        if (account is null)
            return (false, "payment_account_required");

        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);
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
