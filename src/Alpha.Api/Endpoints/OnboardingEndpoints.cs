using System.Text.Json;
using Alpha.Api.Contracts;
using Alpha.Api.Validation;
using Alpha.Application.Abstractions;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Subscriptions;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/onboarding").RequireAuthorization().WithTags("Onboarding");

        group.MapGet("/status", async (IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated) return Results.Unauthorized();
            if (currentUser.IsPlatformAdmin)
                return Results.Ok(new { needsOnboarding = false, hasAccess = true });

            var hasOrganizationAccess = await db.OrganizationMemberships.AsNoTracking().AnyAsync(x =>
                x.UserId == currentUser.UserId && x.IsActive &&
                (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct);
            var hasEmployerAccess = await db.EmployerUserAccesses.AsNoTracking()
                .AnyAsync(x => x.UserId == currentUser.UserId, ct);
            var hasAccess = hasOrganizationAccess || hasEmployerAccess;

            return Results.Ok(new { needsOnboarding = !hasAccess, hasAccess });
        });

        group.MapPost("/self-service", async (CreateEmployerRequest request, AlphaDbContext db,
            ICurrentUser currentUser, HttpContext http, CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated || currentUser.IsPlatformAdmin) return Results.Forbid();

            var validationError = ApiInputValidation.Employer(
                request.LegalName, request.RegistrationNumber, request.WithholdingFileNumber,
                request.ContactFirstName, request.ContactLastName, request.ContactPhone, request.ContactEmail, request.ContactMobile);
            if (validationError is not null) return Results.BadRequest(new { error = validationError });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({currentUser.UserId.ToString()}))", ct);

            var hasOrganizationAccess = await db.OrganizationMemberships.AnyAsync(x =>
                x.UserId == currentUser.UserId && x.IsActive &&
                (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct);
            var hasEmployerAccess = await db.EmployerUserAccesses.AnyAsync(x =>
                x.UserId == currentUser.UserId, ct);
            if (hasOrganizationAccess || hasEmployerAccess)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { error = "onboarding_already_completed" });
            }

            var organization = new Organization(request.LegalName.Trim(), OrganizationType.SelfService);
            organization.Activate();
            db.Organizations.Add(organization);

            var employer = new Employer(
                organization.Id,
                request.LegalName.Trim(),
                request.RegistrationNumber.Trim(),
                request.WithholdingFileNumber.Trim(),
                request.ContactFirstName,
                request.ContactLastName,
                request.ContactPhone,
                request.ContactEmail,
                request.ContactMobile);
            db.Employers.Add(employer);
            var employerSettings = new EmployerProfileSettings(employer.Id);
            employerSettings.ApplyDefaultBillingMode(EmployerBillingMode.EmployerDirect);
            db.EmployerProfileSettings.Add(employerSettings);

            var ownerAccess = new EmployerUserAccess(
                currentUser.UserId,
                organization.Id,
                employer.Id,
                EmployerRole.Owner);
            db.EmployerUserAccesses.Add(ownerAccess);

            var freePlan = await db.Plans.SingleAsync(x => x.Code == SubscriptionSchemaInitializer.FreePlanCode && x.IsActive, ct);
            db.Subscriptions.Add(new Subscription(organization.Id, freePlan.Id));

            db.AuditEvents.Add(new AuditEvent(
                currentUser.UserId,
                "self_service.onboarding.completed",
                nameof(Employer),
                employer.Id,
                organization.Id,
                employer.Id,
                JsonSerializer.Serialize(new { organizationId = organization.Id, employerId = employer.Id, role = EmployerRole.Owner }),
                http.TraceIdentifier));

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Created($"/api/organizations/{organization.Id}/employers/{employer.Id}", new
            {
                organizationId = organization.Id,
                employerId = employer.Id,
                employer,
                employerRole = EmployerRole.Owner,
                subscriptionPlan = freePlan.Code
            });
        });

        return endpoints;
    }
}
