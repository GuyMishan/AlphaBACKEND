using System.Data;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Billing.Contracts;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Modules.Billing.Application;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Billing.Features.Management;

public static partial class BillingManagementEndpoints
{
    private static async Task<IResult> GetOrganizationSelfServicePricingAsync(
        Guid organizationId, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return Results.Ok(await SelfServicePricingResponse(db, account, ct));
    }

    private static async Task<IResult> GetEmployerSelfServicePricingAsync(
        Guid organizationId, Guid employerId, AlphaDbContext db, IOrganizationAuthorization access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();
        return Results.Ok(await SelfServicePricingResponse(db, resolution.Account, ct));
    }

    private static async Task<object> SelfServicePricingResponse(AlphaDbContext db, BillingAccount? account, CancellationToken ct)
    {
        if (account is null) return new { billingType = "Free", unitPrice = 0m, employeeUnitPrice = 10m, rowUnitPrice = 2m, accountValid = false };
        var component = await db.BillingAccountPricingComponents.AsNoTracking()
            .Where(x => x.BillingAccountId == account.Id && x.EffectiveTo == null && x.IsEnabled &&
                        (x.MetricType == BillingMetricType.Employee || x.MetricType == BillingMetricType.ReportRow))
            .OrderBy(x => x.MetricType).FirstOrDefaultAsync(ct);
        var type = component?.MetricType == BillingMetricType.Employee ? "PerEmployee"
            : component?.MetricType == BillingMetricType.ReportRow ? "PerReportRow" : "Free";
        var valid = account.PaymentMethodStatus == BillingPaymentMethodStatus.Active &&
                    (account.PaymentMethodType == BillingPaymentMethodType.CreditCard
                        ? !string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId)
                        : !string.IsNullOrWhiteSpace(account.BankDebitMandateReference));
        var defaults = await GetDefaultSelfServiceTariffs(db, ct);
        return new { billingType = type, unitPrice = component?.UnitPrice ?? 0m, employeeUnitPrice = defaults.Employee, rowUnitPrice = defaults.Row, accountValid = valid };
    }

    private static Task<(decimal Employee, decimal Row)> GetDefaultSelfServiceTariffs(AlphaDbContext db, CancellationToken ct) =>
        Task.FromResult((10m, 2m));

    private static async Task<IResult> UpdateOrganizationSelfServicePricingAsync(
        Guid organizationId, SelfServiceBillingPlanRequest request, AlphaDbContext db,
        IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await ApplySelfServicePricing(organizationId, null, account, request, db, ct);
    }

    private static async Task<IResult> UpdateEmployerSelfServicePricingAsync(
        Guid organizationId, Guid employerId, SelfServiceBillingPlanRequest request, AlphaDbContext db,
        IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(
            x => x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await ApplySelfServicePricing(organizationId, employerId, account, request, db, ct);
    }

    private static async Task<IResult> ApplySelfServicePricing(
        Guid organizationId, Guid? employerId, BillingAccount? account, SelfServiceBillingPlanRequest request,
        AlphaDbContext db, CancellationToken ct)
    {
        var billingType = (request.BillingType ?? string.Empty).Trim();
        if (billingType is not ("Free" or "PerEmployee" or "PerReportRow"))
            return Results.BadRequest(new { error = "invalid_billing_type" });

        if (billingType == "Free")
        {
            var employers = await db.Employers.AsNoTracking()
                .CountAsync(x => x.OrganizationId == organizationId && x.Status != EmployerStatus.Closed, ct);
            var employees = await db.Employments.AsNoTracking()
                .CountAsync(x => x.OrganizationId == organizationId && x.Status == EmploymentStatus.Active, ct);
            var orgUsers = db.OrganizationMemberships.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.IsActive &&
                            (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow)).Select(x => x.UserId);
            var employerUsers = db.EmployerUserAccesses.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId).Select(x => x.UserId);
            var users = await orgUsers.Union(employerUsers).CountAsync(ct);
            if (employers > 1 || users > 1 || employees > 3)
                return Results.Conflict(new { error = "free_plan_limits_exceeded", employers, users, activeEmployees = employees, contactSupport = true });
        }
        else
        {
            if (account is null) return Results.Conflict(new { error = "billing_account_required" });
            var validPayment = account.PaymentMethodStatus == BillingPaymentMethodStatus.Active &&
                (account.PaymentMethodType == BillingPaymentMethodType.CreditCard
                    ? !string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId)
                    : !string.IsNullOrWhiteSpace(account.BankDebitMandateReference));
            if (!validPayment) return Results.Conflict(new { error = "billing_payment_method_invalid" });
        }

        if (account is null)
        {
            // Free is represented by the absence of an active pricing component.
            return Results.Ok(new { billingType = "Free", unitPrice = 0m, accountValid = false });
        }

        var current = await db.BillingAccountPricingComponents
            .Where(x => x.BillingAccountId == account.Id && x.EffectiveTo == null).ToListAsync(ct);
        var existing = current.FirstOrDefault(x => x.IsEnabled &&
            (x.MetricType == BillingMetricType.Employee || x.MetricType == BillingMetricType.ReportRow));
        var targetMetric = billingType == "PerEmployee" ? BillingMetricType.Employee : BillingMetricType.ReportRow;

        decimal unitPrice = 0m;
        if (billingType != "Free")
        {
            if (existing is not null && existing.MetricType == targetMetric) unitPrice = existing.UnitPrice;
            else
            {
                var configuredPrice = await db.BillingAccountPricingComponents.AsNoTracking()
                    .Where(x => x.BillingAccountId == account.Id && x.MetricType == targetMetric && x.UnitPrice > 0)
                    .OrderByDescending(x => x.EffectiveFrom)
                    .Select(x => (decimal?)x.UnitPrice)
                    .FirstOrDefaultAsync(ct);
                if (configuredPrice.HasValue) unitPrice = configuredPrice.Value;
                else
                {
                    var defaults = await GetDefaultSelfServiceTariffs(db, ct);
                    unitPrice = targetMetric == BillingMetricType.Employee ? defaults.Employee : defaults.Row;
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        var version = current.Count == 0 ? 1 : current.Max(x => x.Version) + 1;
        foreach (var item in current) item.Close(now);
        if (billingType != "Free")
            db.BillingAccountPricingComponents.Add(new BillingAccountPricingComponent(
                account.Id, targetMetric, BillingPricingType.PerUnit, unitPrice, 0, null, null, true, version, now));
        await db.SaveChangesAsync(ct);
        var accountValid = billingType == "Free" ||
            (account.PaymentMethodStatus == BillingPaymentMethodStatus.Active &&
             (account.PaymentMethodType == BillingPaymentMethodType.CreditCard
                 ? !string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId)
                 : !string.IsNullOrWhiteSpace(account.BankDebitMandateReference)));
        return Results.Ok(new { billingType, unitPrice, accountValid });
    }

    private static async Task<IResult> GetOrganizationBillingContextAsync(
        Guid organizationId, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (!await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, ct))
            return Results.NotFound();

        var account = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        var canManage = await access.CanManageOrganizationAsync(organizationId, ct);

        return Results.Ok(new
        {
            organizationId,
            employerId = (Guid?)null,
            source = "Organization",
            billedThroughName = account?.BillingName ?? string.Empty,
            canManageBilling = canManage,
            account = ToSafeBillingAccount(account)
        });
    }

    private static async Task<IResult> GetEmployerBillingContextAsync(
        Guid organizationId, Guid employerId, AlphaDbContext db, IOrganizationAuthorization access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();

        var canManage = resolution.Source == "Organization"
            ? await access.CanManageOrganizationAsync(organizationId, ct)
            : await access.CanManageEmployerBillingAsync(organizationId, employerId, ct);

        return Results.Ok(new
        {
            organizationId,
            employerId,
            resolution.Source,
            resolution.BilledThroughName,
            canManageBilling = canManage,
            account = ToSafeBillingAccount(resolution.Account)
        });
    }

    private static object ToSafeBillingAccount(BillingAccount? account) => new
    {
        id = account?.Id,
        billingMode = account?.BillingMode,
        status = account?.Status ?? BillingAccountStatus.PendingSetup,
        paymentMethodType = account?.PaymentMethodType ?? BillingPaymentMethodType.CreditCard,
        paymentMethodStatus = account?.PaymentMethodStatus ?? BillingPaymentMethodStatus.NotConfigured,
        cardBrand = account?.CardBrand ?? string.Empty,
        cardLast4 = account?.CardLast4 ?? string.Empty,
        cardExpiryMonth = account?.CardExpiryMonth,
        cardExpiryYear = account?.CardExpiryYear,
        hasBankDebitMandate = !string.IsNullOrWhiteSpace(account?.BankDebitMandateReference),
        configured = account is not null
    };

    private static async Task<IResult> GetOrganizationPeriodsAsync(
        Guid organizationId, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var accountId = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == null)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        return Results.Ok(accountId.HasValue
            ? await PeriodRows(db, accountId.Value, ct)
            : []);
    }

    private static async Task<IResult> GetEmployerPeriodsAsync(
        Guid organizationId, Guid employerId, AlphaDbContext db, IOrganizationAuthorization access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();
        return Results.Ok(resolution.Account is null
            ? []
            : await PeriodRows(db, resolution.Account.Id, ct));
    }

    private static async Task<IResult> GetOrganizationPaymentsAsync(
        Guid organizationId, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var accountId = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == null)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        return Results.Ok(accountId.HasValue
            ? await PaymentRows(db, accountId.Value, ct)
            : []);
    }

    private static async Task<IResult> GetEmployerPaymentsAsync(
        Guid organizationId, Guid employerId, AlphaDbContext db, IOrganizationAuthorization access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();
        return Results.Ok(resolution.Account is null
            ? []
            : await PaymentRows(db, resolution.Account.Id, ct));
    }

    private static Task<List<object>> PaymentRows(AlphaDbContext db, Guid accountId, CancellationToken ct) =>
        db.Payments.AsNoTracking()
            .Where(x => x.BillingAccountId == accountId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (object)new
            {
                x.Id, x.BillingAccountId, x.BillingPeriodId, x.Amount, x.Currency,
                x.Status, x.Provider, x.ProviderTransactionId, x.InvoiceReference,
                x.FailureCode, x.FailureMessage, x.PaidAt, x.CreatedAt
            }).ToListAsync(ct);

    private static Task<List<object>> PeriodRows(AlphaDbContext db, Guid accountId, CancellationToken ct) =>
        db.BillingPeriods.AsNoTracking()
            .Where(x => x.BillingAccountId == accountId)
            .OrderByDescending(x => x.PeriodEnd)
            .Select(x => (object)new
            {
                x.Id, x.BillingAccountId, x.PeriodStart, x.PeriodEnd,
                x.Status, x.Currency, x.Subtotal, x.Total, x.CalculationSnapshotJson,
                x.CalculatedAt, x.ChargedAt
            }).ToListAsync(ct);
}
