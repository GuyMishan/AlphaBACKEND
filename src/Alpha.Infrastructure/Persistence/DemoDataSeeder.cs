using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class DemoDataSeeder
{
    private const string Marker = "[DEMO]";

    public static async Task SeedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        if (await db.Organizations.AsNoTracking().AnyAsync(x => x.Name.StartsWith(Marker), ct))
            return;

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
            new EmployerUserAccess(operations.Id, organizations[0].Id, employers[0].Id),
            new EmployerUserAccess(operations.Id, organizations[0].Id, employers[1].Id),
            new EmployerUserAccess(viewer.Id, organizations[0].Id, employers[2].Id)
        );

        var firstNames = new[] { "יעל", "אורי", "נועה", "איתי", "מאיה", "דניאל", "שירה", "עומר", "רוני", "יובל", "תמר", "אלון" };
        var lastNames = new[] { "כהן", "לוי", "מזרחי", "פרץ", "ביטון", "ישראלי", "אברהם", "דהן", "שחר", "ברק", "מלכה", "רוזן" };
        var employeeCounter = 1;
        var nationalIdCounter = 200000001;

        foreach (var employer in employers)
        {
            for (var i = 0; i < 7; i++)
            {
                var person = new Person(
                    employer.OrganizationId,
                    (nationalIdCounter++).ToString(),
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
}
