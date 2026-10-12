using System.Text.Json;
using Alpha.Domain.Validation;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Alpha.Modules.Organizations.Features.Onboarding;

public sealed record CreateSelfServiceEmployerRequest(
    string LegalName,
    string RegistrationNumber,
    string WithholdingFileNumber,
    string? ContactFirstName = null,
    string? ContactLastName = null,
    string? ContactPhone = null,
    string? ContactEmail = null,
    string? ContactMobile = null);

public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/onboarding").RequireAuthorization().WithTags("Onboarding");

        group.MapGet("/status", async (AlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated) return Results.Unauthorized();
            if (currentUser.IsPlatformAdmin || await db.Users.AsNoTracking().AnyAsync(x =>
                    x.Id == currentUser.UserId && x.IsActive && x.IsReferent, ct))
                return Results.Ok(new { needsOnboarding = false, hasAccess = true });

            var hasOrganizationAccess = await db.OrganizationMemberships.AsNoTracking().AnyAsync(x =>
                x.UserId == currentUser.UserId && x.IsActive &&
                (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct);
            var hasEmployerAccess = await db.EmployerUserAccesses.AsNoTracking()
                .AnyAsync(x => x.UserId == currentUser.UserId, ct);
            var hasAccess = hasOrganizationAccess || hasEmployerAccess;

            return Results.Ok(new { needsOnboarding = !hasAccess, hasAccess });
        });

        group.MapPost("/self-service", async (CreateSelfServiceEmployerRequest request, AlphaDbContext db,
            ICurrentUser currentUser, HttpContext http, CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated || currentUser.IsPlatformAdmin ||
                await db.Users.AsNoTracking().AnyAsync(x => x.Id == currentUser.UserId && x.IsReferent, ct))
                return Results.Forbid();

            var validationError = WorkforceInputValidation.Employer(
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

            var organization = new Organization($"ארגון - {request.LegalName.Trim()}", OrganizationType.SelfService);
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
                employerRole = EmployerRole.Owner
            });
        });

        return endpoints;
    }
}
