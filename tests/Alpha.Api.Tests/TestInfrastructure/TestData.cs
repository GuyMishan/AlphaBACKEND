using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;

namespace Alpha.Api.Tests.TestInfrastructure;

/// <summary>Synthetic tenants, users and sessions for HTTP tests. No real personal data.</summary>
internal static class TestData
{
    public sealed record Tenant(Organization Organization, User Admin, UserSession Session, Employer Employer, Employment Employment);

    public static async Task<(User User, UserSession Session)> SeedUserAsync(AlphaApiTestHost.Factory factory, string key, bool platformAdmin)
    {
        var user = new User($"test-{key}", $"{key}@test.invalid", $"User {key}");
        if (platformAdmin) user.SetPlatformAdmin(true);
        var session = new UserSession(user.Id, DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        await factory.WithDbAsync(async db =>
        {
            db.AddRange(user, session);
            await db.SaveChangesAsync();
        });
        return (user, session);
    }

    public static async Task<Tenant> SeedTenantAsync(AlphaApiTestHost.Factory factory, string key)
    {
        var (admin, session) = await SeedUserAsync(factory, $"tenant-admin-{key}", platformAdmin: false);
        var organization = new Organization($"Tenant {key}", OrganizationType.RegularOrganization);
        organization.Activate();
        var employer = new Employer(organization.Id, $"Employer {key}", $"1000000{key.Length}", $"20000000{key.Length}");
        var person = NewPerson(organization.Id, key);
        var employment = new Employment(organization.Id, employer.Id, person.Id, new DateOnly(2026, 1, 1), $"EMP-{key}-{Guid.NewGuid():N}"[..20], 10_000m);
        await factory.WithDbAsync(async db =>
        {
            db.AddRange(organization, employer, person, employment,
                new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin, EmployerAccessMode.AllEmployers, admin.Id));
            await db.SaveChangesAsync();
        });
        return new Tenant(organization, admin, session, employer, employment);
    }

    public static Person NewPerson(Guid organizationId, string key)
    {
        var person = new Person(organizationId, string.Empty, $"First{key}", $"Last{key}");
        person.SetProtectedIdentifier(PersonIdentifierType.IsraeliId, $"test-encrypted-{Guid.NewGuid():N}", $"test-hash-{Guid.NewGuid():N}");
        return person;
    }
}
