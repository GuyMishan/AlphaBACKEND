using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Api.Errors;
using Alpha.Domain.Organizations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Integration;

/// <summary>
/// The single API error contract (docs/architecture.md#error-handling): every error, from every module and from the
/// framework, is Problem Details with type, title, status, detail, instance, errorCode and traceId; errors is present
/// exactly for VALIDATION_FAILED; nothing internal leaks.
/// </summary>
public sealed class ApiErrorContractTests
{
    [Fact]
    public async Task Framework_and_host_errors_follow_the_contract()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (user, session) = await SeedUserAsync(factory, "framework-user", platformAdmin: false);

            using (var response = await client.GetAsync("/api/scope", ct))
                await AssertContractAsync(response, HttpStatusCode.Unauthorized, ApiErrors.AuthenticationRequired, "/api/scope", ct);

            using (var response = await client.SendAsync(Request(HttpMethod.Get, "/api/scope", user.Id, Guid.NewGuid()), ct))
            {
                await AssertContractAsync(response, HttpStatusCode.Unauthorized, ApiErrors.SessionExpired, "/api/scope", ct);
                Assert.True(response.Headers.Contains("X-Alpha-Session-Expired"));
            }

            using (var response = await client.SendAsync(Request(HttpMethod.Delete, "/api/scope", user.Id, session.Id), ct))
                await AssertContractAsync(response, HttpStatusCode.MethodNotAllowed, ApiErrors.MethodNotAllowed, "/api/scope", ct);

            using (var request = Request(HttpMethod.Post, "/api/auth/logout", user.Id, session.Id))
            {
                // Success responses are never turned into errors: 204 stays empty.
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/otp/request")
                   { Content = new StringContent("{ \"nationalId\": ", System.Text.Encoding.UTF8, "application/json") })
            using (var response = await client.SendAsync(request, ct))
                await AssertContractAsync(response, HttpStatusCode.BadRequest, ApiErrors.MalformedRequest, "/api/auth/otp/request", ct);

            // Routing-level 405/415 need an authenticated caller: for anonymous callers default-deny answers 401 first.
            var (mediaUser, mediaSession) = await SeedUserAsync(factory, "media-user", platformAdmin: false);
            using (var request = Request(HttpMethod.Put, "/api/me/preferences/appearance", mediaUser.Id, mediaSession.Id))
            {
                request.Content = new StringContent("appearance=dark", System.Text.Encoding.UTF8, "text/plain");
                using var response = await client.SendAsync(request, ct);
                await AssertContractAsync(response, HttpStatusCode.UnsupportedMediaType, ApiErrors.UnsupportedMediaType, null, ct);
            }

            // Results.Problem from a module (authentication not configured in this host) keeps its detail.
            using (var response = await client.PostAsJsonAsync("/api/auth/otp/request",
                       new { nationalId = "123456782", phone = "0501234567", channel = "email" }, ct))
            {
                var body = await AssertContractAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrors.ServiceUnavailable, "/api/auth/otp/request", ct);
                Assert.Equal("Authentication is not configured.", body.GetProperty("detail").GetString());
            }

            HttpResponseMessage? limited = null;
            for (var i = 0; i < 21; i++)
            {
                limited?.Dispose();
                limited = await client.PostAsJsonAsync("/api/auth/otp/verify", new { challengeId = Guid.NewGuid(), code = "x" }, ct);
            }
            await AssertContractAsync(limited!, HttpStatusCode.TooManyRequests, ApiErrors.RateLimitExceeded, "/api/auth/otp/verify", ct);
            limited!.Dispose();
        });
    }

    [Fact]
    public async Task Module_errors_follow_the_contract_and_keep_their_specific_codes()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "M");
            var other = await SeedTenantAsync(factory, "O");
            var (user, session) = await SeedUserAsync(factory, "module-user", platformAdmin: false);
            var (admin, adminSession) = await SeedUserAsync(factory, "module-admin", platformAdmin: true);
            var org = $"/api/organizations/{tenant.Organization.Id}";

            // IdentityAccess: 403, validation message (400), specific 404 and 409 codes.
            using (var response = await client.SendAsync(Request(HttpMethod.Get, "/api/platform/users", user.Id, session.Id), ct))
            {
                var body = await AssertContractAsync(response, HttpStatusCode.Forbidden, ApiErrors.AccessDenied, "/api/platform/users", ct);
                Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), body.GetProperty("traceId").GetString());
            }
            using (var response = await client.SendAsync(Request(HttpMethod.Post, "/api/platform/users", admin.Id, adminSession.Id, platformAdminClaim: true,
                       json: new { email = "not-an-email", displayName = "Bad Email", nationalId = "123456782", phone = "0501234567" }), ct))
            {
                var body = await AssertContractAsync(response, HttpStatusCode.BadRequest, ApiErrors.ValidationFailed, "/api/platform/users", ct);
                Assert.Equal("Email is invalid.", body.GetProperty("detail").GetString());
                Assert.Equal("Email is invalid.", body.GetProperty("errors").GetProperty("email")[0].GetString());
                Assert.Equal("Email is invalid.", body.GetProperty("error").GetString()); // deprecated alias
            }
            using (var response = await client.GetAsync("/api/invitations/unknown-token", ct))
                await AssertContractAsync(response, HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", "/api/invitations/unknown-token", ct);
            using (var response = await client.SendAsync(Request(HttpMethod.Put, $"{org}/access/users/{tenant.Admin.Id}", tenant.Admin.Id, tenant.Session.Id,
                       json: new { role = (int)OrganizationRole.Viewer, employerAccessMode = 1, canCreateEmployer = false, canEditEmployer = false,
                           canCreateEmployee = false, canEditEmployee = false, canCreateReport = false, canTransmitReport = false }), ct))
            {
                var body = await AssertContractAsync(response, HttpStatusCode.Conflict, "LAST_ORGANIZATION_ADMIN", $"{org}/access/users/{tenant.Admin.Id}", ct);
                Assert.Equal("last_organization_admin", body.GetProperty("error").GetString());
            }

            // Organizations: tenant concealment stays 403 for the foreign route scope.
            using (var response = await client.SendAsync(Request(HttpMethod.Get, $"{org}/profile-center/", other.Admin.Id, other.Session.Id), ct))
                await AssertContractAsync(response, HttpStatusCode.Forbidden, ApiErrors.AccessDenied, $"{org}/profile-center/", ct);

            // Workforce: unknown resource and a Hebrew validation message.
            var employees = $"{org}/employers/{tenant.Employer.Id}/employees";
            using (var response = await client.SendAsync(Request(HttpMethod.Get, $"{employees}/{Guid.NewGuid()}", tenant.Admin.Id, tenant.Session.Id), ct))
                await AssertContractAsync(response, HttpStatusCode.NotFound, ApiErrors.ResourceNotFound, null, ct);
            using (var response = await client.SendAsync(Request(HttpMethod.Put, $"{org}/employers/{tenant.Employer.Id}", tenant.Admin.Id, tenant.Session.Id,
                       json: new { legalName = "", registrationNumber = "1", withholdingFileNumber = "1" }), ct))
                await AssertContractAsync(response, HttpStatusCode.BadRequest, ApiErrors.ValidationFailed, $"{org}/employers/{tenant.Employer.Id}", ct);

            // Billing: specific code.
            using (var response = await client.SendAsync(Request(HttpMethod.Put, $"{org}/billing/pricing", tenant.Admin.Id, tenant.Session.Id,
                       json: new { billingType = "Bogus" }), ct))
                await AssertContractAsync(response, HttpStatusCode.BadRequest, "INVALID_BILLING_TYPE", $"{org}/billing/pricing", ct);

            // Reporting: unknown report in the caller's own scope.
            using (var response = await client.SendAsync(Request(HttpMethod.Get, $"{org}/employers/{tenant.Employer.Id}/manual-reports/{Guid.NewGuid()}",
                       tenant.Admin.Id, tenant.Session.Id), ct))
                await AssertContractAsync(response, HttpStatusCode.NotFound, ApiErrors.ResourceNotFound, null, ct);

            // ReferenceData: platform administration requires a platform admin.
            using (var response = await client.SendAsync(Request(HttpMethod.Get, "/api/platform/reference-data/interfaces", user.Id, session.Id), ct))
                await AssertContractAsync(response, HttpStatusCode.Forbidden, ApiErrors.AccessDenied, "/api/platform/reference-data/interfaces", ct);
        });
    }

    [Theory]
    [InlineData("23505", HttpStatusCode.Conflict, ApiErrors.DuplicateResource)]
    [InlineData("40001", HttpStatusCode.Conflict, ApiErrors.ConcurrencyConflict)]
    [InlineData("P0001", HttpStatusCode.InternalServerError, ApiErrors.InternalError)]
    public async Task Database_failures_map_to_stable_codes_without_leaking_details(string sqlState, HttpStatusCode status, string errorCode)
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (admin, session) = await SeedUserAsync(factory, $"db-failure-{sqlState}", platformAdmin: true);
            // sqlState is a compile-time test constant (InlineData), never input.
#pragma warning disable EF1002
            await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION public.alpha_test_fail_user_insert() RETURNS trigger LANGUAGE plpgsql AS $f$
                BEGIN RAISE EXCEPTION 'alpha-secret-detail 123456782' USING ERRCODE = '{sqlState}'; END $f$;
                CREATE TRIGGER alpha_test_fail_user_insert BEFORE INSERT ON identity.users
                FOR EACH ROW EXECUTE FUNCTION public.alpha_test_fail_user_insert();
                """, ct));
#pragma warning restore EF1002

            using var response = await client.SendAsync(Request(HttpMethod.Post, "/api/platform/users", admin.Id, session.Id,
                platformAdminClaim: true, json: new { email = "new@test.invalid", displayName = "New User", nationalId = "123456782", phone = "0501234567" }), ct);
            var body = await AssertContractAsync(response, status, errorCode, "/api/platform/users", ct);
            Assert.DoesNotContain("alpha-secret-detail", body.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("123456782", body.GetRawText(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Unreadable_requests_map_to_MALFORMED_REQUEST_outside_Development_too()
    {
        // Minimal APIs throw BadHttpRequestException (mapped to MALFORMED_REQUEST) only when ThrowOnBadRequest is on,
        // which the framework enables only in Development; this test host always runs in Development.
        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production })
            .AddRouting()
            .AddAlphaErrorHandling();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RouteHandlerOptions>>().Value;

        Assert.True(options.ThrowOnBadRequest);
    }

    [Fact]
    public void Legacy_module_bodies_are_mapped_deterministically()
    {
        static IDictionary<string, object?> Map(int status, object body) => ((ProblemHttpResult)ApiErrorResultFilter.ToProblem(status, body)).ProblemDetails.Extensions;

        var code = ApiErrorResultFilter.ToProblem(409, new { error = "invitation_not_pending", status = "Accepted" });
        Assert.Equal("INVITATION_NOT_PENDING", code.ProblemDetails.Extensions["errorCode"]);
        Assert.Equal("invitation_not_pending", code.ProblemDetails.Extensions["error"]);
        Assert.Equal("Accepted", ((JsonElement)code.ProblemDetails.Extensions["resourceStatus"]!).GetString());
        Assert.Equal(409, code.ProblemDetails.Status);

        var withDetail = ApiErrorResultFilter.ToProblem(400, new { error = "invalid_assignments", detail = "רשימת השיוכים אינה תקינה." });
        Assert.Equal("רשימת השיוכים אינה תקינה.", withDetail.ProblemDetails.Detail);
        Assert.Equal("INVALID_ASSIGNMENTS", withDetail.ProblemDetails.Extensions["errorCode"]);

        var fieldError = ApiErrorResultFilter.ToProblem(400, new { error = "שם משפטי הוא שדה חובה.", field = "LegalName" });
        Assert.Equal("שם משפטי הוא שדה חובה.", fieldError.ProblemDetails.Detail);
        Assert.Equal(["שם משפטי הוא שדה חובה."], ((Dictionary<string, string[]>)fieldError.ProblemDetails.Extensions["errors"]!)["legalName"]);
        Assert.False(fieldError.ProblemDetails.Extensions.ContainsKey("field"));

        var message = ApiErrorResultFilter.ToProblem(409, new { error = "A user with this email already exists." });
        Assert.Equal("A user with this email already exists.", message.ProblemDetails.Detail);
        Assert.False(message.ProblemDetails.Extensions.ContainsKey("errorCode")); // generic CONFLICT is added on write

        var extras = Map(409, new { error = "free_plan_limits_exceeded", employers = 3, contactSupport = true });
        Assert.Equal(3, ((JsonElement)extras["employers"]!).GetInt32());
        Assert.True(((JsonElement)extras["contactSupport"]!).GetBoolean());
    }

    /// <summary>Asserts the full contract and returns the parsed body.</summary>
    private static async Task<JsonElement> AssertContractAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode,
        string? instance, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(text).RootElement.Clone();

        foreach (var member in new[] { "type", "title", "detail", "instance", "errorCode", "traceId" })
            Assert.True(body.TryGetProperty(member, out var value) && !string.IsNullOrWhiteSpace(value.GetString()), $"'{member}' is missing: {text}");
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(errorCode, body.GetProperty("errorCode").GetString());
        if (instance is not null) Assert.Equal(instance, body.GetProperty("instance").GetString());
        Assert.Equal(errorCode == ApiErrors.ValidationFailed, body.TryGetProperty("errors", out var errors));
        if (errorCode == ApiErrors.ValidationFailed) Assert.Equal(JsonValueKind.Object, errors.ValueKind);

        foreach (var forbidden in new[] { "   at ", "Exception", "Npgsql", "SELECT ", "INSERT " })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        return body;
    }
}
