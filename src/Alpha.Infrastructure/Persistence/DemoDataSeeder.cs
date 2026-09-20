using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class DemoDataSeeder
{
    private const string Marker = "[DEMO]";
    private const string OrganizationUserSubject = "demo-org-admin";
    private const string EmployerUserSubject = "demo-employer-admin";
    private const string OrganizationUserNationalId = "200000008";
    private const string EmployerUserNationalId = "200000016";
    private const string OrganizationUserPhone = "0507000001";
    private const string EmployerUserPhone = "0507000002";

    public static async Task SeedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        var alreadySeeded = await db.Organizations.AsNoTracking().AnyAsync(x => x.Name.StartsWith(Marker), ct);
        if (!alreadySeeded)
            await SeedBaseDemoDataAsync(db, ct);

        await EnsureScopedDemoUsersAsync(db, ct);
        await RepairInvalidEmployeeNationalIdsAsync(db, ct);
    }

    private static async Task SeedBaseDemoDataAsync(AlphaDbContext db, CancellationToken ct)
    {
        var organizations = new[]
        {
            new Organization($"{Marker} קבוצת אלפא", OrganizationType.CorporateGroup),
            new Organization($"{Marker} חשבות פלוס", OrganizationType.PayrollOffice),
            new Organization($"{Marker} אורון טכנולוגיות", OrganizationType.Employer)
        };
        foreach (var organization in organizations) organization.Activate();
        db.Organizations.AddRange(organizations);
        await db.SaveChangesAsync(ct);

        var employers = new[]
        {
            new Employer(organizations[0].Id, "אלפא פתרונות בע״מ", "515100101", "935100101"),
            new Employer(organizations[0].Id, "אלפא לוגיסטיקה בע״מ", "515100102", "935100102"),
            new Employer(organizations[0].Id, "אלפא שירותים פיננסיים בע״מ", "515100103", "935100103"),
            new Employer(organizations[0].Id, "אלפא קמעונאות בע״מ", "515100104", "935100104"),
            new Employer(organizations[1].Id, "נובה מדיה בע״מ", "515200201", "935200201"),
            new Employer(organizations[1].Id, "פסגה הנדסה בע״מ", "515200202", "935200202"),
            new Employer(organizations[1].Id, "ירדן מזון בע״מ", "515200203", "935200203"),
            new Employer(organizations[2].Id, "אורון טכנולוגיות בע״מ", "515300301", "935300301")
        };
        db.Employers.AddRange(employers);
        await db.SaveChangesAsync(ct);

        var admin = new User("demo-dana-admin", "dana.admin@alpha-demo.local", "דנה כהן");
        var payroll = new User("demo-ron-payroll", "ron.payroll@alpha-demo.local", "רון לוי");
        var operations = new User("demo-maya-operations", "maya.operations@alpha-demo.local", "מאיה ישראלי");
        var viewer = new User("demo-noam-viewer", "noam.viewer@alpha-demo.local", "נועם אדרי");
        db.Users.AddRange(admin, payroll, operations, viewer);
        await db.SaveChangesAsync(ct);

        db.OrganizationMemberships.AddRange(
            new OrganizationMembership(admin.Id, organizations[0].Id, OrganizationRole.Admin, EmployerAccessMode.AllEmployers, admin.Id),
            new OrganizationMembership(payroll.Id, organizations[0].Id, OrganizationRole.PayrollManager, EmployerAccessMode.AllEmployers, admin.Id),
            new OrganizationMembership(operations.Id, organizations[0].Id, OrganizationRole.OperationsAgent, EmployerAccessMode.SelectedEmployers, admin.Id),
            new OrganizationMembership(viewer.Id, organizations[0].Id, OrganizationRole.Viewer, EmployerAccessMode.SelectedEmployers, admin.Id),
            new OrganizationMembership(admin.Id, organizations[1].Id, OrganizationRole.Admin, EmployerAccessMode.AllEmployers, admin.Id),
            new OrganizationMembership(payroll.Id, organizations[1].Id, OrganizationRole.PayrollManager, EmployerAccessMode.AllEmployers, admin.Id),
            new OrganizationMembership(admin.Id, organizations[2].Id, OrganizationRole.Admin, EmployerAccessMode.AllEmployers, admin.Id)
        );
        db.EmployerUserAccesses.AddRange(
            new EmployerUserAccess(operations.Id, organizations[0].Id, employers[0].Id, EmployerRole.User),
            new EmployerUserAccess(operations.Id, organizations[0].Id, employers[1].Id, EmployerRole.User),
            new EmployerUserAccess(viewer.Id, organizations[0].Id, employers[2].Id, EmployerRole.Viewer)
        );

        var firstNames = new[] { "יעל", "אורי", "נועה", "איתי", "מאיה", "דניאל", "שירה", "עומר", "רוני", "יובל", "תמר", "אלון" };
        var lastNames = new[] { "כהן", "לוי", "מזרחי", "פרץ", "ביטון", "ישראלי", "אברהם", "דהן", "שחר", "ברק", "מלכה", "רוזן" };
        var employeeCounter = 1;
        var nationalIdCandidate = 300000000;

        foreach (var employer in employers)
        {
            for (var i = 0; i < 7; i++)
            {
                var person = new Person(
                    employer.OrganizationId,
                    NextValidNationalId(ref nationalIdCandidate),
                    firstNames[(employeeCounter + i) % firstNames.Length],
                    lastNames[(employeeCounter * 2 + i) % lastNames.Length]);
                db.People.Add(person);

                var startYear = 2021 + (employeeCounter % 5);
                var startMonth = 1 + (employeeCounter % 12);
                var employment = new Employment(
                    employer.OrganizationId,
                    employer.Id,
                    person.Id,
                    new DateOnly(startYear, startMonth, 1 + (employeeCounter % 20)),
                    $"EMP-{employeeCounter:0000}");
                db.Employments.Add(employment);
                employeeCounter++;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureScopedDemoUsersAsync(AlphaDbContext db, CancellationToken ct)
    {
        var organization = await db.Organizations.FirstOrDefaultAsync(x => x.Name == $"{Marker} קבוצת אלפא", ct);
        if (organization is null) return;

        var employer = await db.Employers
            .Where(x => x.OrganizationId == organization.Id)
            .OrderBy(x => x.LegalName)
            .FirstOrDefaultAsync(ct);
        if (employer is null) return;

        var organizationUser = await db.Users.FirstOrDefaultAsync(x => x.ExternalSubject == OrganizationUserSubject, ct);
        if (organizationUser is null)
        {
            organizationUser = new User(
                OrganizationUserSubject,
                "org.admin@alpha-demo.local",
                "מנהל ארגון בדיקה",
                OrganizationUserNationalId,
                OrganizationUserPhone);
            db.Users.Add(organizationUser);
            await db.SaveChangesAsync(ct);
        }

        var employerUser = await db.Users.FirstOrDefaultAsync(x => x.ExternalSubject == EmployerUserSubject, ct);
        if (employerUser is null)
        {
            employerUser = new User(
                EmployerUserSubject,
                "employer.admin@alpha-demo.local",
                "מנהל מעסיק בדיקה",
                EmployerUserNationalId,
                EmployerUserPhone);
            db.Users.Add(employerUser);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.OrganizationMemberships.AnyAsync(x => x.UserId == organizationUser.Id && x.OrganizationId == organization.Id, ct))
        {
            db.OrganizationMemberships.Add(new OrganizationMembership(
                organizationUser.Id,
                organization.Id,
                OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers,
                organizationUser.Id));
        }

        var employerMembership = await db.OrganizationMemberships
            .SingleOrDefaultAsync(x => x.UserId == employerUser.Id && x.OrganizationId == organization.Id && x.IsActive, ct);
        employerMembership?.Deactivate();

        var employerAccess = await db.EmployerUserAccesses
            .SingleOrDefaultAsync(x => x.UserId == employerUser.Id && x.EmployerId == employer.Id, ct);
        if (employerAccess is null)
        {
            db.EmployerUserAccesses.Add(new EmployerUserAccess(
                employerUser.Id,
                organization.Id,
                employer.Id,
                EmployerRole.Owner));
        }
        else
        {
            employerAccess.ChangeRole(EmployerRole.Owner);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task RepairInvalidEmployeeNationalIdsAsync(AlphaDbContext db, CancellationToken ct)
    {
        var people = await db.People.OrderBy(x => x.OrganizationId).ThenBy(x => x.Id).ToListAsync(ct);
        if (people.Count == 0) return;

        var usedByOrganization = people
            .GroupBy(x => x.OrganizationId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.NationalId).ToHashSet(StringComparer.Ordinal));

        var candidate = 700000000;
        var changed = false;

        foreach (var person in people.Where(x => !IsIsraeliId(x.NationalId)))
        {
            var used = usedByOrganization[person.OrganizationId];
            string replacement;
            do
            {
                replacement = NextValidNationalId(ref candidate);
            }
            while (used.Contains(replacement));

            used.Remove(person.NationalId);
            used.Add(replacement);
            person.Update(replacement, person.FirstName, person.LastName);
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private static string NextValidNationalId(ref int candidate)
    {
        while (candidate <= 999999999)
        {
            var value = candidate++.ToString("000000000");
            if (IsIsraeliId(value)) return value;
        }

        throw new InvalidOperationException("Could not generate a valid Israeli national ID for demo data.");
    }

    private static bool IsIsraeliId(string value)
    {
        if (value.Length is < 1 or > 9 || !value.All(char.IsDigit)) return false;
        var id = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < id.Length; i++)
        {
            var number = (id[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += number > 9 ? number - 9 : number;
        }
        return sum % 10 == 0;
    }
}
