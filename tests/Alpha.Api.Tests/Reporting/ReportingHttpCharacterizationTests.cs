using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Validation;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.ReferenceData.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

public sealed class ReportingHttpCharacterizationTests
{
    [Fact]
    public void Moved_employee_profile_validation_preserves_every_legacy_error_contract()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var validBirthDate = new DateOnly(1990, 1, 2);
        var scenarios = new (DateOnly? BirthDate, PersonGender? Gender, string? Email, string? Mobile,
            string? City, string? Street, string? House, string? Postal, string? Box)[]
        {
            (null, null, null, null, null, null, null, null, null),
            (future, PersonGender.Female, "a@b.co", "0501234567", "City", "Street", "1", "12345", ""),
            (validBirthDate, null, "a@b.co", "0501234567", "City", "Street", "1", "12345", ""),
            (validBirthDate, PersonGender.Female, "bad", "0501234567", "City", "Street", "1", "12345", ""),
            (validBirthDate, PersonGender.Female, "a@b.co", "1", "City", "Street", "1", "12345", ""),
            (validBirthDate, PersonGender.Female, "a@b.co", "0501234567", "City", "Street", "1", "postal", ""),
            (validBirthDate, PersonGender.Female, "a@b.co", "0501234567", "", "", "", "", "100000"),
            (validBirthDate, PersonGender.Female, "a@b.co", "0501234567", "", "", "", "", "")
        };

        foreach (var scenario in scenarios)
        {
            var legacy = WorkforceInputValidation.EmployeeInterfaceProfile(scenario.BirthDate, scenario.Gender,
                scenario.Email, scenario.Mobile, scenario.City, scenario.Street, scenario.House,
                scenario.Postal, scenario.Box);
            var moved = EmployerInterfaceProfileValidation.Validate(scenario.BirthDate, scenario.Gender,
                scenario.Email, scenario.Mobile, scenario.City, scenario.Street, scenario.House,
                scenario.Postal, scenario.Box);
            Assert.Equal(legacy, moved);
        }
    }

    [Fact]
    public async Task Reporting_profiles_and_product_references_preserve_permissions_tenant_scope_and_draft_snapshot_sync()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new ReportingWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a73-admin", "a73-admin@test.invalid", "A73 Admin");
            var viewer = new User("a73-viewer", "a73-viewer@test.invalid", "A73 Viewer");
            var outsider = new User("a73-outsider", "a73-outsider@test.invalid", "A73 Outsider");
            var organization = new Organization("A73 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A73 Other", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var employer = new Employer(organization.Id, "A73 Employer", "510000027", "930000027",
                "Old", "Contact", "031111111", "old@test.invalid", "0501111111");
            var person = new Person(organization.Id, "123456782", "Dana", "Profile", new DateOnly(1990, 1, 2),
                PersonGender.Female, "old.employee@test.invalid", "0502222222", "Tel Aviv", "Main", "10", "2", "61000", "");
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2020, 1, 1), "A73-1", 12_000m);
            var draft = new ManualReport(organization.Id, employer.Id,
                new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), DateOnly.FromDateTime(DateTime.UtcNow));
            var historical = new ManualReport(organization.Id, employer.Id,
                new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), DateOnly.FromDateTime(DateTime.UtcNow));
            historical.MarkValidated();
            var draftEmployee = Snapshot(draft.Id);
            var historicalEmployee = Snapshot(historical.Id);
            var product = new ManualReportProduct(draftEmployee.Id, PensionProductType.PensionFund, "A73-POLICY",
                draft.ReportingMonth, 12_000m, "1", "1", false, null, fundName: "A73 Fund");

            ManualReportEmployee Snapshot(Guid reportId)
            {
                var snapshot = new ManualReportEmployee(reportId, organization.Id, employer.Id, employment.Id,
                    person.Id, "123456782", person.FirstName, person.LastName, employment.EmployeeNumber, employment.MonthlySalary);
                snapshot.SetInterfaceSnapshot(1, "123456782", person.BirthDate, (int?)person.Gender,
                    person.Email, person.Mobile, person.City, person.Street, person.HouseNumber, person.Apartment,
                    person.PostalCode, person.PostOfficeBox, employment.StartDate);
                return snapshot;
            }

            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var viewerMembership = new OrganizationMembership(viewer.Id, organization.Id, OrganizationRole.Viewer,
                EmployerAccessMode.AllEmployers, admin.Id);
            var outsiderMembership = new OrganizationMembership(outsider.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, outsider.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var viewerSession = new UserSession(viewer.Id, now, now.AddHours(1));
            var outsiderSession = new UserSession(outsider.Id, now, now.AddHours(1));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, viewer, outsider, organization, otherOrganization, employer, person, employment,
                    draft, historical, draftEmployee, historicalEmployee, product,
                    adminMembership, viewerMembership, outsiderMembership,
                    adminSession, viewerSession, outsiderSession);
                await db.SaveChangesAsync(ct);
            }

            var root = $"/api/organizations/{organization.Id}/employers/{employer.Id}/employer-interface";
            using var anonymous = await client.GetAsync($"{root}/profile", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var viewerReadRequest = NewRequest(HttpMethod.Get, $"{root}/profile", viewer.Id, viewerSession.Id);
            using var viewerRead = await client.SendAsync(viewerReadRequest, ct);
            Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);
            var employerProfile = await viewerRead.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("Old", employerProfile.GetProperty("contactFirstName").GetString());

            using var viewerWriteRequest = NewRequest(HttpMethod.Put, $"{root}/profile", viewer.Id, viewerSession.Id);
            viewerWriteRequest.Content = JsonContent.Create(new
            {
                contactFirstName = "Denied", contactLastName = "Viewer", contactPhone = "031111111",
                contactEmail = "viewer@test.invalid", contactMobile = "0501111111"
            });
            using var viewerWrite = await client.SendAsync(viewerWriteRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, viewerWrite.StatusCode);

            using var crossTenantRequest = NewRequest(HttpMethod.Get, $"{root}/profile",
                outsider.Id, outsiderSession.Id);
            using var crossTenant = await client.SendAsync(crossTenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

            using var employerUpdateRequest = NewRequest(HttpMethod.Put, $"{root}/profile", admin.Id, adminSession.Id);
            employerUpdateRequest.Content = JsonContent.Create(new
            {
                contactFirstName = "New", contactLastName = "Contact", contactPhone = "032222222",
                contactEmail = "new@test.invalid", contactMobile = "0503333333"
            });
            using var employerUpdated = await client.SendAsync(employerUpdateRequest, ct);
            Assert.Equal(HttpStatusCode.OK, employerUpdated.StatusCode);

            var employeePath = $"{root}/employees/{employment.Id}/profile";
            using var invalidEmployeeRequest = NewRequest(HttpMethod.Put, employeePath, admin.Id, adminSession.Id);
            invalidEmployeeRequest.Content = JsonContent.Create(new
            {
                birthDate = (DateOnly?)null, gender = (int?)null, email = "bad", mobile = "1",
                city = "", street = "", houseNumber = "", apartment = "", postalCode = "", postOfficeBox = ""
            });
            using var invalidEmployee = await client.SendAsync(invalidEmployeeRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalidEmployee.StatusCode);
            Assert.True((await invalidEmployee.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
                .TryGetProperty("error", out _));

            using var employeeUpdateRequest = NewRequest(HttpMethod.Put, employeePath, admin.Id, adminSession.Id);
            employeeUpdateRequest.Content = JsonContent.Create(new
            {
                birthDate = new DateOnly(1990, 1, 2), gender = (int)PersonGender.Female,
                email = "new.employee@test.invalid", mobile = "0504444444", city = "Haifa", street = "Port",
                houseNumber = "20", apartment = "3", postalCode = "62000", postOfficeBox = ""
            });
            using var employeeUpdated = await client.SendAsync(employeeUpdateRequest, ct);
            Assert.Equal(HttpStatusCode.OK, employeeUpdated.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var draftSnapshot = await db.ManualReportEmployees.AsNoTracking()
                    .SingleAsync(x => x.Id == draftEmployee.Id, ct);
                var historicalSnapshot = await db.ManualReportEmployees.AsNoTracking()
                    .SingleAsync(x => x.Id == historicalEmployee.Id, ct);
                Assert.Equal("62000", draftSnapshot.PostalCodeSnapshot);
                Assert.Equal("61000", historicalSnapshot.PostalCodeSnapshot);
            }

            var metadataPath = $"{root}/reports/{draft.Id}/products/{product.Id}/metadata";
            using var viewerMetadataWriteRequest = NewRequest(HttpMethod.Put, metadataPath, viewer.Id, viewerSession.Id);
            viewerMetadataWriteRequest.Content = JsonContent.Create(new { operationCode = 1 });
            using var viewerMetadataWrite = await client.SendAsync(viewerMetadataWriteRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, viewerMetadataWrite.StatusCode);

            using var metadataUpdateRequest = NewRequest(HttpMethod.Put, metadataPath, admin.Id, adminSession.Id);
            metadataUpdateRequest.Content = JsonContent.Create(new
            {
                operationCode = 1, depositStatus = 1, employeeStatus = 1,
                statusStartDate = employment.StartDate, employmentPercentage = 100m, workDaysInMonth = 22,
                lastDeposit = 2, refundReason = (int?)null, paymentMethodCode = 1,
                employerAccountType = 1, receiverAccountType = 1, oldPensionTypeCode = (int?)null,
                previousIdentifier = (string?)null, previousClearingIdentifier = (string?)null,
                previousReferenceExceptionCode = (int?)null
            });
            using var metadataUpdated = await client.SendAsync(metadataUpdateRequest, ct);
            Assert.Equal(HttpStatusCode.OK, metadataUpdated.StatusCode);
            var metadata = await metadataUpdated.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(1, metadata.GetProperty("operationCode").GetInt32());

            var previousPath = $"{root}/reports/{draft.Id}/products/{product.Id}/previous-reference";
            var previousIdentifier = Guid.NewGuid().ToString("D");
            var previousClearingIdentifier = Guid.NewGuid().ToString("D");
            using var previousUpdateRequest = NewRequest(HttpMethod.Put, previousPath, admin.Id, adminSession.Id);
            previousUpdateRequest.Content = JsonContent.Create(new
            {
                previousIdentifier, previousClearingIdentifier, previousReferenceExceptionCode = 1
            });
            using var previousUpdated = await client.SendAsync(previousUpdateRequest, ct);
            Assert.Equal(HttpStatusCode.OK, previousUpdated.StatusCode);

            using var previousReadRequest = NewRequest(HttpMethod.Get, previousPath, viewer.Id, viewerSession.Id);
            using var previousRead = await client.SendAsync(previousReadRequest, ct);
            Assert.Equal(HttpStatusCode.OK, previousRead.StatusCode);
            var previous = await previousRead.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(previousIdentifier.ToUpperInvariant(), previous.GetProperty("previousIdentifier").GetString());
            Assert.Equal(previousClearingIdentifier.ToUpperInvariant(),
                previous.GetProperty("previousClearingIdentifier").GetString());

            using var crossProductRequest = NewRequest(HttpMethod.Get, metadataPath, outsider.Id, outsiderSession.Id);
            using var crossProduct = await client.SendAsync(crossProductRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossProduct.StatusCode);
        });
    }

    [Fact]
    public async Task Derived_reports_and_corrections_preserve_http_contract_tenant_scope_lineage_and_audit()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new ReportingWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a76-admin", "a76-admin@test.invalid", "A76 Admin");
            var viewer = new User("a76-viewer", "a76-viewer@test.invalid", "A76 Viewer");
            var outsider = new User("a76-outsider", "a76-outsider@test.invalid", "A76 Outsider");
            var organization = new Organization("A76 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A76 Other", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var employer = new Employer(organization.Id, "A76 Employer", "510000076", "930000076",
                "A76", "Contact", "031234567", "a76@test.invalid", "0501234567");
            var person = new Person(organization.Id, "123456782", "Dana", "Correction",
                new DateOnly(1990, 1, 2), PersonGender.Female, "dana@test.invalid", "0507654321");
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2020, 1, 1), "A76-1", 12_000m);
            var paymentAccount = new EmployerPaymentAccount(organization.Id, employer.Id, 12, 345,
                "123456789", employer.LegalName, employer.RegistrationNumber);
            var source = new ManualReport(organization.Id, employer.Id, new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 9));
            var protector = factory.Services.GetRequiredService<IDataProtectionService>();

            person.SetProtectedNationalId(
                protector.Protect("123456782", "person-national-id"),
                protector.LookupHash("123456782", "person-national-id-lookup"));
            paymentAccount.SetProtectedValues(
                protector.Protect("123456789", "bank-account-number"),
                protector.LookupHash("123456789", "bank-account-number-lookup"),
                protector.Protect(employer.RegistrationNumber, "bank-account-holder-id"));
            source.SetEmployerInterfaceSnapshot(employer.LegalName, employer.RegistrationNumber,
                employer.WithholdingFileNumber, employer.ContactFirstName, employer.ContactLastName,
                employer.ContactPhone, employer.ContactEmail, employer.ContactMobile, 1, 1);
            source.SetProtectedEmployerSnapshot(
                protector.Protect(employer.RegistrationNumber, $"report-employer-registration:{source.Id}"),
                protector.Protect(employer.WithholdingFileNumber, $"report-employer-withholding:{source.Id}"),
                protector.Protect(employer.ContactPhone, $"report-employer-phone:{source.Id}"),
                protector.Protect(employer.ContactEmail, $"report-employer-email:{source.Id}"),
                protector.Protect(employer.ContactMobile, $"report-employer-mobile:{source.Id}"));
            var reportEmployee = new ManualReportEmployee(source.Id, organization.Id, employer.Id,
                employment.Id, person.Id, "123456782", person.FirstName, person.LastName,
                employment.EmployeeNumber, employment.MonthlySalary);
            reportEmployee.SetInterfaceSnapshot(1, "123456782", person.BirthDate, (int?)person.Gender,
                person.Email, person.Mobile, "Tel Aviv", "Main", "10", "2", "61000", "", employment.StartDate);
            reportEmployee.SetProtectedIdentifiers(
                protector.Protect("123456782", $"report-employee-national-id:{reportEmployee.Id}"),
                protector.LookupHash("123456782", "report-employee-national-id-lookup"),
                protector.Protect("123456782", $"report-employee-interface-id:{reportEmployee.Id}"));
            reportEmployee.SetProtectedContactSnapshot(
                protector.Protect(person.Email, $"report-employee-email:{reportEmployee.Id}"),
                protector.Protect(person.Mobile, $"report-employee-mobile:{reportEmployee.Id}"));
            var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund,
                "A76-POLICY", source.ReportingMonth, 12_000m, "1", "1", false, null,
                "a76-fund", "076", "A76 Fund", "A76 Company", SalaryAllocationType.Fixed, 12_000m, 0, 3);
            var contribution = new ManualContribution(product.Id, ContributionParty.Employee,
                ContributionComponent.Benefits, 720m, 6m, 0m);
            contribution.SetInterfaceRecordIdentifier(Guid.NewGuid().ToString("D"));
            var payment = new ManualReportPayment(product.Id);
            payment.Update("A76 Fund", "12-345-123456789", "bank", new DateOnly(2026, 9, 10), null,
                "A76-REF", "A76 Bank", "12", "345",
                protector.Protect("123456789", $"report-payment-account:{product.Id}"), "");
            var metadata = new EmployerInterfaceReportProductData(product.Id);
            metadata.Update(1, 1, 1, employment.StartDate, 100m, 22, 2, null, 1, 1, 1);
            metadata.SetInterfaceTransferIdentifier(Guid.NewGuid().ToString("D").ToUpperInvariant());
            source.MarkReadyForValidation();
            source.MarkValidated();
            source.MarkTransmissionStarted();
            source.MarkSent();

            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var viewerMembership = new OrganizationMembership(viewer.Id, organization.Id, OrganizationRole.Viewer,
                EmployerAccessMode.AllEmployers, admin.Id);
            var outsiderMembership = new OrganizationMembership(outsider.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, outsider.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var viewerSession = new UserSession(viewer.Id, now, now.AddHours(1));
            var outsiderSession = new UserSession(outsider.Id, now, now.AddHours(1));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, viewer, outsider, organization, otherOrganization, employer, person, employment,
                    paymentAccount, source, reportEmployee, product, contribution, payment, metadata,
                    adminMembership, viewerMembership, outsiderMembership,
                    adminSession, viewerSession, outsiderSession);
                await db.SaveChangesAsync(ct);
            }

            var root = $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports";
            using var anonymous = await client.GetAsync($"{root}/source-reports", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var viewerCreateRequest = NewRequest(HttpMethod.Post,
                $"{root}/{source.Id}/correction-workspace", viewer.Id, viewerSession.Id);
            viewerCreateRequest.Content = JsonContent.Create(new { sourceReportProductId = (Guid?)null });
            using var viewerCreate = await client.SendAsync(viewerCreateRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, viewerCreate.StatusCode);

            using var crossTenantRequest = NewRequest(HttpMethod.Get, $"{root}/source-reports?skip=0&take=30",
                outsider.Id, outsiderSession.Id);
            using var crossTenant = await client.SendAsync(crossTenantRequest, ct);
            Assert.True(crossTenant.StatusCode == HttpStatusCode.Forbidden,
                $"Expected 403 but received {(int)crossTenant.StatusCode}: {await crossTenant.Content.ReadAsStringAsync(ct)}");

            using var listRequest = NewRequest(HttpMethod.Get, $"{root}/source-reports?skip=0&take=30",
                viewer.Id, viewerSession.Id);
            using var list = await client.SendAsync(listRequest, ct);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listJson = await list.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Contains(listJson.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == source.Id);

            using var metadataRequest = NewRequest(HttpMethod.Get, $"{root}/{source.Id}/metadata",
                viewer.Id, viewerSession.Id);
            using var metadataResponse = await client.SendAsync(metadataRequest, ct);
            Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
            var metadataJson = await metadataResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(source.Id, metadataJson.GetProperty("id").GetGuid());
            Assert.Equal((int)ManualReportStatus.Sent, metadataJson.GetProperty("status").GetInt32());

            using var derivedRequest = NewRequest(HttpMethod.Post, $"{root}/derived", admin.Id, adminSession.Id);
            derivedRequest.Content = JsonContent.Create(new
            {
                sourceReportId = source.Id,
                reportKind = (int)ManualReportKind.Differences,
                reportingMonth = source.ReportingMonth,
                salaryPaymentDate = source.SalaryPaymentDate,
                paymentAccountId = paymentAccount.Id,
                correctionOperationCode = (int?)null,
                reportProductIds = new[] { product.Id }
            });
            using var derived = await client.SendAsync(derivedRequest, ct);
            Assert.True(derived.StatusCode == HttpStatusCode.Created,
                $"Expected 201 but received {(int)derived.StatusCode}: {await derived.Content.ReadAsStringAsync(ct)}");
            var derivedJson = await derived.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(source.Id, derivedJson.GetProperty("sourceReportId").GetGuid());
            Assert.Equal((int)ManualReportKind.Differences, derivedJson.GetProperty("reportKind").GetInt32());
            Assert.Equal(1, derivedJson.GetProperty("employeeCount").GetInt32());

            async Task<JsonElement> CreateWorkspaceAsync()
            {
                using var request = NewRequest(HttpMethod.Post, $"{root}/{source.Id}/correction-workspace",
                    admin.Id, adminSession.Id);
                request.Content = JsonContent.Create(new { sourceReportProductId = (Guid?)null });
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            }

            var workspaceResponses = await Task.WhenAll(CreateWorkspaceAsync(), CreateWorkspaceAsync());
            var workspaceId = workspaceResponses[0].GetProperty("reportId").GetGuid();
            Assert.Equal(workspaceId, workspaceResponses[1].GetProperty("reportId").GetGuid());
            Assert.Single(workspaceResponses, x => x.GetProperty("created").GetBoolean());

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var workspaceEmployeeId = await db.ManualReportEmployees
                    .Where(x => x.ReportId == workspaceId).Select(x => x.Id).SingleAsync(ct);
                var workspaceProductId = await db.ManualReportProducts
                    .Where(x => x.ReportEmployeeId == workspaceEmployeeId).Select(x => x.Id).SingleAsync(ct);
                var workspaceContribution = await db.ManualContributions.SingleAsync(
                    x => x.ReportProductId == workspaceProductId, ct);
                workspaceContribution.Update(840m, 7m, 0m);
                await db.SaveChangesAsync(ct);
            }

            using var materializeRequest = NewRequest(HttpMethod.Post,
                $"{root}/{workspaceId}/materialize-correction", admin.Id, adminSession.Id);
            using var materialized = await client.SendAsync(materializeRequest, ct);
            Assert.Equal(HttpStatusCode.OK, materialized.StatusCode);
            Assert.True(materialized.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
            var correlationId = Assert.Single(correlationValues);
            var materializedJson = await materialized.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(workspaceId, materializedJson.GetProperty("workspaceId").GetGuid());
            Assert.Equal(source.Id, materializedJson.GetProperty("sourceReportId").GetGuid());
            Assert.Equal(1, materializedJson.GetProperty("pendingChanges").GetInt32());
            Assert.Equal(2, materializedJson.GetProperty("revisionNumber").GetInt32());

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var persistedSource = await db.ManualReports.AsNoTracking().SingleAsync(x => x.Id == source.Id, ct);
                Assert.Equal(ManualReportStatus.Sent, persistedSource.Status);
                var sourceAmount = await db.ManualContributions.AsNoTracking()
                    .Where(x => x.ReportProductId == product.Id).Select(x => x.Amount).SingleAsync(ct);
                Assert.Equal(720m, sourceAmount);
                Assert.Equal(2, await db.ManualReports.CountAsync(
                    x => x.CorrectionWorkspaceId == workspaceId && x.IsTechnicalCorrectionDocument, ct));
                Assert.Contains(await db.AuditEvents.AsNoTracking().ToListAsync(ct), x =>
                    x.Action == "report-correction.materialized" && x.EntityId != Guid.Empty &&
                    x.OrganizationId == organization.Id && x.EmployerId == employer.Id &&
                    x.CorrelationId == correlationId);
            }
        });
    }

    [Fact]
    public async Task Manual_reports_preserve_crud_validation_encrypted_snapshots_tenant_scope_and_audit()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new ReportingWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a72-admin", "a72-admin@test.invalid", "A72 Admin");
            var outsider = new User("a72-outsider", "a72-outsider@test.invalid", "A72 Outsider");
            var organization = new Organization("A72 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A72 Other", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var employer = new Employer(organization.Id, "A72 Employer", "510000017", "930000017",
                "A", "Admin", "031234567", "reports@test.invalid", "0501234567");
            var person = new Person(organization.Id, "123456782", "Dana", "Report", new DateOnly(1990, 1, 2),
                PersonGender.Female, "dana@test.invalid", "0507654321", "Tel Aviv", "Main", "10", "2", "61000", "");
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2020, 1, 1), "A72-1", 12_000m);
            var account = new EmployerPaymentAccount(organization.Id, employer.Id, 12, 345,
                "123456789", "A72 Employer", "510000017");
            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var outsiderMembership = new OrganizationMembership(outsider.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, outsider.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var outsiderSession = new UserSession(outsider.Id, now, now.AddHours(1));

            var protector = factory.Services.GetRequiredService<IDataProtectionService>();
            person.SetProtectedNationalId(
                protector.Protect("123456782", "person-national-id"),
                protector.LookupHash("123456782", "person-national-id-lookup"));
            account.SetProtectedValues(
                protector.Protect("123456789", "bank-account-number"),
                protector.LookupHash("123456789", "bank-account-number-lookup"),
                protector.Protect("510000017", "bank-account-holder-id"));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, outsider, organization, otherOrganization, employer, person, employment, account,
                    adminMembership, outsiderMembership, adminSession, outsiderSession);
                await db.SaveChangesAsync(ct);
            }

            var path = $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports/";
            var reportMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

            using var invalidRequest = NewRequest(HttpMethod.Post, path, admin.Id, adminSession.Id);
            invalidRequest.Content = JsonContent.Create(new
            {
                reportingMonth = reportMonth,
                salaryPaymentDate = (DateOnly?)null,
                employmentIds = Array.Empty<Guid>(),
                paymentAccountId = account.Id
            });
            using var invalid = await client.SendAsync(invalidRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var invalidJson = await invalid.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            // A12.3 unified error contract: field-independent validation messages are under errors.request.
            Assert.Equal("VALIDATION_FAILED", invalidJson.GetProperty("errorCode").GetString());
            Assert.Equal(JsonValueKind.Array, invalidJson.GetProperty("errors").GetProperty("request").ValueKind);

            using var createRequest = NewRequest(HttpMethod.Post, path, admin.Id, adminSession.Id);
            createRequest.Content = JsonContent.Create(new
            {
                reportingMonth = reportMonth,
                salaryPaymentDate = reportMonth.AddDays(8),
                employmentIds = new[] { employment.Id },
                paymentAccountId = account.Id
            });
            using var created = await client.SendAsync(createRequest, ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created,
                $"Expected 201 but received {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync(ct)}");
            var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var reportId = createdJson.GetProperty("id").GetGuid();
            Assert.Equal(1, createdJson.GetProperty("employeeCount").GetInt32());
            Assert.Equal(account.Id, createdJson.GetProperty("paymentAccountId").GetGuid());

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var report = await db.ManualReports.AsNoTracking().SingleAsync(x => x.Id == reportId, ct);
                var snapshot = await db.ManualReportEmployees.AsNoTracking()
                    .SingleAsync(x => x.ReportId == reportId, ct);
                Assert.NotEqual("510000017", report.EmployerRegistrationNumberSnapshot);
                Assert.Equal("510000017", protector.Unprotect(report.EmployerRegistrationNumberSnapshot,
                    $"report-employer-registration:{report.Id}"));
                Assert.NotEqual("123456782", snapshot.NationalId);
                Assert.Equal("123456782", protector.Unprotect(snapshot.NationalId,
                    $"report-employee-national-id:{snapshot.Id}"));
                Assert.NotEqual("dana@test.invalid", snapshot.EmailSnapshot);
                Assert.Equal("dana@test.invalid", protector.Unprotect(snapshot.EmailSnapshot,
                    $"report-employee-email:{snapshot.Id}"));
            }

            using var outsiderReadRequest = NewRequest(HttpMethod.Get, $"{path}{reportId}",
                outsider.Id, outsiderSession.Id);
            using var outsiderRead = await client.SendAsync(outsiderReadRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, outsiderRead.StatusCode);

            using var updateRequest = NewRequest(HttpMethod.Put, $"{path}{reportId}/details",
                admin.Id, adminSession.Id);
            updateRequest.Content = JsonContent.Create(new
            {
                reportingMonth = reportMonth,
                salaryPaymentDate = reportMonth.AddDays(9)
            });
            using var updated = await client.SendAsync(updateRequest, ct);
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

            using var deleteRequest = NewRequest(HttpMethod.Delete, $"{path}{reportId}",
                admin.Id, adminSession.Id);
            using var deleted = await client.SendAsync(deleteRequest, ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.True(deleted.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
            var correlationId = Assert.Single(correlationValues);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                Assert.False(await db.ManualReports.AnyAsync(x => x.Id == reportId, ct));
                Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "manual-report.deleted"
                    && x.EntityId == reportId && x.OrganizationId == organization.Id
                    && x.EmployerId == employer.Id && x.CorrelationId == correlationId, ct));
            }
        });
    }

    [Fact]
    public async Task Reporting_routes_preserve_authentication_authorization_tenant_scope_and_json_contracts()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new ReportingWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a7-admin", "a7-admin@test.invalid", "A7 Admin");
            var viewer = new User("a7-viewer", "a7-viewer@test.invalid", "A7 Viewer");
            var otherAdmin = new User("a7-other", "a7-other@test.invalid", "A7 Other Admin");
            var organization = new Organization("A7 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A7 Other Organization", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var employer = new Employer(organization.Id, "A7 Employer", "510000007", "930000007");
            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var viewerMembership = new OrganizationMembership(viewer.Id, organization.Id, OrganizationRole.Viewer,
                EmployerAccessMode.AllEmployers, admin.Id);
            var otherMembership = new OrganizationMembership(otherAdmin.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, otherAdmin.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var viewerSession = new UserSession(viewer.Id, now, now.AddHours(1));
            var otherSession = new UserSession(otherAdmin.Id, now, now.AddHours(1));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, viewer, otherAdmin, organization, otherOrganization, employer,
                    adminMembership, viewerMembership, otherMembership, adminSession, viewerSession, otherSession);
                await db.SaveChangesAsync(ct);
            }

            var manualReportsPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports/";
            var manualReportsListPath = manualReportsPath + "?skip=0&take=30";
            using var anonymous = await client.GetAsync(manualReportsListPath, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var ownRequest = NewRequest(HttpMethod.Get, manualReportsListPath, admin.Id, adminSession.Id);
            using var own = await client.SendAsync(ownRequest, ct);
            Assert.True(own.StatusCode == HttpStatusCode.OK,
                $"Expected 200 but received {(int)own.StatusCode}: {await own.Content.ReadAsStringAsync(ct)}");
            var manualReports = await own.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(JsonValueKind.Array, manualReports.GetProperty("items").ValueKind);
            Assert.Empty(manualReports.GetProperty("items").EnumerateArray());
            Assert.False(manualReports.GetProperty("hasMore").GetBoolean());

            using var viewerReadRequest = NewRequest(HttpMethod.Get, manualReportsListPath, viewer.Id, viewerSession.Id);
            using var viewerRead = await client.SendAsync(viewerReadRequest, ct);
            Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);

            using var crossTenantRequest = NewRequest(HttpMethod.Get, manualReportsListPath,
                otherAdmin.Id, otherSession.Id);
            using var crossTenant = await client.SendAsync(crossTenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

            var salaryDate = DateOnly.FromDateTime(DateTime.UtcNow);
            var reportMonth = new DateOnly(salaryDate.Year, salaryDate.Month, 1);
            using var deniedCreateRequest = NewRequest(HttpMethod.Post, manualReportsPath,
                viewer.Id, viewerSession.Id);
            deniedCreateRequest.Content = JsonContent.Create(new
            {
                reportingMonth = reportMonth,
                salaryPaymentDate = salaryDate,
                employmentIds = Array.Empty<Guid>(),
                paymentAccountId = (Guid?)null
            });
            using var deniedCreate = await client.SendAsync(deniedCreateRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);

            using var authorizedCreateRequest = NewRequest(HttpMethod.Post, manualReportsPath,
                admin.Id, adminSession.Id);
            authorizedCreateRequest.Content = JsonContent.Create(new
            {
                reportingMonth = reportMonth,
                salaryPaymentDate = salaryDate,
                employmentIds = Array.Empty<Guid>(),
                paymentAccountId = (Guid?)null
            });
            using var authorizedCreate = await client.SendAsync(authorizedCreateRequest, ct);
            Assert.Equal(HttpStatusCode.Conflict, authorizedCreate.StatusCode);
            var createError = await authorizedCreate.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("payment_account_required", createError.GetProperty("error").GetString());

            var feedbackPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/report-feedback/?skip=0&take=50";
            using var feedbackRequest = NewRequest(HttpMethod.Get, feedbackPath, admin.Id, adminSession.Id);
            using var feedback = await client.SendAsync(feedbackRequest, ct);
            Assert.Equal(HttpStatusCode.OK, feedback.StatusCode);
            var feedbackJson = await feedback.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Empty(feedbackJson.GetProperty("items").EnumerateArray());
            Assert.False(feedbackJson.GetProperty("hasMore").GetBoolean());
            var summary = feedbackJson.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("total").GetInt32());
            Assert.Equal(0, summary.GetProperty("completed").GetInt32());
            Assert.Equal(0, summary.GetProperty("attention").GetInt32());
            Assert.Equal(0, summary.GetProperty("pending").GetInt32());

            var schemaPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/employer-interface/schemas/status";
            using var anonymousSchema = await client.GetAsync(schemaPath, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousSchema.StatusCode);

            using var crossTenantSchemaRequest = NewRequest(HttpMethod.Get, schemaPath,
                otherAdmin.Id, otherSession.Id);
            using var crossTenantSchema = await client.SendAsync(crossTenantSchemaRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenantSchema.StatusCode);

            using var schemaRequest = NewRequest(HttpMethod.Get, schemaPath, admin.Id, adminSession.Id);
            using var schema = await client.SendAsync(schemaRequest, ct);
            Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
            var schemaJson = await schema.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("006", schemaJson.GetProperty("version").GetString());
            Assert.True(schemaJson.TryGetProperty("ready", out _));
            Assert.Equal(JsonValueKind.Array, schemaJson.GetProperty("missing").ValueKind);
            Assert.Equal(JsonValueKind.String, schemaJson.GetProperty("runtimeDirectory").ValueKind);

            var validatePath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/employer-interface/validate";
            using var deniedValidationRequest = NewRequest(HttpMethod.Post, validatePath,
                viewer.Id, viewerSession.Id);
            deniedValidationRequest.Content = XmlUpload("not-006.xml", "<not-employer-interface />"u8.ToArray());
            using var deniedValidation = await client.SendAsync(deniedValidationRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedValidation.StatusCode);

            var invalidXmlBytes = "<not-employer-interface />"u8.ToArray();
            using var invalidValidationRequest = NewRequest(HttpMethod.Post, validatePath,
                admin.Id, adminSession.Id);
            invalidValidationRequest.Content = XmlUpload("not-006.xml", invalidXmlBytes);
            using var invalidValidation = await client.SendAsync(invalidValidationRequest, ct);
            Assert.Equal(HttpStatusCode.OK, invalidValidation.StatusCode);
            var invalidValidationJson = await invalidValidation.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.False(invalidValidationJson.GetProperty("isValid").GetBoolean());
            Assert.Equal("not-006.xml", invalidValidationJson.GetProperty("fileName").GetString());
            Assert.Equal(EmployerInterfacePayload.Hash(invalidXmlBytes),
                invalidValidationJson.GetProperty("fileHash").GetString());
            Assert.Equal(JsonValueKind.Array, invalidValidationJson.GetProperty("issues").ValueKind);

            var transmittedReport = new ManualReport(organization.Id, employer.Id,
                new DateOnly(salaryDate.Year, salaryDate.Month, 1), salaryDate);
            transmittedReport.MarkReadyForValidation();
            transmittedReport.MarkValidated();
            transmittedReport.MarkTransmissionStarted();
            transmittedReport.MarkSent();
            var payloadBytes = "<?xml version=\"1.0\" encoding=\"utf-8\"?><MimshakMaasikim />"u8.ToArray();
            var payloadFileName = "003000510000007EMPONG000006202610110000000001.DAT";
            var transmission = new ReportTransmission(transmittedReport.Id, organization.Id, employer.Id,
                "characterization", 1);
            var evidenceProtector = factory.Services.GetRequiredService<IDataProtectionService>();
            transmission.Prepare(EmployerInterfacePayload.Hash(payloadBytes), payloadFileName,
                evidenceProtector.ProtectBytes(payloadBytes, $"report-transmission:{transmission.Id}"));
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(transmittedReport, transmission);
                await db.SaveChangesAsync(ct);
            }

            var exportPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/employer-interface/reports/{transmittedReport.Id}/xml";
            using var anonymousExport = await client.GetAsync(exportPath, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousExport.StatusCode);
            using var crossTenantExportRequest = NewRequest(HttpMethod.Get, exportPath,
                otherAdmin.Id, otherSession.Id);
            using var crossTenantExport = await client.SendAsync(crossTenantExportRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenantExport.StatusCode);
            using var exportRequest = NewRequest(HttpMethod.Get, exportPath, admin.Id, adminSession.Id);
            using var export = await client.SendAsync(exportRequest, ct);
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("application/xml", export.Content.Headers.ContentType?.MediaType);
            Assert.Equal(payloadFileName, export.Content.Headers.ContentDisposition?.FileNameStar
                ?? export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal(payloadBytes, await export.Content.ReadAsByteArrayAsync(ct));

            var invalidLimitsPath =
                $"{manualReportsPath}contribution-limits?year=1999";
            using var invalidLimitsRequest = NewRequest(HttpMethod.Get, invalidLimitsPath,
                admin.Id, adminSession.Id);
            using var invalidLimits = await client.SendAsync(invalidLimitsRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalidLimits.StatusCode);
            var invalidLimitsJson = await invalidLimits.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(JsonValueKind.String, invalidLimitsJson.GetProperty("error").ValueKind);

            var missingReportId = Guid.NewGuid();
            using var historyRequest = NewRequest(HttpMethod.Get,
                $"{manualReportsPath}{missingReportId}/transmissions", admin.Id, adminSession.Id);
            using var history = await client.SendAsync(historyRequest, ct);
            Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        });
    }

    [Fact]
    public async Task Employer_interface_import_preserves_http_security_contract_mapping_and_atomic_rollback()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new ReportingWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a78-admin", "a78-admin@test.invalid", "A78 Admin");
            var viewer = new User("a78-viewer", "a78-viewer@test.invalid", "A78 Viewer");
            var otherAdmin = new User("a78-other", "a78-other@test.invalid", "A78 Other");
            var organization = new Organization("A78 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A78 Other Organization", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var employer = new Employer(organization.Id, "A78 Employer", "510000027", "930000027",
                "Import", "Owner", "031234567", "import@test.invalid", "0501234567");
            var person = new Person(organization.Id, "123456782", "Import", "Employee",
                new DateOnly(1990, 1, 1), PersonGender.Female, "employee@test.invalid", "0507654321",
                "Tel Aviv", "Main", "10", "2", "6100000", null);
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2020, 1, 1), "A78-1", 10_000m);
            var account = new EmployerPaymentAccount(organization.Id, employer.Id, 12, 345,
                "123456789", "A78 Employer", "510000027");
            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var viewerMembership = new OrganizationMembership(viewer.Id, organization.Id, OrganizationRole.Viewer,
                EmployerAccessMode.AllEmployers, admin.Id);
            var otherMembership = new OrganizationMembership(otherAdmin.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, otherAdmin.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var viewerSession = new UserSession(viewer.Id, now, now.AddHours(1));
            var otherSession = new UserSession(otherAdmin.Id, now, now.AddHours(1));
            var reportingMonth = new DateOnly(now.Year, now.Month, 1).AddMonths(-1);
            var salaryPaymentDate = reportingMonth.AddMonths(1).AddDays(8);
            var xmlBytes = BuildImportPayload(employer, person, employment, reportingMonth);

            var protector = factory.Services.GetRequiredService<IDataProtectionService>();
            person.SetProtectedNationalId(
                protector.Protect("123456782", "person-national-id"),
                protector.LookupHash("123456782", "person-national-id-lookup"));
            account.SetProtectedValues(
                protector.Protect("123456789", "bank-account-number"),
                protector.LookupHash("123456789", "bank-account-number-lookup"),
                protector.Protect("510000027", "bank-account-holder-id"));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, viewer, otherAdmin, organization, otherOrganization, employer, person, employment,
                    account, adminMembership, viewerMembership, otherMembership,
                    adminSession, viewerSession, otherSession);
                await db.SaveChangesAsync(ct);
            }

            var path =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/employer-interface/import";
            using var anonymousContent = XmlUpload("current-006.xml", xmlBytes, account.Id, salaryPaymentDate);
            using var anonymous = await client.PostAsync(path, anonymousContent, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var viewerRequest = NewRequest(HttpMethod.Post, path, viewer.Id, viewerSession.Id);
            viewerRequest.Content = XmlUpload("current-006.xml", xmlBytes, account.Id, salaryPaymentDate);
            using var viewerResponse = await client.SendAsync(viewerRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

            using var crossTenantRequest = NewRequest(HttpMethod.Post, path, otherAdmin.Id, otherSession.Id);
            crossTenantRequest.Content = XmlUpload("current-006.xml", xmlBytes, account.Id, salaryPaymentDate);
            using var crossTenant = await client.SendAsync(crossTenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

            using var invalidRequest = NewRequest(HttpMethod.Post, path, admin.Id, adminSession.Id);
            invalidRequest.Content = XmlUpload("invalid.xml", "<invalid />"u8.ToArray(), account.Id, salaryPaymentDate);
            using var invalid = await client.SendAsync(invalidRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var invalidJson = await invalid.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.False(invalidJson.GetProperty("validation").GetProperty("isValid").GetBoolean());

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var inject = new NpgsqlCommand("""
                    CREATE OR REPLACE FUNCTION reporting.a78_fail_import() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'A7.8 injected import failure'; END $$;
                    CREATE TRIGGER a78_fail_import BEFORE INSERT ON reporting.manual_reports
                    FOR EACH ROW EXECUTE FUNCTION reporting.a78_fail_import();
                    """, connection);
                await inject.ExecuteNonQueryAsync(ct);
            }

            using var failingRequest = NewRequest(HttpMethod.Post, path, admin.Id, adminSession.Id);
            failingRequest.Content = XmlUpload("current-006.xml", xmlBytes, account.Id, salaryPaymentDate);
            using var failing = await client.SendAsync(failingRequest, ct);
            Assert.Equal(HttpStatusCode.InternalServerError, failing.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                Assert.False(await db.ManualReports.AnyAsync(x => x.OrganizationId == organization.Id, ct));
                Assert.False(await db.ManualReportEmployees.AnyAsync(x => x.OrganizationId == organization.Id, ct));
                Assert.False(await db.ManualReportProducts.AnyAsync(ct));
                Assert.False(await db.ManualContributions.AnyAsync(ct));
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var remove = new NpgsqlCommand("""
                    DROP TRIGGER a78_fail_import ON reporting.manual_reports;
                    DROP FUNCTION reporting.a78_fail_import();
                    """, connection);
                await remove.ExecuteNonQueryAsync(ct);
            }

            using var validRequest = NewRequest(HttpMethod.Post, path, admin.Id, adminSession.Id);
            validRequest.Content = XmlUpload("current-006.xml", xmlBytes, account.Id, salaryPaymentDate);
            using var valid = await client.SendAsync(validRequest, ct);
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.True(valid.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
            Assert.Single(correlationValues);
            var validJson = await valid.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var reportId = validJson.GetProperty("reportId").GetGuid();
            Assert.Equal("CurrentReport", validJson.GetProperty("documentType").GetString());
            Assert.Equal(1, validJson.GetProperty("importedEmployees").GetInt32());
            Assert.Equal(0, validJson.GetProperty("unmatchedRows").GetInt32());
            Assert.Equal("current-006.xml", validJson.GetProperty("fileName").GetString());
            Assert.Equal(EmployerInterfacePayload.Hash(xmlBytes), validJson.GetProperty("fileHash").GetString());

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var importedReport = await db.ManualReports.AsNoTracking().SingleAsync(x => x.Id == reportId, ct);
                Assert.Equal(organization.Id, importedReport.OrganizationId);
                Assert.Equal(employer.Id, importedReport.EmployerId);
                Assert.Equal(reportingMonth, importedReport.ReportingMonth);
                var importedEmployee = await db.ManualReportEmployees.AsNoTracking()
                    .SingleAsync(x => x.ReportId == reportId, ct);
                Assert.Equal(employment.Id, importedEmployee.EmploymentId);
                Assert.Single(await db.ManualReportProducts.AsNoTracking()
                    .Where(x => x.ReportEmployeeId == importedEmployee.Id).ToListAsync(ct));
            }
        });
    }

    [Fact]
    public async Task Feedback_queries_and_exports_preserve_http_security_json_and_xlsx_contracts()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new ReportingWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a711-admin", "a711-admin@test.invalid", "A711 Admin");
            var outsider = new User("a711-outsider", "a711-outsider@test.invalid", "A711 Outsider");
            var organization = new Organization("A711 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A711 Other", OrganizationType.RegularOrganization);
            var employer = new Employer(organization.Id, "A711 Employer", "515711001", "935711001");
            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var outsiderMembership = new OrganizationMembership(outsider.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, outsider.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var outsiderSession = new UserSession(outsider.Id, now, now.AddHours(1));
            var report = new ManualReport(organization.Id, employer.Id,
                new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 9));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, outsider, organization, otherOrganization, employer,
                    adminMembership, outsiderMembership, adminSession, outsiderSession, report);
                await db.SaveChangesAsync(ct);
            }

            var listPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/report-feedback/?skip=0&take=20";
            using var anonymous = await client.GetAsync(listPath, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var deniedRequest = NewRequest(HttpMethod.Get, listPath, outsider.Id, outsiderSession.Id);
            using var denied = await client.SendAsync(deniedRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            using var listRequest = NewRequest(HttpMethod.Get, listPath, admin.Id, adminSession.Id);
            using var list = await client.SendAsync(listRequest, ct);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listJson = await list.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.True(listJson.TryGetProperty("items", out var items));
            Assert.True(items.GetArrayLength() >= 1);
            Assert.True(listJson.TryGetProperty("summary", out _));

            var exportPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/report-feedback/{report.Id}/exports/contributions";
            using var deniedExportRequest = NewRequest(HttpMethod.Get, exportPath,
                outsider.Id, outsiderSession.Id);
            using var deniedExport = await client.SendAsync(deniedExportRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedExport.StatusCode);

            using var exportRequest = NewRequest(HttpMethod.Get, exportPath, admin.Id, adminSession.Id);
            using var export = await client.SendAsync(exportRequest, ct);
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                export.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(export.Content.Headers.ContentDisposition?.FileNameStar
                ?? export.Content.Headers.ContentDisposition?.FileName);
            var workbook = await export.Content.ReadAsByteArrayAsync(ct);
            Assert.True(workbook.Length > 4);
            Assert.Equal((byte)'P', workbook[0]);
            Assert.Equal((byte)'K', workbook[1]);

            var invalidPath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/report-feedback/{report.Id}/exports/not-supported";
            using var invalidRequest = NewRequest(HttpMethod.Get, invalidPath, admin.Id, adminSession.Id);
            using var invalid = await client.SendAsync(invalidRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var invalidJson = await invalid.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("export_type_invalid", invalidJson.GetProperty("error").GetString());

            var resolvePath =
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/report-feedback/{report.Id}/resolution-actions/problems/resolve";
            using var anonymousResolve = new HttpRequestMessage(HttpMethod.Post, resolvePath)
            {
                Content = JsonContent.Create(new
                {
                    problemIds = new[] { "stale-problem" },
                    source = "manual",
                    validatedReportId = (Guid?)null
                })
            };
            using var anonymousResolveResponse = await client.SendAsync(anonymousResolve, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResolveResponse.StatusCode);

            using var deniedResolve = NewRequest(HttpMethod.Post, resolvePath,
                outsider.Id, outsiderSession.Id);
            deniedResolve.Content = JsonContent.Create(new
            {
                problemIds = new[] { "stale-problem" },
                source = "manual",
                validatedReportId = (Guid?)null
            });
            using var deniedResolveResponse = await client.SendAsync(deniedResolve, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedResolveResponse.StatusCode);

            using var emptyResolve = NewRequest(HttpMethod.Post, resolvePath, admin.Id, adminSession.Id);
            emptyResolve.Content = JsonContent.Create(new
            {
                problemIds = Array.Empty<string>(),
                source = "manual",
                validatedReportId = (Guid?)null
            });
            using var emptyResolveResponse = await client.SendAsync(emptyResolve, ct);
            Assert.Equal(HttpStatusCode.BadRequest, emptyResolveResponse.StatusCode);
            var emptyJson = await emptyResolveResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("problem_ids_required", emptyJson.GetProperty("error").GetString());

            using var staleResolve = NewRequest(HttpMethod.Post, resolvePath, admin.Id, adminSession.Id);
            staleResolve.Content = JsonContent.Create(new
            {
                problemIds = new[] { "stale-problem" },
                source = "manual",
                validatedReportId = (Guid?)null
            });
            using var staleResolveResponse = await client.SendAsync(staleResolve, ct);
            Assert.Equal(HttpStatusCode.Conflict, staleResolveResponse.StatusCode);
            var staleJson = await staleResolveResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("resolution_problem_stale", staleJson.GetProperty("error").GetString());
        });
    }

    private static HttpRequestMessage NewRequest(
        HttpMethod method, string path, Guid userId, Guid sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-User-Id", userId.ToString());
        request.Headers.Add("X-Session-Id", sessionId.ToString());
        return request;
    }

    private static MultipartFormDataContent XmlUpload(string fileName, byte[] bytes,
        Guid? paymentAccountId = null, DateOnly? salaryPaymentDate = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        content.Add(file, "file", fileName);
        if (paymentAccountId.HasValue)
            content.Add(new StringContent(paymentAccountId.Value.ToString()), "paymentAccountId");
        if (salaryPaymentDate.HasValue)
            content.Add(new StringContent(salaryPaymentDate.Value.ToString("yyyy-MM-dd")), "salaryPaymentDate");
        return content;
    }

    private static byte[] BuildImportPayload(Employer employer, Person person, Employment employment,
        DateOnly reportingMonth)
    {
        var reportEmployee = new ManualReportEmployee(Guid.NewGuid(), employer.OrganizationId, employer.Id,
            employment.Id, person.Id, "123456782", person.FirstName, person.LastName,
            employment.EmployeeNumber, employment.MonthlySalary);
        reportEmployee.SetInterfaceSnapshot(1, "123456782", person.BirthDate,
            person.Gender.HasValue ? (int)person.Gender.Value : null,
            person.Email, person.Mobile, person.City, person.Street, person.HouseNumber, person.Apartment,
            person.PostalCode, person.PostOfficeBox, employment.StartDate);
        var product = new ManualReportProduct(reportEmployee.Id, PensionProductType.PensionFund,
            "A78-POLICY", reportingMonth, 10_000m, "1", "1", false, null,
            fundCode: new string('1', 30), fundName: "A78 Fund");
        var contribution = new ManualContribution(product.Id, ContributionParty.Employee,
            ContributionComponent.Benefits, 600m, 6m, 0m);
        var payment = new ManualReportPayment(product.Id);
        payment.Update("A78 Fund", "10 - 123 - 987654", "", reportingMonth.AddMonths(1).AddDays(8), null,
            "A78-REF", "Test Bank", "10", "123", "123456", "");
        var metadata = new EmployerInterfaceReportProductData(product.Id);
        metadata.Update(1, 1, 1, reportingMonth, null, null, 2, null, 1, 1, 1);
        var options = new EmployerInterface006Options
        {
            EnvironmentCode = 2,
            SenderCode = 3,
            SenderIdentifierType = 1,
            SenderIdentifier = employer.RegistrationNumber,
            SenderName = employer.LegalName,
            SenderContactFirstName = employer.ContactFirstName,
            SenderContactLastName = employer.ContactLastName,
            SenderContactPhone = employer.ContactPhone,
            SenderContactEmail = employer.ContactEmail,
            SenderContactMobile = employer.ContactMobile,
            RecipientCode = 6,
            RecipientIdentifierType = 1,
            RecipientIdentifier = employer.RegistrationNumber
        };
        var context = new EmployerInterface006XmlBuilder.BuildContext(
            employer, [reportEmployee], new Dictionary<Guid, Person> { [person.Id] = person },
            new Dictionary<Guid, Employment> { [employment.Id] = employment },
            [product], [contribution], [payment], [metadata], options,
            PreparedAt: new DateTimeOffset(reportingMonth.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var built = EmployerInterface006XmlBuilder.BuildCurrent(context);
        Assert.Empty(built.Issues);
        Assert.NotNull(built.Document);
        Assert.Empty(EmployerInterface006WorkbookRules.ValidateAndApply(built.Document!, context, false));
        return EmployerInterface006XmlSerializer.Serialize(built.Document!);
    }

    private sealed class ReportingWebApplicationFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:AlphaDatabase", connectionString);
            builder.UseSetting("EmployerInterface006:ClearinghouseProvider", "Mock");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AlphaDatabase"] = connectionString,
                    ["Database:ApplyMigrations"] = "false",
                    ["DemoData:Enabled"] = "false",
                    ["Security:DataProtectionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                }));
            builder.ConfigureTestServices(services =>
            {
                var hostedServices = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();
                foreach (var hostedService in hostedServices) services.Remove(hostedService);
                services.RemoveAll<AlphaDbContext>();
                services.RemoveAll<DbContextOptions<AlphaDbContext>>();
                services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "A7TestHeaders";
                        options.DefaultChallengeScheme = "A7TestHeaders";
                        options.DefaultForbidScheme = "A7TestHeaders";
                    })
                    .AddScheme<AuthenticationSchemeOptions, A7TestAuthenticationHandler>(
                        "A7TestHeaders", null);
            });
        }
    }

    private sealed class A7TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User-Id", out var userValue) ||
                !Guid.TryParse(userValue, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
            if (Request.Headers.TryGetValue("X-Session-Id", out var sessionValue) &&
                Guid.TryParse(sessionValue, out var sessionId))
                claims.Add(new Claim("alpha:session_id", sessionId.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private static async Task InIsolatedDatabase(Func<string, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_a7_reporting_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = database,
            Pooling = false
        };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(connection.ConnectionString).Options;
            await using (var db = new AlphaDbContext(options))
            {
                await db.Database.EnsureCreatedAsync(ct);
                await ReferenceDataSchemaInitializer.EnsureCreatedAsync(db, ct);
            }
            await test(connection.ConnectionString, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(baseConnection);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
