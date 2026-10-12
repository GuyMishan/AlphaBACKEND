using System.Net;
using Alpha.Api.Tests.TestInfrastructure;
using Alpha.Domain.Billing;
using Alpha.Domain.Organizations;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Security;

/// <summary>
/// Invitation emails carry a live invitation token, so the link must point at the configured frontend and never at a
/// host taken from request headers (<c>Origin</c> and <c>Host</c> are caller-controlled).
/// </summary>
public sealed class InvitationLinkTests
{
    [Fact]
    public async Task Invitation_link_ignores_a_caller_supplied_origin()
    {
        var mail = new RecordingMailGateway();
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "invite-link");
            await factory.WithDbAsync(async db =>
            {
                // A paid pricing component lifts the free plan's one-user limit.
                var account = new BillingAccount(tenant.Organization.Id, null);
                db.AddRange(account, new BillingAccountPricingComponent(account.Id, BillingMetricType.Employee, BillingPricingType.PerUnit, 10m));
                await db.SaveChangesAsync(ct);
            });
            using var request = Request(HttpMethod.Post, $"/api/organizations/{tenant.Organization.Id}/invitations",
                tenant.Admin.Id, tenant.Session.Id, json: new
                {
                    email = "invitee@test.invalid", nationalId = "000000018", phone = "0541112222",
                    employerId = (Guid?)null, organizationRole = OrganizationRole.Viewer
                });
            request.Headers.Add("Origin", "https://attacker.example");

            using var response = await client.SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = Assert.Single(mail.Bodies);
            Assert.DoesNotContain("attacker.example", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/invite?token=", body, StringComparison.Ordinal);
        }, RecordingMailGateway.Settings, mail.Install);
    }
}
