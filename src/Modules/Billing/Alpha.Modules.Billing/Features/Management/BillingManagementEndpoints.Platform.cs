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
    private static async Task<IResult> GetBillingCustomersAsync(
        AlphaDbContext db, ICurrentUser currentUser, BillingInheritanceService inheritance, CancellationToken ct)
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
        BillingCustomerPricingRequest request, AlphaDbContext db,
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
        AlphaDbContext db, ICurrentUser currentUser, int take = 500, CancellationToken ct = default)
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
        Guid billingAccountId, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
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
        AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
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
        Guid billingAccountId, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
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
        AlphaDbContext db, ICurrentUser currentUser, int take = 100, CancellationToken ct = default)
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
        AlphaDbContext db, ICurrentUser currentUser, int take = 100, CancellationToken ct = default)
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
        AlphaDbContext db, ICurrentUser currentUser, int take = 100, CancellationToken ct = default)
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
        Guid billingPeriodId, AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct)
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
        if (decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero) != request.Amount)
            return Results.BadRequest(new { error = "refund_amount_precision_invalid" });
        if (request.IdempotencyKey.Trim().Length > 160)
            return Results.BadRequest(new { error = "idempotency_key_too_long" });

        Refund refund;
        Payment payment;

        await using (var reservation = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var existing = await db.Refunds.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                await reservation.RollbackAsync(ct);
                if (existing.PaymentId != paymentId || existing.Amount != request.Amount)
                    return Results.Conflict(new { error = "idempotency_key_reused_with_different_request" });
                return Results.Ok(new
                {
                    existing.Id, existing.PaymentId, existing.Amount, existing.Status,
                    existing.ProviderRefundId, existing.ErrorMessage
                });
            }

            var foundPayment = await db.Payments.SingleOrDefaultAsync(x => x.Id == paymentId, ct);
            if (foundPayment is null)
            {
                await reservation.RollbackAsync(ct);
                return Results.NotFound();
            }
            payment = foundPayment;
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
        IPaymentProvider provider;
        try
        {
            provider = providers.Resolve(payment.Provider);
        }
        catch (InvalidOperationException)
        {
            refund.RecordPendingError("provider_unavailable");
            await db.SaveChangesAsync(ct);
            return Results.Json(new
            {
                refund.Id,
                refund.Status,
                errorCode = "payment_provider_unavailable"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

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
            refund.RecordPendingError("provider_result_unknown");
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            refund.RecordPendingError("provider_result_unknown");
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(new
            {
                refund.Id,
                refund.Status,
                errorCode = "provider_result_unknown",
                errorMessage = "Refund result is unknown and requires reconciliation before retry."
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        if (!result.Success && result.ErrorCode == "refund_id_missing")
        {
            refund.RecordPendingError("provider_result_unknown");
            await db.SaveChangesAsync(ct);
            return Results.Json(new
            {
                refund.Id,
                refund.Status,
                errorCode = "provider_result_unknown",
                errorMessage = "Provider approved the refund without a refund id; reconciliation is required."
            }, statusCode: StatusCodes.Status502BadGateway);
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
}
