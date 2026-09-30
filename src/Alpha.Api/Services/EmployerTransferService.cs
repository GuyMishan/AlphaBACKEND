using System.Text.Json;
using Alpha.Application.Billing;
using Alpha.Application.Entitlements;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

/// <summary>
/// Moves ownership of exactly one employer; callers must verify platform-admin authorization.
/// The historical billing ledger and append-only audit records are intentionally not rewritten.
/// </summary>
public sealed class EmployerTransferService(
    AlphaDbContext db,
    EntitlementService entitlements,
    BillingInheritanceService billingInheritance,
    OrganizationEntitlementLock entitlementLock)
{
    public sealed record Result(bool Success, string? Error = null, string? Message = null);

    public async Task<Result> TransferAsync(
        Guid sourceOrganizationId, Guid employerId, Guid targetOrganizationId,
        Guid actorId, string traceIdentifier, CancellationToken ct)
    {
        if (targetOrganizationId == Guid.Empty || targetOrganizationId == sourceOrganizationId)
            return new(false, "invalid_target", "בחר ארגון יעד שונה מהארגון הנוכחי.");

        // The same advisory locks are also used by quota-bound employer/employee creation.
        // Stable ordering prevents deadlocks between concurrent transfers.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var organizationId in new[] { sourceOrganizationId, targetOrganizationId }.OrderBy(x => x))
            await entitlementLock.AcquireInCurrentTransactionAsync(organizationId, ct);

        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == sourceOrganizationId, ct);
        if (employer is null)
            return new(false, "employer_not_found", "המעסיק אינו משויך עוד לארגון המקור.");
        if (!await db.Organizations.AnyAsync(x => x.Id == targetOrganizationId, ct))
            return new(false, "target_not_found", "ארגון היעד לא נמצא.");

        if (await db.Employers.AnyAsync(x => x.OrganizationId == targetOrganizationId &&
            (x.RegistrationNumber == employer.RegistrationNumber ||
             x.WithholdingFileNumber == employer.WithholdingFileNumber), ct))
            return new(false, "duplicate_employer", "ארגון היעד כבר מכיל מעסיק בעל ח.פ. או תיק ניכויים זהה.");

        // Never transfer while a background send is underway: its callback can still
        // write new records under the old organization scope.
        if (await db.ReportTransmissions.AnyAsync(x => x.EmployerId == employerId &&
            (x.Status == Alpha.Domain.Reporting.ReportTransmissionStatus.Pending ||
             x.Status == Alpha.Domain.Reporting.ReportTransmissionStatus.Sending), ct))
            return new(false, "transmission_in_progress", "יש להשלים שידורים ממתינים לפני העברת המעסיק.");

        var employments = await db.Employments.AsNoTracking()
            .Where(x => x.EmployerId == employerId)
            .Select(x => new { x.Id, x.PersonId, x.Status })
            .ToListAsync(ct);
        var personIds = employments.Select(x => x.PersonId).Distinct().ToArray();
        var people = await db.People.AsNoTracking()
            .Where(x => personIds.Contains(x.Id))
            .ToListAsync(ct);

        // The identity check includes passports as well as national IDs. Never leak
        // the identifier itself in an error, log or audit event.
        if (people.Count != personIds.Length || people.Any(x => x.OrganizationId != sourceOrganizationId ||
            string.IsNullOrWhiteSpace(x.NationalIdLookupHash) ||
            string.IsNullOrWhiteSpace(x.NationalIdEncrypted)))
            return new(false, "identity_incomplete", "לא ניתן להעביר עובדים בעלי רשומת זהות חסרה או לא תקינה.");
        var hashes = people.Select(x => x.NationalIdLookupHash!).ToArray();
        if (await db.People.AnyAsync(x => x.OrganizationId == targetOrganizationId &&
            hashes.Contains(x.NationalIdLookupHash!), ct))
        {
            var targetPeople = await db.People.AsNoTracking()
                .Where(x => x.OrganizationId == targetOrganizationId &&
                            hashes.Contains(x.NationalIdLookupHash!))
                .Select(x => new { x.IdentifierType, x.NationalIdLookupHash })
                .ToListAsync(ct);
            var incoming = people.Select(x => (x.IdentifierType, x.NationalIdLookupHash)).ToHashSet();
            if (targetPeople.Any(x => incoming.Contains((x.IdentifierType, x.NationalIdLookupHash))))
                return new(false, "duplicate_identity", "בארגון היעד קיימים עובדים עם תעודת זהות או דרכון זהים.");
        }

        var employerLimit = await entitlements.GetEmployerLimit(targetOrganizationId, ct);
        if (employer.Status != EmployerStatus.Closed && employerLimit.HasValue &&
            await db.Employers.CountAsync(x => x.OrganizationId == targetOrganizationId &&
                x.Status != EmployerStatus.Closed, ct) >= employerLimit.Value)
            return new(false, "target_employer_limit", "ארגון היעד הגיע למגבלת המעסיקים.");

        var employeeLimit = await entitlements.GetEmployeeLimit(targetOrganizationId, ct);
        var movingActive = employments.Count(x => x.Status == EmploymentStatus.Active);
        if (employeeLimit.HasValue && movingActive +
            await db.Employments.CountAsync(x => x.OrganizationId == targetOrganizationId &&
                x.Status == EmploymentStatus.Active, ct) > employeeLimit.Value)
            return new(false, "target_employee_limit", "העברת המעסיק חורגת ממגבלת העובדים בארגון היעד.");

        // Membership-based grants belong to the former organization, not to the
        // employer. Standalone employer-only users retain their explicit access.
        var oldMemberIds = await db.OrganizationMemberships.AsNoTracking()
            .Where(x => x.OrganizationId == sourceOrganizationId)
            .Select(x => x.UserId)
            .ToListAsync(ct);
        var grants = await db.EmployerUserAccesses.AsNoTracking()
            .Where(x => x.EmployerId == employerId && x.OrganizationId == sourceOrganizationId)
            .ToListAsync(ct);
        var standaloneIds = grants.Where(x => !oldMemberIds.Contains(x.UserId))
            .Select(x => x.UserId).Distinct().ToArray();

        var userLimit = await entitlements.GetUserLimit(targetOrganizationId, ct);
        if (userLimit.HasValue)
        {
            var memberIds = await db.OrganizationMemberships.AsNoTracking()
                .Where(x => x.OrganizationId == targetOrganizationId && x.IsActive &&
                    (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
                .Select(x => x.UserId).ToListAsync(ct);
            var directIds = await db.EmployerUserAccesses.AsNoTracking()
                .Where(x => x.OrganizationId == targetOrganizationId).Select(x => x.UserId).ToListAsync(ct);
            var pending = await db.UserInvitations.AsNoTracking()
                .Where(x => x.OrganizationId == targetOrganizationId &&
                    x.Status == UserInvitationStatus.Pending && x.ExpiresAt > DateTimeOffset.UtcNow)
                .Select(x => x.Email).Distinct().CountAsync(ct);
            if (memberIds.Concat(directIds).Concat(standaloneIds).Distinct().Count() + pending > userLimit.Value)
                return new(false, "target_user_limit", "העברת המשתמשים חורגת ממגבלת המשתמשים בארגון היעד.");
        }

        // Person rows can be shared between multiple employers in the source
        // organization. Clone such people rather than stealing the other
        // employers' Person rows; snapshots keep their immutable encrypted data.
        foreach (var person in people)
        {
            var shared = await db.Employments.AsNoTracking()
                .AnyAsync(x => x.PersonId == person.Id && x.EmployerId != employerId, ct);
            if (!shared) continue;
            var clone = new Person(targetOrganizationId, "",
                person.FirstName, person.LastName, person.BirthDate, person.Gender,
                person.Email, person.Mobile, person.City, person.Street,
                person.HouseNumber, person.Apartment, person.PostalCode, person.PostOfficeBox);
            clone.SetProtectedIdentifier(person.IdentifierType, person.NationalIdEncrypted!,
                person.NationalIdLookupHash!);
            db.People.Add(clone);
            await db.SaveChangesAsync(ct);
            await db.Employments.Where(x => x.EmployerId == employerId && x.PersonId == person.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PersonId, clone.Id), ct);
            await db.ManualReportEmployees.Where(x => x.EmployerId == employerId && x.PersonId == person.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PersonId, clone.Id), ct);
        }

        var movableIds = new List<Guid>();
        foreach (var person in people)
        {
            if (!await db.Employments.AsNoTracking()
                .AnyAsync(x => x.PersonId == person.Id && x.EmployerId != employerId, ct))
                movableIds.Add(person.Id);
        }
        if (movableIds.Count > 0)
            await db.People.Where(x => movableIds.Contains(x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);

        // Re-scope references together; no employer can ever be left orphaned.
        await db.Employments.Where(x => x.EmployerId == employerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);
        await db.ManualReportEmployees.Where(x => x.EmployerId == employerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);
        await db.ManualReports.Where(x => x.EmployerId == employerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);
        await db.ReportTransmissions.Where(x => x.EmployerId == employerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);
        await db.EmployerInterfaceFeedback.Where(x => x.EmployerId == employerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);
        await db.EmployerPaymentAccounts.Where(x => x.EmployerId == employerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);

        // Cancel unaccepted invitations from the former organization; never
        // reissue them silently for the new owner.
        var invites = await db.UserInvitations
            .Where(x => x.OrganizationId == sourceOrganizationId && x.EmployerId == employerId &&
                x.Status == UserInvitationStatus.Pending).ToListAsync(ct);
        foreach (var invitation in invites) invitation.Cancel();

        var membershipGrantIds = grants.Where(x => oldMemberIds.Contains(x.UserId))
            .Select(x => x.Id).ToArray();
        if (membershipGrantIds.Length > 0)
            await db.EmployerUserAccesses.Where(x => membershipGrantIds.Contains(x.Id))
                .ExecuteDeleteAsync(ct);
        if (standaloneIds.Length > 0)
            await db.EmployerUserAccesses.Where(x => x.EmployerId == employerId &&
                    x.OrganizationId == sourceOrganizationId && standaloneIds.Contains(x.UserId))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);

        // Transfer the employer last, while all dependent writes are protected
        // by this transaction.
        await db.Employers.Where(x => x.Id == employerId && x.OrganizationId == sourceOrganizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, targetOrganizationId), ct);
        await billingInheritance.NormalizeDefaultsAsync(sourceOrganizationId, ct);
        await billingInheritance.NormalizeDefaultsAsync(targetOrganizationId, ct);

        db.AuditEvents.Add(new AuditEvent(actorId, "employer.transferred", nameof(Employer),
            employerId, targetOrganizationId, employerId,
            JsonSerializer.Serialize(new { sourceOrganizationId, targetOrganizationId, employerId }),
            traceIdentifier));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true);
    }
}
