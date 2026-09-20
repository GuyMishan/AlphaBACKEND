using Alpha.Domain.Employees;
using Alpha.Domain.Organizations;
using Alpha.Domain.Identity;
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
    public void Invitation_is_single_use()
    {
        var invitation = new UserInvitation(
            "invitee@example.com",
            Guid.NewGuid(),
            null,
            OrganizationRole.Viewer,
            null,
            new string('A', 64),
            DateTimeOffset.UtcNow.AddDays(7),
            Guid.NewGuid());

        var userId = Guid.NewGuid();
        invitation.Accept(userId);

        Assert.Equal(UserInvitationStatus.Accepted, invitation.Status);
        Assert.Equal(userId, invitation.AcceptedByUserId);
        Assert.False(invitation.IsUsableAt(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => invitation.Accept(Guid.NewGuid()));
    }

    [Fact]
    public void Cancelled_invitation_is_not_usable()
    {
        var invitation = new UserInvitation(
            "invitee@example.com",
            Guid.NewGuid(),
            null,
            OrganizationRole.Viewer,
            null,
            new string('B', 64),
            DateTimeOffset.UtcNow.AddDays(7),
            Guid.NewGuid());

        invitation.Cancel();

        Assert.Equal(UserInvitationStatus.Cancelled, invitation.Status);
        Assert.False(invitation.IsUsableAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void New_membership_is_valid()
    {
        var membership = new OrganizationMembership(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Viewer,
            EmployerAccessMode.AllEmployers, Guid.NewGuid());
        Assert.True(membership.IsValidAt(DateTimeOffset.UtcNow));
    }
}
