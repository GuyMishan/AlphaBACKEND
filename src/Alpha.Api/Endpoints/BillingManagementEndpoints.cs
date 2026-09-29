using System.Data;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Billing;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record PricingTierRequest(decimal FromQuantity, decimal? ToQuantity, decimal UnitPrice);

public sealed record PricingComponentRequest(
    BillingMetricType MetricType,
    BillingPricingType PricingType,
    decimal UnitPrice,
    decimal IncludedQuantity,
    decimal? MinimumCharge,
    decimal? MaximumCharge,
    bool IsEnabled,
    CorrectionBillingMode? CorrectionMode = null,
    IReadOnlyList<PricingTierRequest>? Tiers = null);

public sealed record PlanBillingRequest(
    string Code,
    string Name,
    string? Description,
    string? Currency,
    string? BillingInterval,
    int MaxEmployers,
    int MaxEmployees,
    int MaxUsers,
    bool IsActive,
    CorrectionBillingMode CorrectionBillingMode,
    decimal? CorrectionUnitPrice,
    decimal IncludedCorrections,
    decimal IncludedCorrectionRows,
    DateTimeOffset? EffectiveFrom,
    IReadOnlyList<PricingComponentRequest> Components);

public sealed record PricingSimulationRequest(
    decimal Employers,
    decimal Employees,
    decimal ReportRows,
    decimal Corrections,
    decimal CorrectedRows);

public sealed record RunBillingPeriodRequest(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, bool Charge = true);
public sealed record RefundPaymentRequest(decimal Amount, string? Reason, string IdempotencyKey);
public sealed record BillingAccountPricingRequest(string BillingType, decimal? UnitPrice);
public sealed record BillingCustomerPricingRequest(string PayerType, Guid PayerId, string BillingType, decimal? UnitPrice);
public sealed record SelfServiceBillingPlanRequest(string BillingType);

public static class BillingManagementEndpoints
{
    public static IEndpointRouteBuilder MapBillingManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform/billing")
            .RequireAuthorization().WithTags("Platform Billing");

        platform.MapGet("/customers", GetBillingCustomersAsync);
        platform.MapPut("/customers/pricing", UpdateBillingCustomerPricingAsync);
        platform.MapGet("/summary", GetPlatformBillingSummaryAsync);
        platform.MapGet("/periods", GetPlatformPeriodsAsync);
        platform.MapGet("/payments", GetPlatformPaymentsAsync);
        platform.MapGet("/refunds", GetPlatformRefundsAsync);
        platform.MapGet("/usage", GetPlatformUsageAsync);
        platform.MapGet("/accounts/{billingAccountId:guid}/pricing", GetAccountPricingAsync);
        platform.MapPut("/accounts/{billingAccountId:guid}/pricing", UpdateAccountPricingAsync);
        platform.MapDelete("/accounts/{billingAccountId:guid}/pricing", ResetAccountPricingAsync);
        platform.MapPost("/accounts/{billingAccountId:guid}/run", RunPeriodAsync);
        platform.MapPost("/payments/{paymentId:guid}/refunds", RefundAsync);

        var organization = endpoints.MapGroup("/api/organizations/{organizationId:guid}/billing")
            .RequireAuthorization().WithTags("Alpha Billing");
        organization.MapGet("/context", GetOrganizationBillingContextAsync);
        organization.MapGet("/periods", GetOrganizationPeriodsAsync);
        organization.MapGet("/payments", GetOrganizationPaymentsAsync);
        organization.MapGet("/pricing", GetOrganizationSelfServicePricingAsync);
        organization.MapPut("/pricing", UpdateOrganizationSelfServicePricingAsync);

        var employer = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/billing")
            .RequireAuthorization().WithTags("Alpha Billing");
        employer.MapGet("/context", GetEmployerBillingContextAsync);
        employer.MapGet("/periods", GetEmployerPeriodsAsync);
        employer.MapGet("/payments", GetEmployerPaymentsAsync);
        employer.MapGet("/pricing", GetEmployerSelfServicePricingAsync);
        employer.MapPut("/pricing", UpdateEmployerSelfServicePricingAsync);

        return endpoints;
    }

    private static async Task<IResult> GetBillingCustomersAsync(
        IAlphaDbContext db, ICurrentUser currentUser, BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();

        var organizations = await db.Organizations.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(ct);

        var employers = await db.Employers.AsNoTracking()
            .OrderBy(x => x.LegalName)
            .Select(x => new { x.Id, x.OrganizationId, x.LegalName })
            .ToListAsync(ct);

        var accounts = await db.BillingAccounts.AsNoTracking().ToListAsync(ct);
        var pensionPaymentAccounts = await db.EmployerPaymentAccounts.AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new { x.Id, x.OrganizationId, x.EmployerId })
            .ToListAsync(ct);
        var employerPaymentModes = await db.EmployerProfileSettings.AsNoTracking()
            .Select(x => new { x.EmployerId, x.PensionPaymentMode })
            .ToDictionaryAsync(x => x.EmployerId, x => x.PensionPaymentMode, ct);
        var accountIds = accounts.Select(x => x.Id).ToArray();
        var pricing = await db.BillingAccountPricingComponents.AsNoTracking()
            .Where(x => accountIds.Contains(x.BillingAccountId) && x.EffectiveTo == null && x.IsEnabled)
            .ToListAsync(ct);

        static (string BillingType, decimal UnitPrice) Pricing(
            Guid? accountId, IReadOnlyCollection<BillingAccountPricingComponent> components)
        {
            if (!accountId.HasValue)
                return ("Free", 0m);

            var employee = components.FirstOrDefault(x =>
                x.BillingAccountId == accountId.Value && x.MetricType == BillingMetricType.Employee);
            var row = components.FirstOrDefault(x =>
                x.BillingAccountId == accountId.Value && x.MetricType == BillingMetricType.ReportRow);
            return employee is not null
                ? ("PerEmployee", employee.UnitPrice)
                : row is not null
                    ? ("PerReportRow", row.UnitPrice)
                    : ("Free", 0m);
        }

        var result = new List<object>();
        foreach (var organization in organizations)
        {
            var account = accounts.SingleOrDefault(x =>
                x.OrganizationId == organization.Id && x.EmployerId == null);
            var p = Pricing(account?.Id, pricing);
            result.Add(new
            {
                payerType = "Organization",
                entityType = "Organization",
                payerId = organization.Id,
                payerName = organization.Name,
                organizationId = organization.Id,
                organizationName = organization.Name,
                employerId = (Guid?)null,
                employerName = (string?)null,
                billingAccountId = account?.Id,
                billingSource = "Organization",
                billedThroughName = organization.Name,
                inherited = false,
                paymentMethodStatus = account?.PaymentMethodStatus ?? BillingPaymentMethodStatus.NotConfigured,
                paymentMethodType = account?.PaymentMethodType ?? BillingPaymentMethodType.CreditCard,
                cardBrand = account?.CardBrand ?? string.Empty,
                cardLast4 = account?.CardLast4 ?? string.Empty,
                configured = account?.PaymentMethodStatus == BillingPaymentMethodStatus.Active,
                pensionPaymentConfigured = pensionPaymentAccounts.Any(x =>
                    x.OrganizationId == organization.Id && x.EmployerId == null),
                pensionPaymentSource = "Organization",
                pensionPaymentThroughName = organization.Name,
                billingType = p.BillingType,
                unitPrice = p.UnitPrice
            });
        }

        foreach (var employer in employers)
        {
            var resolution = await inheritance.ResolveBillingAccountAsync(employer.Id, ct);
            var effectiveAccount = resolution?.Account;
            var p = Pricing(effectiveAccount?.Id, pricing);
            var organizationName = organizations.FirstOrDefault(x => x.Id == employer.OrganizationId)?.Name ?? string.Empty;
            var inherited = string.Equals(resolution?.Source, "Organization", StringComparison.Ordinal);
            var directPensionPaymentAccount = pensionPaymentAccounts.FirstOrDefault(x =>
                x.OrganizationId == employer.OrganizationId && x.EmployerId == employer.Id);
            var organizationPensionPaymentAccount = pensionPaymentAccounts.FirstOrDefault(x =>
                x.OrganizationId == employer.OrganizationId && x.EmployerId == null);
            var pensionPaymentMode = employerPaymentModes.GetValueOrDefault(employer.Id,
                directPensionPaymentAccount is not null
                    ? EmployerPensionPaymentMode.EmployerDirect
                    : EmployerPensionPaymentMode.InheritOrganization);
            var pensionPaymentInherited = pensionPaymentMode == EmployerPensionPaymentMode.InheritOrganization;
            var effectivePensionPaymentAccount = pensionPaymentInherited
                ? organizationPensionPaymentAccount
                : directPensionPaymentAccount;
            result.Add(new
            {
                payerType = "Employer",
                entityType = "Employer",
                payerId = employer.Id,
                payerName = employer.LegalName,
                organizationId = employer.OrganizationId,
                organizationName,
                employerId = (Guid?)employer.Id,
                employerName = employer.LegalName,
                billingAccountId = effectiveAccount?.Id,
                billingSource = resolution?.Source ?? "Employer",
                billedThroughName = resolution?.BilledThroughName ?? employer.LegalName,
                inherited,
                paymentMethodStatus = effectiveAccount?.PaymentMethodStatus ?? BillingPaymentMethodStatus.NotConfigured,
                paymentMethodType = effectiveAccount?.PaymentMethodType ?? BillingPaymentMethodType.CreditCard,
                cardBrand = effectiveAccount?.CardBrand ?? string.Empty,
                cardLast4 = effectiveAccount?.CardLast4 ?? string.Empty,
                configured = effectiveAccount?.PaymentMethodStatus == BillingPaymentMethodStatus.Active,
                pensionPaymentConfigured = effectivePensionPaymentAccount is not null,
                pensionPaymentSource = pensionPaymentInherited ? "Organization" : "Employer",
                pensionPaymentThroughName = pensionPaymentInherited ? organizationName : employer.LegalName,
                billingType = p.BillingType,
                unitPrice = p.UnitPrice
            });
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> UpdateBillingCustomerPricingAsync(
        BillingCustomerPricingRequest request, IAlphaDbContext db,
        ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();

        var payerType = (request.PayerType ?? string.Empty).Trim();
        BillingAccount? account;
        if (payerType == "Organization")
        {
            if (!await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == request.PayerId, ct))
                return Results.NotFound();
            account = await db.BillingAccounts.SingleOrDefaultAsync(
                x => x.OrganizationId == request.PayerId && x.EmployerId == null, ct);
            if (account is null)
            {
                account = new BillingAccount(request.PayerId, null);
                db.BillingAccounts.Add(account);
                await db.SaveChangesAsync(ct);
            }
        }
        else if (payerType == "Employer")
        {
            var employer = await db.Employers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == request.PayerId, ct);
            if (employer is null) return Results.NotFound();

            var settings = await db.EmployerProfileSettings
                .SingleOrDefaultAsync(x => x.EmployerId == request.PayerId, ct);
            if (settings is null)
            {
                settings = new Alpha.Domain.Employers.EmployerProfileSettings(request.PayerId);
                db.EmployerProfileSettings.Add(settings);
            }
            settings.UpdateBilling(Alpha.Domain.Employers.EmployerBillingMode.EmployerDirect);

            account = await db.BillingAccounts.SingleOrDefaultAsync(
                x => x.EmployerId == request.PayerId && x.OrganizationId == null, ct);
            if (account is null)
            {
                account = new BillingAccount(null, request.PayerId);
                db.BillingAccounts.Add(account);
            }
            await db.SaveChangesAsync(ct);
        }
        else
        {
            return Results.BadRequest(new { error = "invalid_payer_type" });
        }

        return await UpdateAccountPricingAsync(
            account.Id,
            new BillingAccountPricingRequest(request.BillingType, request.UnitPrice),
            db,
            currentUser,
            ct);
    }

    private static async Task<IResult> GetPlatformBillingSummaryAsync(
        IAlphaDbContext db, ICurrentUser currentUser, int take = 500, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        take = Math.Clamp(take, 1, 1000);

        var periods = await db.BillingPeriods.AsNoTracking()
            .OrderByDescending(x => x.PeriodEnd)
            .Take(take)
            .ToListAsync(ct);

        if (periods.Count == 0) return Results.Ok(Array.Empty<object>());

        var accountIds = periods.Select(x => x.BillingAccountId).Distinct().ToArray();
        var accounts = await db.BillingAccounts.AsNoTracking()
            .Where(x => accountIds.Contains(x.Id))
            .ToListAsync(ct);

        var organizationIds = accounts.Where(x => x.OrganizationId.HasValue)
            .Select(x => x.OrganizationId!.Value)
            .Distinct()
            .ToArray();
        var employerIds = accounts.Where(x => x.EmployerId.HasValue)
            .Select(x => x.EmployerId!.Value)
            .Distinct()
            .ToArray();

        var organizations = await db.Organizations.AsNoTracking()
            .Where(x => organizationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var employers = await db.Employers.AsNoTracking()
            .Where(x => employerIds.Contains(x.Id))
            .Select(x => new { x.Id, x.OrganizationId, x.LegalName })
            .ToListAsync(ct);

        var employerOrganizationIds = employers.Select(x => x.OrganizationId).Distinct().ToArray();
        var employerOrganizations = await db.Organizations.AsNoTracking()
            .Where(x => employerOrganizationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var periodIds = periods.Select(x => x.Id).ToArray();
        var payments = await db.Payments.AsNoTracking()
            .Where(x => periodIds.Contains(x.BillingPeriodId))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var currentAccountPricing = await db.BillingAccountPricingComponents.AsNoTracking()
            .Where(x => accountIds.Contains(x.BillingAccountId) && x.EffectiveTo == null)
            .OrderBy(x => x.MetricType)
            .ToListAsync(ct);

        var rows = periods.Select(period =>
        {
            var account = accounts.Single(x => x.Id == period.BillingAccountId);
            var employer = account.EmployerId.HasValue
                ? employers.SingleOrDefault(x => x.Id == account.EmployerId.Value)
                : null;

            var organizationId = account.OrganizationId ?? employer?.OrganizationId;
            var organizationName = account.OrganizationId.HasValue
                ? organizations.GetValueOrDefault(account.OrganizationId.Value, account.BillingName)
                : employer is not null
                    ? employerOrganizations.GetValueOrDefault(employer.OrganizationId, string.Empty)
                    : string.Empty;

            var periodPayments = payments.Where(x => x.BillingPeriodId == period.Id).ToArray();
            var successfulPayment = periodPayments.FirstOrDefault(x =>
                x.Status is BillingPaymentStatus.Succeeded or BillingPaymentStatus.Refunded or BillingPaymentStatus.PartiallyRefunded);
            var latestPayment = successfulPayment ?? periodPayments.FirstOrDefault();
            var paid = successfulPayment is not null;

            return (object)new
            {
                period.Id,
                period.BillingAccountId,
                payerType = account.OrganizationId.HasValue ? "Organization" : "Employer",
                payerName = account.OrganizationId.HasValue
                    ? organizationName
                    : employer?.LegalName ?? account.BillingName,
                organizationId,
                organizationName,
                employerId = account.EmployerId,
                employerName = employer?.LegalName,
                month = period.PeriodStart.ToString("yyyy-MM"),
                period.PeriodStart,
                period.PeriodEnd,
                period.Status,
                accountStatus = account.Status,
                paymentMethodStatus = account.PaymentMethodStatus,
                account.PaymentMethodType,
                account.CardBrand,
                account.CardLast4,
                period.Currency,
                amount = period.Total,
                paid,
                paymentId = latestPayment?.Id,
                paymentStatus = latestPayment?.Status,
                provider = latestPayment?.Provider ?? string.Empty,
                providerTransactionId = latestPayment?.ProviderTransactionId ?? string.Empty,
                failureCode = latestPayment?.FailureCode ?? string.Empty,
                failureMessage = latestPayment?.FailureMessage ?? string.Empty,
                paidAt = successfulPayment?.PaidAt,
                calculatedAt = period.CalculatedAt,
                chargedAt = period.ChargedAt,
                billingType = currentAccountPricing.Any(x => x.BillingAccountId == account.Id && x.IsEnabled && x.MetricType == BillingMetricType.Employee)
                    ? "PerEmployee"
                    : currentAccountPricing.Any(x => x.BillingAccountId == account.Id && x.IsEnabled && x.MetricType == BillingMetricType.ReportRow)
                        ? "PerReportRow"
                        : "Free",
                unitPrice = currentAccountPricing
                    .Where(x => x.BillingAccountId == account.Id && x.IsEnabled &&
                                (x.MetricType == BillingMetricType.Employee || x.MetricType == BillingMetricType.ReportRow))
                    .Select(x => (decimal?)x.UnitPrice)
                    .FirstOrDefault() ?? 0m
            };
        }).ToArray();

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAccountPricingAsync(
        Guid billingAccountId, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (!await db.BillingAccounts.AsNoTracking().AnyAsync(x => x.Id == billingAccountId, ct))
            return Results.NotFound();

        var components = await db.BillingAccountPricingComponents.AsNoTracking()
            .Where(x => x.BillingAccountId == billingAccountId && x.EffectiveTo == null && x.IsEnabled)
            .ToListAsync(ct);

        var employee = components.FirstOrDefault(x => x.MetricType == BillingMetricType.Employee);
        var row = components.FirstOrDefault(x => x.MetricType == BillingMetricType.ReportRow);
        var billingType = employee is not null ? "PerEmployee"
            : row is not null ? "PerReportRow"
            : "Free";
        var unitPrice = employee?.UnitPrice ?? row?.UnitPrice ?? 0m;

        return Results.Ok(new { billingAccountId, billingType, unitPrice });
    }

    private static async Task<IResult> UpdateAccountPricingAsync(
        Guid billingAccountId, BillingAccountPricingRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (!await db.BillingAccounts.AsNoTracking().AnyAsync(x => x.Id == billingAccountId, ct))
            return Results.NotFound();

        var billingType = (request.BillingType ?? string.Empty).Trim();
        if (billingType is not ("Free" or "PerEmployee" or "PerReportRow"))
            return Results.BadRequest(new { error = "invalid_billing_type" });

        var unitPrice = request.UnitPrice ?? 0m;
        if (unitPrice < 0 || (billingType != "Free" && unitPrice <= 0))
            return Results.BadRequest(new { error = "invalid_unit_price" });

        var effectiveFrom = DateTimeOffset.UtcNow;
        var current = await db.BillingAccountPricingComponents
            .Where(x => x.BillingAccountId == billingAccountId && x.EffectiveTo == null)
            .ToListAsync(ct);
        var nextVersion = current.Count == 0 ? 1 : current.Max(x => x.Version) + 1;

        foreach (var item in current)
            item.Close(effectiveFrom);

        if (billingType != "Free")
        {
            var metric = billingType == "PerEmployee"
                ? BillingMetricType.Employee
                : BillingMetricType.ReportRow;
            db.BillingAccountPricingComponents.Add(new BillingAccountPricingComponent(
                billingAccountId, metric, BillingPricingType.PerUnit, unitPrice,
                includedQuantity: 0, minimumCharge: null, maximumCharge: null,
                isEnabled: true, version: nextVersion, effectiveFrom: effectiveFrom));
        }

        await db.SaveChangesAsync(ct);
        return await GetAccountPricingAsync(billingAccountId, db, currentUser, ct);
    }

    private static async Task<IResult> ResetAccountPricingAsync(
        Guid billingAccountId, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        var current = await db.BillingAccountPricingComponents
            .Where(x => x.BillingAccountId == billingAccountId && x.EffectiveTo == null)
            .ToListAsync(ct);
        var effectiveTo = DateTimeOffset.UtcNow;
        foreach (var item in current)
            item.Close(effectiveTo);
        if (current.Count > 0) await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPlatformPeriodsAsync(
        IAlphaDbContext db, ICurrentUser currentUser, int take = 100, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        take = Math.Clamp(take, 1, 500);
        var rows = await db.BillingPeriods.AsNoTracking()
            .OrderByDescending(x => x.PeriodEnd)
            .Take(take)
            .Select(x => new
            {
                x.Id, x.BillingAccountId, x.PeriodStart, x.PeriodEnd,
                x.Status, x.Currency, x.Subtotal, x.Total, x.CalculatedAt, x.ChargedAt
            }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetPlatformPaymentsAsync(
        IAlphaDbContext db, ICurrentUser currentUser, int take = 100, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        take = Math.Clamp(take, 1, 500);
        var rows = await db.Payments.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new
            {
                x.Id, x.BillingAccountId, x.BillingPeriodId, x.Amount, x.Currency,
                x.Status, x.Provider, x.ProviderTransactionId, x.InvoiceReference,
                x.FailureCode, x.FailureMessage, x.PaidAt, x.CreatedAt
            }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetPlatformRefundsAsync(
        IAlphaDbContext db, ICurrentUser currentUser, int take = 100, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        take = Math.Clamp(take, 1, 500);
        var rows = await db.Refunds.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new
            {
                x.Id, x.PaymentId, x.Amount, x.Reason, x.Status,
                x.ProviderRefundId, x.ErrorMessage, x.IdempotencyKey, x.CreatedAt
            }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetPlatformUsageAsync(
        Guid billingPeriodId, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        var exists = await db.BillingPeriods.AsNoTracking().AnyAsync(x => x.Id == billingPeriodId, ct);
        if (!exists) return Results.NotFound();
        var rows = await db.BillingUsages.AsNoTracking()
            .Where(x => x.BillingPeriodId == billingPeriodId)
            .OrderBy(x => x.MetricType)
            .Select(x => new
            {
                x.Id, x.EmployerId, x.MetricType, x.Quantity, x.IncludedQuantity,
                x.BillableQuantity, x.UnitPrice, x.Amount, x.SourceType, x.SourceId
            }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> RunPeriodAsync(
        Guid billingAccountId, RunBillingPeriodRequest request, ICurrentUser currentUser,
        IBillingCycleService billing, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (request.PeriodEnd <= request.PeriodStart)
            return Results.BadRequest(new { error = "invalid_period" });

        try
        {
            return Results.Ok(await billing.RunPeriodAsync(
                billingAccountId, request.PeriodStart, request.PeriodEnd, request.Charge, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "billing_operation_already_in_progress" });
        }
    }

    private static async Task<IResult> RefundAsync(
        Guid paymentId, RefundPaymentRequest request, AlphaDbContext db, ICurrentUser currentUser,
        IPaymentProviderResolver providers, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Results.BadRequest(new { error = "amount_and_idempotency_key_required" });

        Refund refund;
        Payment payment;

        await using (var reservation = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var existing = await db.Refunds.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                await reservation.RollbackAsync(ct);
                return Results.Ok(new
                {
                    existing.Id, existing.PaymentId, existing.Amount, existing.Status,
                    existing.ProviderRefundId, existing.ErrorMessage
                });
            }

            payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == paymentId, ct)
                ?? throw new InvalidOperationException("payment_not_found");
            if (payment.Status is not BillingPaymentStatus.Succeeded and not BillingPaymentStatus.PartiallyRefunded)
            {
                await reservation.RollbackAsync(ct);
                return Results.Conflict(new { error = "payment_not_refundable" });
            }

            var reservedOrRefunded = await db.Refunds.AsNoTracking()
                .Where(x => x.PaymentId == paymentId &&
                            (x.Status == BillingRefundStatus.Pending || x.Status == BillingRefundStatus.Succeeded))
                .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
            if (reservedOrRefunded + request.Amount > payment.Amount)
            {
                await reservation.RollbackAsync(ct);
                return Results.BadRequest(new
                {
                    error = "refund_exceeds_remaining_amount",
                    remaining = payment.Amount - reservedOrRefunded
                });
            }

            refund = new Refund(payment.Id, request.Amount, request.Reason ?? string.Empty, request.IdempotencyKey.Trim());
            db.Refunds.Add(refund);
            await db.SaveChangesAsync(ct);
            await reservation.CommitAsync(ct);
        }

        var account = await db.BillingAccounts.SingleAsync(x => x.Id == payment.BillingAccountId, ct);
        var provider = providers.Resolve(payment.Provider);

        PaymentRefundResult result;
        try
        {
            result = await provider.Refund(new PaymentRefundRequest(
                payment.ProviderTransactionId,
                account.ProviderPaymentMethodId,
                request.Amount,
                payment.Currency,
                refund.IdempotencyKey,
                account.CardExpiryMonth,
                account.CardExpiryYear), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new PaymentRefundResult(false, string.Empty, "provider_exception", ex.Message);
        }

        refund.Complete(result.Success, result.RefundId, result.ErrorMessage);
        if (result.Success)
        {
            var previouslySucceeded = await db.Refunds.AsNoTracking()
                .Where(x => x.PaymentId == paymentId &&
                            x.Id != refund.Id &&
                            x.Status == BillingRefundStatus.Succeeded)
                .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
            payment.MarkRefunded(previouslySucceeded + request.Amount < payment.Amount);
        }

        await db.SaveChangesAsync(ct);
        return result.Success
            ? Results.Ok(new { refund.Id, refund.PaymentId, refund.Amount, refund.Status, refund.ProviderRefundId })
            : Results.Json(new { refund.Id, refund.Status, result.ErrorCode, result.ErrorMessage },
                statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> GetOrganizationSelfServicePricingAsync(
        Guid organizationId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return Results.Ok(await SelfServicePricingResponse(db, account, ct));
    }

    private static async Task<IResult> GetEmployerSelfServicePricingAsync(
        Guid organizationId, Guid employerId, IAlphaDbContext db, OrganizationAccessService access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();
        return Results.Ok(await SelfServicePricingResponse(db, resolution.Account, ct));
    }

    private static async Task<object> SelfServicePricingResponse(IAlphaDbContext db, BillingAccount? account, CancellationToken ct)
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

    private static Task<(decimal Employee, decimal Row)> GetDefaultSelfServiceTariffs(IAlphaDbContext db, CancellationToken ct) =>
        Task.FromResult((10m, 2m));

    private static async Task<IResult> UpdateOrganizationSelfServicePricingAsync(
        Guid organizationId, SelfServiceBillingPlanRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await ApplySelfServicePricing(organizationId, null, account, request, db, ct);
    }

    private static async Task<IResult> UpdateEmployerSelfServicePricingAsync(
        Guid organizationId, Guid employerId, SelfServiceBillingPlanRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(
            x => x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await ApplySelfServicePricing(organizationId, employerId, account, request, db, ct);
    }

    private static async Task<IResult> ApplySelfServicePricing(
        Guid organizationId, Guid? employerId, BillingAccount? account, SelfServiceBillingPlanRequest request,
        IAlphaDbContext db, CancellationToken ct)
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
        return Results.Ok(new { billingType, unitPrice, accountValid = billingType == "Free" || true });
    }

    private static async Task<IResult> GetOrganizationBillingContextAsync(
        Guid organizationId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
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
        Guid organizationId, Guid employerId, IAlphaDbContext db, OrganizationAccessService access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();

        var canManage = resolution.Source == "Organization"
            ? await access.CanManageOrganizationAsync(organizationId, ct)
            : await access.CanManageEmployerAsync(organizationId, employerId, ct);

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
        Guid organizationId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
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
        Guid organizationId, Guid employerId, IAlphaDbContext db, OrganizationAccessService access,
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
        Guid organizationId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
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
        Guid organizationId, Guid employerId, IAlphaDbContext db, OrganizationAccessService access,
        BillingInheritanceService inheritance, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.OrganizationId != organizationId) return Results.NotFound();
        return Results.Ok(resolution.Account is null
            ? []
            : await PaymentRows(db, resolution.Account.Id, ct));
    }

    private static Task<List<object>> PaymentRows(IAlphaDbContext db, Guid accountId, CancellationToken ct) =>
        db.Payments.AsNoTracking()
            .Where(x => x.BillingAccountId == accountId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (object)new
            {
                x.Id, x.BillingAccountId, x.BillingPeriodId, x.Amount, x.Currency,
                x.Status, x.Provider, x.ProviderTransactionId, x.InvoiceReference,
                x.FailureCode, x.FailureMessage, x.PaidAt, x.CreatedAt
            }).ToListAsync(ct);

    private static Task<List<object>> PeriodRows(IAlphaDbContext db, Guid accountId, CancellationToken ct) =>
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
