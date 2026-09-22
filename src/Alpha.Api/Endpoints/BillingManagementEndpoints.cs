using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Billing;
using Alpha.Domain.Billing;
using Alpha.Domain.Subscriptions;
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

public static class BillingManagementEndpoints
{
    public static IEndpointRouteBuilder MapBillingManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform/billing")
            .RequireAuthorization().WithTags("Platform Billing");

        platform.MapGet("/plans", GetPlansAsync);
        platform.MapPost("/plans", CreatePlanAsync);
        platform.MapPut("/plans/{planId:guid}", UpdatePlanAsync);
        platform.MapPost("/plans/{planId:guid}/simulate", SimulateAsync);
        platform.MapGet("/periods", GetPlatformPeriodsAsync);
        platform.MapGet("/payments", GetPlatformPaymentsAsync);
        platform.MapGet("/refunds", GetPlatformRefundsAsync);
        platform.MapGet("/usage", GetPlatformUsageAsync);
        platform.MapPost("/accounts/{billingAccountId:guid}/run", RunPeriodAsync);
        platform.MapPost("/payments/{paymentId:guid}/refunds", RefundAsync);

        var organization = endpoints.MapGroup("/api/organizations/{organizationId:guid}/billing")
            .RequireAuthorization().WithTags("Alpha Billing");
        organization.MapGet("/context", GetOrganizationBillingContextAsync);
        organization.MapGet("/periods", GetOrganizationPeriodsAsync);
        organization.MapGet("/payments", GetOrganizationPaymentsAsync);

        var employer = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/billing")
            .RequireAuthorization().WithTags("Alpha Billing");
        employer.MapGet("/context", GetEmployerBillingContextAsync);
        employer.MapGet("/periods", GetEmployerPeriodsAsync);
        employer.MapGet("/payments", GetEmployerPaymentsAsync);

        return endpoints;
    }

    private static async Task<IResult> GetPlansAsync(
        IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();

        var plans = await db.Plans.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var planIds = plans.Select(x => x.Id).ToArray();
        var components = await db.PlanPricingComponents.AsNoTracking()
            .Where(x => planIds.Contains(x.PlanId) && x.EffectiveTo == null)
            .OrderBy(x => x.MetricType)
            .ToListAsync(ct);
        var componentIds = components.Select(x => x.Id).ToArray();
        var tiers = await db.PlanPricingTiers.AsNoTracking()
            .Where(x => componentIds.Contains(x.ComponentId))
            .OrderBy(x => x.FromQuantity)
            .ToListAsync(ct);

        return Results.Ok(plans.Select(plan => ToPlanResponse(
            plan,
            components.Where(x => x.PlanId == plan.Id).ToArray(),
            tiers)));
    }

    private static async Task<IResult> CreatePlanAsync(
        PlanBillingRequest request, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        var validation = ValidatePlanRequest(request);
        if (validation is not null) return Results.BadRequest(new { error = validation });

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Plans.AsNoTracking().AnyAsync(x => x.Code == code, ct))
            return Results.Conflict(new { error = "plan_code_exists" });

        var effectiveFrom = request.EffectiveFrom ?? DateTimeOffset.UtcNow;
        var plan = new Plan(code, request.Name, request.MaxEmployers, request.MaxEmployees, request.MaxUsers, request.IsActive);
        plan.UpdateBillingDefinition(request.Description, request.Currency, request.BillingInterval,
            request.CorrectionBillingMode, request.CorrectionUnitPrice,
            request.IncludedCorrections, request.IncludedCorrectionRows, effectiveFrom);

        db.Plans.Add(plan);
        AddPricingComponents(db, plan, request, effectiveFrom);
        await db.SaveChangesAsync(ct);

        var components = await db.PlanPricingComponents.AsNoTracking()
            .Where(x => x.PlanId == plan.Id && x.EffectiveTo == null).ToListAsync(ct);
        var componentIds = components.Select(x => x.Id).ToArray();
        var tiers = await db.PlanPricingTiers.AsNoTracking()
            .Where(x => componentIds.Contains(x.ComponentId)).ToListAsync(ct);
        return Results.Created($"/api/platform/billing/plans/{plan.Id}", ToPlanResponse(plan, components, tiers));
    }

    private static async Task<IResult> UpdatePlanAsync(
        Guid planId, PlanBillingRequest request, IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        var validation = ValidatePlanRequest(request);
        if (validation is not null) return Results.BadRequest(new { error = validation });

        var plan = await db.Plans.SingleOrDefaultAsync(x => x.Id == planId, ct);
        if (plan is null) return Results.NotFound();
        if (!string.Equals(plan.Code, request.Code.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "plan_code_is_immutable" });

        var effectiveFrom = request.EffectiveFrom ?? DateTimeOffset.UtcNow;
        var current = await db.PlanPricingComponents
            .Where(x => x.PlanId == plan.Id && x.EffectiveTo == null)
            .ToListAsync(ct);

        foreach (var item in current)
        {
            if (effectiveFrom <= item.EffectiveFrom)
                return Results.BadRequest(new { error = "effective_from_must_be_after_current_version" });
            item.Close(effectiveFrom);
        }

        plan.UpdateDefinition(request.Name, request.MaxEmployers, request.MaxEmployees, request.MaxUsers, request.IsActive);
        plan.BumpVersion(effectiveFrom);
        plan.UpdateBillingDefinition(request.Description, request.Currency, request.BillingInterval,
            request.CorrectionBillingMode, request.CorrectionUnitPrice,
            request.IncludedCorrections, request.IncludedCorrectionRows, effectiveFrom);

        AddPricingComponents(db, plan, request, effectiveFrom);
        await db.SaveChangesAsync(ct);

        var components = await db.PlanPricingComponents.AsNoTracking()
            .Where(x => x.PlanId == plan.Id && x.EffectiveTo == null).ToListAsync(ct);
        var componentIds = components.Select(x => x.Id).ToArray();
        var tiers = await db.PlanPricingTiers.AsNoTracking()
            .Where(x => componentIds.Contains(x.ComponentId)).ToListAsync(ct);
        return Results.Ok(ToPlanResponse(plan, components, tiers));
    }

    private static async Task<IResult> SimulateAsync(
        Guid planId, PricingSimulationRequest request, IAlphaDbContext db,
        ICurrentUser currentUser, IBillingCalculator calculator, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (request.Employers < 0 || request.Employees < 0 || request.ReportRows < 0 ||
            request.Corrections < 0 || request.CorrectedRows < 0)
            return Results.BadRequest(new { error = "usage_cannot_be_negative" });

        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, ct);
        if (plan is null) return Results.NotFound();
        var now = DateTimeOffset.UtcNow;
        var components = await db.PlanPricingComponents.AsNoTracking()
            .Where(x => x.PlanId == planId && x.EffectiveFrom <= now &&
                        (!x.EffectiveTo.HasValue || x.EffectiveTo > now) && x.IsEnabled)
            .ToListAsync(ct);
        var ids = components.Select(x => x.Id).ToArray();
        var tiers = await db.PlanPricingTiers.AsNoTracking()
            .Where(x => ids.Contains(x.ComponentId)).ToListAsync(ct);

        var calculation = calculator.Calculate(plan, components, tiers,
            new BillingUsageSnapshot(request.Employers, request.Employees, request.ReportRows,
                request.Corrections, request.CorrectedRows));
        return Results.Ok(calculation);
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
                x.Id, x.BillingAccountId, x.PlanId, x.PeriodStart, x.PeriodEnd,
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
    }

    private static async Task<IResult> RefundAsync(
        Guid paymentId, RefundPaymentRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        IPaymentProviderResolver providers, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Results.BadRequest(new { error = "amount_and_idempotency_key_required" });

        var existing = await db.Refunds.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
            return Results.Ok(new { existing.Id, existing.PaymentId, existing.Amount, existing.Status, existing.ProviderRefundId, existing.ErrorMessage });

        var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == paymentId, ct);
        if (payment is null) return Results.NotFound();
        if (payment.Status is not BillingPaymentStatus.Succeeded and not BillingPaymentStatus.PartiallyRefunded)
            return Results.Conflict(new { error = "payment_not_refundable" });

        var refunded = await db.Refunds.AsNoTracking()
            .Where(x => x.PaymentId == paymentId && x.Status == BillingRefundStatus.Succeeded)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        if (refunded + request.Amount > payment.Amount)
            return Results.BadRequest(new { error = "refund_exceeds_remaining_amount", remaining = payment.Amount - refunded });

        var account = await db.BillingAccounts.SingleAsync(x => x.Id == payment.BillingAccountId, ct);
        var provider = providers.Resolve(payment.Provider);
        var refund = new Refund(payment.Id, request.Amount, request.Reason ?? string.Empty, request.IdempotencyKey.Trim());
        db.Refunds.Add(refund);
        await db.SaveChangesAsync(ct);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new PaymentRefundResult(false, string.Empty, "provider_exception", ex.Message);
        }

        refund.Complete(result.Success, result.RefundId, result.ErrorMessage);
        if (result.Success)
            payment.MarkRefunded(refunded + request.Amount < payment.Amount);

        await db.SaveChangesAsync(ct);
        return result.Success
            ? Results.Ok(new { refund.Id, refund.PaymentId, refund.Amount, refund.Status, refund.ProviderRefundId })
            : Results.Json(new { refund.Id, refund.Status, result.ErrorCode, result.ErrorMessage },
                statusCode: StatusCodes.Status502BadGateway);
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
                x.Id, x.BillingAccountId, x.PlanId, x.PeriodStart, x.PeriodEnd,
                x.Status, x.Currency, x.Subtotal, x.Total, x.CalculationSnapshotJson,
                x.CalculatedAt, x.ChargedAt
            }).ToListAsync(ct);

    private static object ToPlanResponse(
        Plan plan,
        IReadOnlyCollection<PlanPricingComponent> components,
        IReadOnlyCollection<PlanPricingTier> tiers) => new
    {
        plan.Id,
        plan.Code,
        plan.Name,
        plan.Description,
        plan.Currency,
        plan.BillingInterval,
        plan.Version,
        plan.EffectiveFrom,
        plan.EffectiveTo,
        plan.CorrectionBillingMode,
        plan.CorrectionUnitPrice,
        plan.IncludedCorrections,
        plan.IncludedCorrectionRows,
        plan.MaxEmployers,
        plan.MaxEmployees,
        plan.MaxUsers,
        plan.IsActive,
        components = components.OrderBy(x => x.MetricType).Select(x => new
        {
            x.Id, x.Version, x.EffectiveFrom, x.EffectiveTo, x.MetricType,
            x.PricingType, x.UnitPrice, x.IncludedQuantity, x.MinimumCharge,
            x.MaximumCharge, x.IsEnabled, x.CorrectionMode,
            tiers = tiers.Where(t => t.ComponentId == x.Id)
                .OrderBy(t => t.FromQuantity)
                .Select(t => new { t.Id, t.FromQuantity, t.ToQuantity, t.UnitPrice })
        })
    };

    private static string? ValidatePlanRequest(PlanBillingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return "code_and_name_required";
        if (request.MaxEmployers < 0 || request.MaxEmployees < 0 || request.MaxUsers < 0 ||
            request.IncludedCorrections < 0 || request.IncludedCorrectionRows < 0 ||
            request.CorrectionUnitPrice < 0)
            return "negative_values_not_allowed";
        if (request.Components.GroupBy(x => x.MetricType).Any(x => x.Count() > 1))
            return "duplicate_metric_component";
        if (request.Components.Any(x => x.UnitPrice < 0 || x.IncludedQuantity < 0 ||
                                        x.MinimumCharge < 0 || x.MaximumCharge < 0 ||
                                        x.MinimumCharge > x.MaximumCharge))
            return "invalid_pricing_component";

        foreach (var component in request.Components.Where(x => x.PricingType == BillingPricingType.Tiered && x.IsEnabled))
        {
            var tiers = (component.Tiers ?? []).OrderBy(x => x.FromQuantity).ToArray();
            if (tiers.Length == 0 || tiers[0].FromQuantity != 0)
                return "tiered_pricing_requires_tiers_starting_at_zero";

            for (var index = 0; index < tiers.Length; index++)
            {
                var tier = tiers[index];
                if (tier.FromQuantity < 0 || tier.ToQuantity < 0 || tier.UnitPrice < 0 ||
                    (tier.ToQuantity.HasValue && tier.ToQuantity <= tier.FromQuantity))
                    return "invalid_pricing_tier";

                if (index < tiers.Length - 1)
                {
                    if (!tier.ToQuantity.HasValue || tier.ToQuantity.Value != tiers[index + 1].FromQuantity)
                        return "pricing_tiers_must_be_contiguous";
                }
                else if (tier.ToQuantity.HasValue)
                    return "last_pricing_tier_must_be_open_ended";
            }
        }

        return null;
    }

    private static void AddPricingComponents(
        IAlphaDbContext db, Plan plan, PlanBillingRequest request, DateTimeOffset effectiveFrom)
    {
        foreach (var component in request.Components)
        {
            CorrectionBillingMode? correctionMode = component.MetricType == BillingMetricType.Correction
                ? component.CorrectionMode ?? request.CorrectionBillingMode
                : null;
            var entity = new PlanPricingComponent(
                plan.Id, component.MetricType, component.PricingType, component.UnitPrice,
                component.IncludedQuantity, component.MinimumCharge, component.MaximumCharge,
                component.IsEnabled, plan.Version, effectiveFrom, correctionMode);
            db.PlanPricingComponents.Add(entity);

            if (component.PricingType == BillingPricingType.Tiered)
            {
                foreach (var tier in (component.Tiers ?? []).OrderBy(x => x.FromQuantity))
                    db.PlanPricingTiers.Add(new PlanPricingTier(
                        entity.Id, tier.FromQuantity, tier.ToQuantity, tier.UnitPrice));
            }
        }

        if (!request.Components.Any(x => x.MetricType == BillingMetricType.Correction))
        {
            var included = request.CorrectionBillingMode == CorrectionBillingMode.PerCorrection
                ? request.IncludedCorrections
                : request.IncludedCorrectionRows;
            db.PlanPricingComponents.Add(new PlanPricingComponent(
                plan.Id, BillingMetricType.Correction, BillingPricingType.PerUnit,
                request.CorrectionUnitPrice ?? 0, included, null, null, true,
                plan.Version, effectiveFrom, request.CorrectionBillingMode));
        }
    }
}
