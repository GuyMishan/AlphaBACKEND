using System.Text.Json;
using Alpha.Api.Contracts;
using Alpha.Api.Validation;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class EmployerEndpoints
{
    public static IEndpointRouteBuilder MapEmployerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers")
            .RequireAuthorization().WithTags("Employers");

        group.MapGet("/", async (Guid organizationId, IAlphaDbContext db, ICurrentUser user,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var query = ApplyEmployerAccess(db.Employers.AsNoTracking().Where(x => x.OrganizationId == organizationId), organizationId, db, user, await access.GetMembershipAsync(organizationId, ct));
            return Results.Ok(await query.OrderBy(x => x.LegalName).Take(500).ToListAsync(ct));
        });

        group.MapGet("/search", async (Guid organizationId, string? search, int skip, int take,
            IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
            skip = Math.Max(0, skip);
            take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
            var query = ApplyEmployerAccess(db.Employers.AsNoTracking().Where(x => x.OrganizationId == organizationId), organizationId, db, user, await access.GetMembershipAsync(organizationId, ct));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(x => x.LegalName.ToLower().Contains(term)
                    || x.RegistrationNumber.Contains(term)
                    || x.WithholdingFileNumber.Contains(term));
            }
            var page = await query.OrderBy(x => x.LegalName).Skip(skip).Take(take + 1).ToListAsync(ct);
            var hasMore = page.Count > take;
            if (hasMore) page.RemoveAt(page.Count - 1);
            return Results.Ok(new { items = page, hasMore });
        });

        group.MapPost("/", async (Guid organizationId, CreateEmployerRequest request, IAlphaDbContext db,
            ICurrentUser user, OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanCreateEmployerAsync(organizationId, ct)) return Results.Forbid();
            var validationError = ApiInputValidation.Employer(request.LegalName, request.RegistrationNumber, request.WithholdingFileNumber);
            if (validationError is not null) return Results.BadRequest(new { error = validationError });
            var item = new Employer(organizationId, request.LegalName.Trim(), request.RegistrationNumber.Trim(), request.WithholdingFileNumber.Trim());
            db.Employers.Add(item);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employer.created", nameof(Employer), item.Id,
                organizationId, item.Id, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/employers/{item.Id}", item);
        });

        group.MapGet("/{employerId:guid}", async (Guid organizationId, Guid employerId, IAlphaDbContext db,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var item = await db.Employers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == employerId && x.OrganizationId == organizationId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/{employerId:guid}/capabilities", async (Guid organizationId, Guid employerId,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            return Results.Ok(new
            {
                canEditEmployer = await access.CanEditEmployerAsync(organizationId, employerId, ct),
                canCreateEmployee = await access.CanCreateEmployeeAsync(organizationId, employerId, ct),
                canEditEmployee = await access.CanEditEmployeeAsync(organizationId, employerId, ct)
            });
        });

        group.MapPut("/{employerId:guid}", async (Guid organizationId, Guid employerId,
            UpdateEmployerRequest request, IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var validationError = ApiInputValidation.Employer(request.LegalName, request.RegistrationNumber, request.WithholdingFileNumber);
            if (validationError is not null) return Results.BadRequest(new { error = validationError });
            var item = await db.Employers.SingleOrDefaultAsync(x =>
                x.Id == employerId && x.OrganizationId == organizationId, ct);
            if (item is null) return Results.NotFound();
            item.Update(request.LegalName.Trim(), request.RegistrationNumber.Trim(), request.WithholdingFileNumber.Trim());
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employer.updated", nameof(Employer), item.Id,
                organizationId, item.Id, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Ok(item);
        });

        group.MapGet("/{employerId:guid}/employees", async (Guid organizationId, Guid employerId, IAlphaDbContext db,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var items = await EmployeeQuery(db, organizationId, employerId).OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Take(500).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapGet("/{employerId:guid}/employees/search", async (Guid organizationId, Guid employerId,
            string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            skip = Math.Max(0, skip);
            take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
            var query = EmployeeQuery(db, organizationId, employerId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(x => x.FirstName.ToLower().Contains(term)
                    || x.LastName.ToLower().Contains(term)
                    || x.NationalId.Contains(term)
                    || x.EmployeeNumber.Contains(term));
            }
            var page = await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Skip(skip).Take(take + 1).ToListAsync(ct);
            var hasMore = page.Count > take;
            if (hasMore) page.RemoveAt(page.Count - 1);
            return Results.Ok(new { items = page, hasMore });
        });

        group.MapPost("/{employerId:guid}/employees", async (Guid organizationId, Guid employerId,
            CreateEmployeeRequest request, IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanCreateEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
            var validationError = ApiInputValidation.Employee(request.NationalId, request.FirstName, request.LastName, request.EmployeeNumber, request.StartDate)
                ?? ValidateEmployeeProfile(request.BirthDate, request.Gender, request.Email, request.Mobile, request.City, request.Street,
                    request.HouseNumber, request.Apartment, request.PostalCode, request.PostOfficeBox);
            if (validationError is not null) return Results.BadRequest(new { error = validationError });
            if (request.MonthlySalary < 0) return Results.BadRequest(new { error = "Monthly salary cannot be negative." });
            if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
            var nationalId = request.NationalId.Trim();
            var person = await db.People.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.NationalId == nationalId, ct);
            if (person is null)
            {
                person = new Person(organizationId, nationalId, request.FirstName.Trim(), request.LastName.Trim(),
                    request.BirthDate, request.Gender, request.Email, request.Mobile, request.City, request.Street,
                    request.HouseNumber, request.Apartment, request.PostalCode, request.PostOfficeBox);
                db.People.Add(person);
            }
            else
            {
                person.Update(request.NationalId, request.FirstName, request.LastName);
                person.UpdateInterfaceDetails(request.BirthDate, request.Gender, request.Email, request.Mobile, request.City,
                    request.Street, request.HouseNumber, request.Apartment, request.PostalCode, request.PostOfficeBox);
            }
            var employeeNumber = request.EmployeeNumber.Trim();
            if (await db.Employments.AnyAsync(x => x.EmployerId == employerId && x.EmployeeNumber == employeeNumber, ct)) return Results.Conflict(new { error = "Employee number already exists for this employer." });
            var employment = new Employment(organizationId, employerId, person.Id, request.StartDate, employeeNumber, request.MonthlySalary);
            db.Employments.Add(employment);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employment.created", nameof(Employment), employment.Id, organizationId, employerId, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/employees/{employment.Id}", new { employment.Id, PersonId = person.Id, employment.MonthlySalary });
        });

        group.MapGet("/{employerId:guid}/employees/{employmentId:guid}", async (Guid organizationId, Guid employerId, Guid employmentId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var item = await EmployeeQuery(db, organizationId, employerId).SingleOrDefaultAsync(x => x.Id == employmentId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPut("/{employerId:guid}/employees/{employmentId:guid}", async (Guid organizationId, Guid employerId, Guid employmentId, UpdateEmployeeRequest request, IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
            var validationError = ApiInputValidation.Employee(request.NationalId, request.FirstName, request.LastName, request.EmployeeNumber, request.StartDate)
                ?? ValidateEmployeeProfile(request.BirthDate, request.Gender, request.Email, request.Mobile, request.City, request.Street,
                    request.HouseNumber, request.Apartment, request.PostalCode, request.PostOfficeBox);
            if (validationError is not null) return Results.BadRequest(new { error = validationError });
            if (request.MonthlySalary < 0) return Results.BadRequest(new { error = "Monthly salary cannot be negative." });
            var employment = await db.Employments.SingleOrDefaultAsync(x => x.Id == employmentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
            if (employment is null) return Results.NotFound();
            var person = await db.People.SingleAsync(x => x.Id == employment.PersonId, ct);
            var nationalId = request.NationalId.Trim();
            var employeeNumber = request.EmployeeNumber.Trim();
            if (await db.People.AnyAsync(x => x.OrganizationId == organizationId && x.NationalId == nationalId && x.Id != person.Id, ct)) return Results.Conflict(new { error = "National ID already exists in this organization." });
            if (await db.Employments.AnyAsync(x => x.EmployerId == employerId && x.EmployeeNumber == employeeNumber && x.Id != employmentId, ct)) return Results.Conflict(new { error = "Employee number already exists for this employer." });
            person.Update(nationalId, request.FirstName.Trim(), request.LastName.Trim());
            person.UpdateInterfaceDetails(request.BirthDate, request.Gender, request.Email, request.Mobile, request.City,
                request.Street, request.HouseNumber, request.Apartment, request.PostalCode, request.PostOfficeBox);
            employment.Update(employeeNumber, request.StartDate, request.MonthlySalary);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employment.updated", nameof(Employment), employment.Id, organizationId, employerId, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Ok(await EmployeeQuery(db, organizationId, employerId).SingleAsync(x => x.Id == employmentId, ct));
        });
        return endpoints;
    }

    private static string? ValidateEmployeeProfile(DateOnly birthDate, PersonGender gender, string? email, string? mobile,
        string? city, string? street, string? houseNumber, string? apartment, string? postalCode, string? postOfficeBox)
    {
        if (birthDate >= DateOnly.FromDateTime(DateTime.UtcNow)) return "Birth date must be in the past.";
        if (!Enum.IsDefined(gender)) return "Employee gender is invalid.";
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return "Employee email is required.";
        if (string.IsNullOrWhiteSpace(mobile) || !mobile.Any(char.IsDigit)) return "Employee mobile is required.";
        if (string.IsNullOrWhiteSpace(city)) return "Employee city is required.";
        if (string.IsNullOrWhiteSpace(street)) return "Employee street is required.";
        if (string.IsNullOrWhiteSpace(houseNumber)) return "Employee house number is required.";
        if (string.IsNullOrWhiteSpace(apartment)) return "Employee apartment is required.";
        if (string.IsNullOrWhiteSpace(postalCode) || !postalCode.All(char.IsDigit)) return "Employee postal code is required and must be numeric.";
        if (string.IsNullOrWhiteSpace(postOfficeBox)) return "Employee post office box is required.";
        return null;
    }

    private static IQueryable<Employer> ApplyEmployerAccess(IQueryable<Employer> query, Guid organizationId, IAlphaDbContext db, ICurrentUser user, OrganizationMembership? membership)
    {
        if (!user.IsPlatformAdmin && membership?.EmployerAccessMode == EmployerAccessMode.SelectedEmployers)
        {
            var ids = db.EmployerUserAccesses.Where(x => x.UserId == user.UserId).Select(x => x.EmployerId);
            query = query.Where(x => ids.Contains(x.Id));
        }
        return query;
    }

    private static IQueryable<EmployeeListRow> EmployeeQuery(IAlphaDbContext db, Guid organizationId, Guid employerId) =>
        from employment in db.Employments.AsNoTracking()
        join person in db.People.AsNoTracking() on employment.PersonId equals person.Id
        where employment.OrganizationId == organizationId && employment.EmployerId == employerId
        select new EmployeeListRow
        {
            Id = employment.Id,
            EmployeeNumber = employment.EmployeeNumber,
            Status = employment.Status,
            StartDate = employment.StartDate,
            EndDate = employment.EndDate,
            MonthlySalary = employment.MonthlySalary,
            PersonId = person.Id,
            NationalId = person.NationalId,
            FirstName = person.FirstName,
            LastName = person.LastName,
            BirthDate = person.BirthDate,
            Gender = person.Gender,
            Email = person.Email,
            Mobile = person.Mobile,
            City = person.City,
            Street = person.Street,
            HouseNumber = person.HouseNumber,
            Apartment = person.Apartment,
            PostalCode = person.PostalCode,
            PostOfficeBox = person.PostOfficeBox
        };

    private sealed class EmployeeListRow
    {
        public Guid Id { get; init; }
        public string EmployeeNumber { get; init; } = string.Empty;
        public EmploymentStatus Status { get; init; }
        public DateOnly StartDate { get; init; }
        public DateOnly? EndDate { get; init; }
        public decimal MonthlySalary { get; init; }
        public Guid PersonId { get; init; }
        public string NationalId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public DateOnly? BirthDate { get; init; }
        public PersonGender? Gender { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Mobile { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string Street { get; init; } = string.Empty;
        public string HouseNumber { get; init; } = string.Empty;
        public string Apartment { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public string PostOfficeBox { get; init; } = string.Empty;
    }
}
