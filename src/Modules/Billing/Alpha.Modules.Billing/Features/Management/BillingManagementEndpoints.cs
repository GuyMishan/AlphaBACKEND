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

public static partial class BillingManagementEndpoints
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
}
