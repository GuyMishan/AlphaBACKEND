using System.Net;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Security;

/// <summary>
/// Handlers that combine bulk deletes (which commit immediately) with a later SaveChanges must be atomic.
/// Failures are injected with triggers created only inside the disposable test database.
/// </summary>
public sealed class DataIntegrityRollbackTests
{
    [Fact]
    public async Task Failed_pension_mix_replacement_keeps_the_previous_mix()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "M");
            var mix = $"/api/organizations/{tenant.Organization.Id}/employers/{tenant.Employer.Id}/employees/{tenant.Employment.Id}/pension-mix/";

            using var initial = await client.SendAsync(Request(HttpMethod.Put, mix, tenant.Admin.Id, tenant.Session.Id,
                json: MixRequest("ORIGINAL-1")), ct);
            Assert.Equal(HttpStatusCode.NoContent, initial.StatusCode);

            await factory.WithDbAsync(db => InjectFailureAsync(db, "employees.employee_pension_products",
                "NEW.\"PolicyNumber\" = 'REPLACEMENT-1'", ct));

            using var failed = await client.SendAsync(Request(HttpMethod.Put, mix, tenant.Admin.Id, tenant.Session.Id,
                json: MixRequest("REPLACEMENT-1")), ct);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

            await factory.WithDbAsync(async db =>
            {
                var products = await db.EmployeePensionProducts.AsNoTracking()
                    .Where(x => x.EmploymentId == tenant.Employment.Id).ToListAsync(ct);
                var product = Assert.Single(products);
                Assert.Equal("ORIGINAL-1", product.PolicyNumber);
                Assert.Equal(2, await db.EmployeePensionContributions.CountAsync(x => x.EmployeePensionProductId == product.Id, ct));
            });
        });
    }

    [Fact]
    public async Task Failed_manual_report_employee_save_keeps_the_previous_report_products()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "D");
            var report = new ManualReport(tenant.Organization.Id, tenant.Employer.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 5));
            var reportEmployee = new ManualReportEmployee(report.Id, tenant.Organization.Id, tenant.Employer.Id,
                tenant.Employment.Id, tenant.Employment.PersonId, "123456782", "First", "Last", tenant.Employment.EmployeeNumber, 10_000m);
            var original = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund, "ORIGINAL-R", new DateOnly(2026, 1, 1),
                10_000m, "1", "1", false, null, "fund-1", "1", "Fund", "Company", SalaryAllocationType.Fixed, 10_000m, 0, 3, "");
            await factory.WithDbAsync(async db =>
            {
                db.AddRange(report, reportEmployee, original);
                await db.SaveChangesAsync(ct);
                await InjectFailureAsync(db, "reporting.manual_report_products", "NEW.\"PolicyNumber\" = 'REPLACEMENT-R'", ct);
            });

            using var failed = await client.SendAsync(Request(HttpMethod.Put,
                $"/api/organizations/{tenant.Organization.Id}/employers/{tenant.Employer.Id}/manual-reports/{report.Id}/employees/{reportEmployee.Id}",
                tenant.Admin.Id, tenant.Session.Id, json: new
                {
                    monthlySalary = 10_000m,
                    products = new[]
                    {
                        new
                        {
                            productType = 1, policyNumber = "REPLACEMENT-R", salaryMonth = new DateOnly(2026, 1, 1), salary = 10_000m,
                            reportingType = "1", salaryLayer = "1", section14 = false, section14StartDate = (DateOnly?)null, section14Code = 3,
                            fundExternalKey = "fund-1", fundCode = "1", fundName = "Fund", fundCompanyName = "Company", fundClassification = "",
                            salaryAllocationType = 1, salaryAllocationValue = 10_000m, allocationOrder = 0,
                            employerContributions = new[] { new { component = 2, amount = 650m, percentage = 6.5m, exemptPayments = 0m } },
                            employeeContributions = new[] { new { component = 2, amount = 600m, percentage = 6m, exemptPayments = 0m } }
                        }
                    }
                }), ct);
            Assert.True(failed.StatusCode == HttpStatusCode.InternalServerError, await failed.Content.ReadAsStringAsync(ct));

            await factory.WithDbAsync(async db =>
            {
                var product = Assert.Single(await db.ManualReportProducts.AsNoTracking()
                    .Where(x => x.ReportEmployeeId == reportEmployee.Id).ToListAsync(ct));
                Assert.Equal("ORIGINAL-R", product.PolicyNumber);
            });
        });
    }

    [Fact]
    public async Task Failed_membership_removal_keeps_membership_grants_and_writes_no_partial_audit()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "T");
            var (member, _) = await SeedUserAsync(factory, "rollback-member", platformAdmin: false);
            await factory.WithDbAsync(async db =>
            {
                db.AddRange(
                    new OrganizationMembership(member.Id, tenant.Organization.Id, OrganizationRole.OperationsAgent,
                        EmployerAccessMode.SelectedEmployers, tenant.Admin.Id),
                    new EmployerUserAccess(member.Id, tenant.Organization.Id, tenant.Employer.Id, EmployerRole.User));
                await db.SaveChangesAsync(ct);
                await InjectFailureAsync(db, "audit.audit_events", "NEW.\"Action\" = 'membership.removed'", ct);
            });

            using var remove = await client.SendAsync(Request(HttpMethod.Delete,
                $"/api/organizations/{tenant.Organization.Id}/access/users/{member.Id}", tenant.Admin.Id, tenant.Session.Id), ct);
            Assert.Equal(HttpStatusCode.InternalServerError, remove.StatusCode);

            await factory.WithDbAsync(async db =>
            {
                Assert.True((await db.OrganizationMemberships.AsNoTracking()
                    .SingleAsync(x => x.UserId == member.Id && x.OrganizationId == tenant.Organization.Id, ct)).IsActive);
                Assert.True(await db.EmployerUserAccesses.AnyAsync(x => x.UserId == member.Id && x.EmployerId == tenant.Employer.Id, ct));
            });
        });
    }

    [Fact]
    public async Task Concurrent_removal_of_the_last_two_admins_leaves_one_admin()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "C");
            var (second, secondSession) = await SeedUserAsync(factory, "second-admin", platformAdmin: false);
            await factory.WithDbAsync(async db =>
            {
                db.Add(new OrganizationMembership(second.Id, tenant.Organization.Id, OrganizationRole.Admin,
                    EmployerAccessMode.AllEmployers, tenant.Admin.Id));
                await db.SaveChangesAsync(ct);
            });

            // Each admin demotes the other at the same time; without serialization both checks see "another admin".
            var demote = new { role = (int)OrganizationRole.Viewer, employerAccessMode = (int)EmployerAccessMode.AllEmployers,
                canCreateEmployer = false, canEditEmployer = false, canCreateEmployee = false, canEditEmployee = false,
                canCreateReport = false, canTransmitReport = false };
            var org = $"/api/organizations/{tenant.Organization.Id}/access/users";
            var responses = await Task.WhenAll(
                client.SendAsync(Request(HttpMethod.Put, $"{org}/{second.Id}", tenant.Admin.Id, tenant.Session.Id, json: demote), ct),
                client.SendAsync(Request(HttpMethod.Put, $"{org}/{tenant.Admin.Id}", second.Id, secondSession.Id, json: demote), ct));
            try
            {
                Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.NoContent);
                await factory.WithDbAsync(async db => Assert.Equal(1, await db.OrganizationMemberships.CountAsync(x =>
                    x.OrganizationId == tenant.Organization.Id && x.IsActive && x.Role == OrganizationRole.Admin, ct)));
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }
        });
    }

    private static object MixRequest(string policyNumber) => new
    {
        monthlySalary = 10_000m,
        products = new[]
        {
            new
            {
                productType = 1, policyNumber, salary = 10_000m, reportingType = "", salaryLayer = "",
                section14 = false, section14Code = 3, section14StartDate = (DateOnly?)null,
                isActive = true, effectiveFrom = new DateOnly(2026, 1, 1), effectiveTo = (DateOnly?)null,
                institutionalBody = "", manufacturer = "", fundExternalKey = "fund-1", fundCode = "1", fundName = "Fund",
                fundCompanyName = "Company", fundClassification = "", salaryAllocationType = 1, salaryAllocationValue = 10_000m,
                allocationOrder = 0,
                employerContributions = new[] { new { component = 2, percentage = 6.5m, amount = (decimal?)null, exemptPayments = (decimal?)null } },
                employeeContributions = new[] { new { component = 2, percentage = 6m, amount = (decimal?)null, exemptPayments = (decimal?)null } }
            }
        }
    };

    private static async Task InjectFailureAsync(AlphaDbContext db, string table, string condition, CancellationToken ct)
    {
        // Identifiers and conditions are compile-time test constants, never user input.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE OR REPLACE FUNCTION public.alpha_test_injected_failure() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN RAISE EXCEPTION 'alpha test injected failure'; END $f$;
            CREATE TRIGGER alpha_test_injected_failure BEFORE INSERT ON {table}
            FOR EACH ROW WHEN ({condition}) EXECUTE FUNCTION public.alpha_test_injected_failure();
            """, ct);
#pragma warning restore EF1002
    }
}
