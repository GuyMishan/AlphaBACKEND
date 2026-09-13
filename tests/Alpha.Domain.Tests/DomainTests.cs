using Alpha.Domain.Employees;
using Alpha.Domain.Organizations;
using Xunit;

namespace Alpha.Domain.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Organization_starts_in_onboarding()
    {
        var organization = new Organization("Alpha customer", OrganizationType.Employer);
        Assert.Equal(OrganizationStatus.Onboarding, organization.Status);
    }

    [Fact]
    public void Employment_cannot_end_before_it_starts()
    {
        var employment = new Employment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 9, 1), "E-1");
        Assert.Throws<ArgumentOutOfRangeException>(() => employment.End(new DateOnly(2026, 8, 31)));
    }

    [Fact]
    public void New_membership_is_valid()
    {
        var membership = new OrganizationMembership(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Viewer,
            EmployerAccessMode.AllEmployers, Guid.NewGuid());
        Assert.True(membership.IsValidAt(DateTimeOffset.UtcNow));
    }
}
