using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.ReferenceData.Application;
using Alpha.Modules.Reporting.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

public sealed class ReportingEvidenceHttpIntegrationTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nA7.4 safe fixture");
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

    [Fact]
    public async Task Attachments_and_payment_confirmations_preserve_security_content_hash_history_and_cleanup()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            var scanner = new ControlledMalwareScanner();
            var storage = new InMemoryStorageHandler();
            var failure = new FailPaymentConfirmationSaveInterceptor();
            await using var factory = new EvidenceWebApplicationFactory(connectionString, scanner, storage, failure);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var admin = new User("a74-admin", "a74-admin@test.invalid", "A74 Admin");
            var viewer = new User("a74-viewer", "a74-viewer@test.invalid", "A74 Viewer");
            var outsider = new User("a74-outsider", "a74-outsider@test.invalid", "A74 Outsider");
            var organization = new Organization("A74 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A74 Other", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var employer = new Employer(organization.Id, "A74 Employer", "510000047", "930000047");
            var person = new Person(organization.Id, "123456782", "Dana", "Evidence");
            var employment = new Employment(organization.Id, employer.Id, person.Id,
                new DateOnly(2020, 1, 1), "A74-1", 10_000m);

            var historicalReport = Report();
            var historicalEmployee = Employee(historicalReport);
            var historicalProduct = Product(historicalEmployee, historicalReport.ReportingMonth, "A74-HISTORY");
            var cleanupReport = Report();
            var cleanupEmployee = Employee(cleanupReport);
            var cleanupProduct = Product(cleanupEmployee, cleanupReport.ReportingMonth, "A74-CLEANUP");
            var rollbackReport = Report();
            var rollbackEmployee = Employee(rollbackReport);
            var rollbackProduct = Product(rollbackEmployee, rollbackReport.ReportingMonth, "A74-ROLLBACK");

            ManualReport Report() => new(organization.Id, employer.Id,
                new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1), DateOnly.FromDateTime(DateTime.UtcNow));
            ManualReportEmployee Employee(ManualReport report) => new(report.Id, organization.Id, employer.Id,
                employment.Id, person.Id, "123456782", person.FirstName, person.LastName,
                employment.EmployeeNumber, employment.MonthlySalary);
            static ManualReportProduct Product(ManualReportEmployee employee, DateOnly month, string policy) =>
                new(employee.Id, PensionProductType.PensionFund, policy, month, 10_000m,
                    "1", "1", false, null, fundExternalKey: "A74-DEFAULT-FUND", fundName: "A74 Fund");

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
                var historicalMetadata = new EmployerInterfaceReportProductData(historicalProduct.Id);
                historicalMetadata.Update(1, 1, 1, new DateOnly(2026, 9, 1),
                    null, null, null, null, 1, 1, 1);
                db.Add(historicalMetadata);
                db.AddRange(admin, viewer, outsider, organization, otherOrganization, employer, person, employment,
                    historicalReport, historicalEmployee, historicalProduct,
                    cleanupReport, cleanupEmployee, cleanupProduct,
                    rollbackReport, rollbackEmployee, rollbackProduct,
                    adminMembership, viewerMembership, outsiderMembership,
                    adminSession, viewerSession, outsiderSession);
                await db.SaveChangesAsync(ct);
                // Make the isolated security fixture an eligible pension default fund,
                // so PDF/malware assertions exercise the upload path rather than eligibility denial.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO employers.employer_default_funds (employer_id, fund_external_keys) VALUES ({employer.Id}, {new[] { "A74-DEFAULT-FUND" }})", ct);
            }

            var attachments = $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports/{historicalReport.Id}/attachments";
            using var anonymous = await client.GetAsync(attachments + "/", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var viewerUpload = Multipart(HttpMethod.Post, attachments + "/", viewer.Id, viewerSession.Id,
                Pdf, "safe.pdf", ("documentTypeCode", "5"), ("reportProductId", historicalProduct.Id.ToString()));
            using var viewerUploadResponse = await client.SendAsync(viewerUpload, ct);
            Assert.Equal(HttpStatusCode.Forbidden, viewerUploadResponse.StatusCode);

            using var outsiderList = Request(HttpMethod.Get, attachments + "/", outsider.Id, outsiderSession.Id);
            using var outsiderListResponse = await client.SendAsync(outsiderList, ct);
            Assert.Equal(HttpStatusCode.Forbidden, outsiderListResponse.StatusCode);

            using var wrongType = Multipart(HttpMethod.Post, attachments + "/", admin.Id, adminSession.Id,
                Pdf, "unsafe.exe", ("documentTypeCode", "5"), ("reportProductId", historicalProduct.Id.ToString()));
            using var wrongTypeResponse = await client.SendAsync(wrongType, ct);
            Assert.Equal(HttpStatusCode.BadRequest, wrongTypeResponse.StatusCode);
            Assert.Equal("Employer Interface attachments must be PDF files.",
                (await wrongTypeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
                .GetProperty("error").GetString());

            using var oversized = Multipart(HttpMethod.Post, attachments + "/", admin.Id, adminSession.Id,
                new byte[65], "large.pdf", ("documentTypeCode", "5"),
                ("reportProductId", historicalProduct.Id.ToString()));
            using var oversizedResponse = await client.SendAsync(oversized, ct);
            Assert.Equal(HttpStatusCode.BadRequest, oversizedResponse.StatusCode);
            Assert.Equal("The maximum attachment size is 64 bytes.",
                (await oversizedResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
                .GetProperty("error").GetString());

            scanner.Verdict = MalwareScanVerdict.Infected;
            using var infected = Multipart(HttpMethod.Post, attachments + "/", admin.Id, adminSession.Id,
                Pdf, "infected.pdf", ("documentTypeCode", "5"),
                ("reportProductId", historicalProduct.Id.ToString()));
            using var infectedResponse = await client.SendAsync(infected, ct);
            Assert.Equal(HttpStatusCode.BadRequest, infectedResponse.StatusCode);
            Assert.Equal("The uploaded file failed the security scan.",
                (await infectedResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
                .GetProperty("error").GetString());

            scanner.Verdict = MalwareScanVerdict.Clean;
            using var upload = Multipart(HttpMethod.Post, attachments + "/", admin.Id, adminSession.Id,
                Pdf, "safe.pdf", ("documentTypeCode", "5"),
                ("reportProductId", historicalProduct.Id.ToString()));
            using var uploaded = await client.SendAsync(upload, ct);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            Assert.True(uploaded.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
            var uploadCorrelationId = Assert.Single(correlationValues);
            var attachmentJson = await uploaded.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var attachmentId = attachmentJson.GetProperty("id").GetGuid();
            var pdfHash = Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant();
            Assert.Equal(pdfHash, attachmentJson.GetProperty("sha256").GetString());
            Assert.Equal(Pdf.LongLength, attachmentJson.GetProperty("sizeBytes").GetInt64());

            var protector = factory.Services.GetRequiredService<IDataProtectionService>();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var stored = await db.ManualReportAttachments.AsNoTracking()
                    .SingleAsync(x => x.Id == attachmentId, ct);
                Assert.False(stored.Content.SequenceEqual(Pdf));
                Assert.Equal(Pdf, protector.UnprotectBytes(stored.Content,
                    $"report-attachment:{historicalReport.Id}:{historicalProduct.Id}:5"));
                var report = await db.ManualReports.SingleAsync(x => x.Id == historicalReport.Id, ct);
                report.MarkValidated();
                await db.SaveChangesAsync(ct);
            }

            using var download = Request(HttpMethod.Get, $"{attachments}/{attachmentId}/file", viewer.Id, viewerSession.Id);
            using var downloaded = await client.SendAsync(download, ct);
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            Assert.Equal(Pdf, await downloaded.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal("application/pdf", downloaded.Content.Headers.ContentType?.MediaType);

            using var crossAttachment = Request(HttpMethod.Get, $"{attachments}/{attachmentId}/file",
                outsider.Id, outsiderSession.Id);
            using var crossAttachmentResponse = await client.SendAsync(crossAttachment, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossAttachmentResponse.StatusCode);

            using var historicalDelete = Request(HttpMethod.Delete, $"{attachments}/{attachmentId}", admin.Id, adminSession.Id);
            using var historicalDeleteResponse = await client.SendAsync(historicalDelete, ct);
            Assert.Equal(HttpStatusCode.Conflict, historicalDeleteResponse.StatusCode);
            Assert.Equal("A sent or completed report cannot be edited.",
                (await historicalDeleteResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
                .GetProperty("error").GetString());

            var confirmations = $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports/{cleanupReport.Id}/payment-confirmations";
            using var badSignature = Multipart(HttpMethod.Post, $"{confirmations}/{cleanupProduct.Id}",
                admin.Id, adminSession.Id, Encoding.ASCII.GetBytes("not-a-png"), "confirmation.png");
            using var badSignatureResponse = await client.SendAsync(badSignature, ct);
            Assert.Equal(HttpStatusCode.BadRequest, badSignatureResponse.StatusCode);
            Assert.Equal("File signature does not match extension.",
                (await badSignatureResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
                .GetProperty("error").GetString());

            using var confirmationUpload = Multipart(HttpMethod.Post, $"{confirmations}/{cleanupProduct.Id}",
                admin.Id, adminSession.Id, Png, "confirmation.png");
            using var confirmationUploaded = await client.SendAsync(confirmationUpload, ct);
            Assert.Equal(HttpStatusCode.OK, confirmationUploaded.StatusCode);
            var confirmationJson = await confirmationUploaded.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var confirmationId = confirmationJson.GetProperty("id").GetGuid();
            var pngHash = Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant();
            Assert.Equal(pngHash, confirmationJson.GetProperty("sha256").GetString());
            var storedPath = Assert.Single(storage.Paths);
            Assert.StartsWith($"{organization.Id:N}/{employer.Id:N}/{cleanupReport.Id:N}/{cleanupProduct.Id:N}/", storedPath);
            Assert.EndsWith(".png", storedPath);

            using var confirmationDownload = Request(HttpMethod.Get,
                $"{confirmations}/{cleanupProduct.Id}/{confirmationId}/file", viewer.Id, viewerSession.Id);
            using var confirmationDownloaded = await client.SendAsync(confirmationDownload, ct);
            Assert.Equal(HttpStatusCode.OK, confirmationDownloaded.StatusCode);
            Assert.Equal(Png, await confirmationDownloaded.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal("image/png", confirmationDownloaded.Content.Headers.ContentType?.MediaType);

            storage.Replace(storedPath, [1, 2, 3]);
            using var corruptedDownload = Request(HttpMethod.Get,
                $"{confirmations}/{cleanupProduct.Id}/{confirmationId}/file", viewer.Id, viewerSession.Id);
            using var corruptedDownloadResponse = await client.SendAsync(corruptedDownload, ct);
            Assert.Equal(HttpStatusCode.BadGateway, corruptedDownloadResponse.StatusCode);
            storage.Replace(storedPath, Png);

            using var crossConfirmation = Request(HttpMethod.Get,
                $"{confirmations}/{cleanupProduct.Id}/{confirmationId}/file", outsider.Id, outsiderSession.Id);
            using var crossConfirmationResponse = await client.SendAsync(crossConfirmation, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossConfirmationResponse.StatusCode);

            using var deleteReport = Request(HttpMethod.Delete,
                $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports/{cleanupReport.Id}",
                admin.Id, adminSession.Id);
            using var reportDeleted = await client.SendAsync(deleteReport, ct);
            Assert.Equal(HttpStatusCode.NoContent, reportDeleted.StatusCode);
            Assert.Contains(storedPath, storage.DeletedPaths);
            Assert.DoesNotContain(storedPath, storage.Paths);

            var rollbackConfirmations = $"/api/organizations/{organization.Id}/employers/{employer.Id}/manual-reports/{rollbackReport.Id}/payment-confirmations";
            failure.FailNext = true;
            using var rollbackUpload = Multipart(HttpMethod.Post, $"{rollbackConfirmations}/{rollbackProduct.Id}",
                admin.Id, adminSession.Id, Png, "rollback.png");
            using var rollbackResponse = await client.SendAsync(rollbackUpload, ct);
            Assert.Equal(HttpStatusCode.InternalServerError, rollbackResponse.StatusCode);
            Assert.Empty(storage.Paths);
            Assert.NotEmpty(storage.DeletedPaths);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                Assert.False(await db.PaymentConfirmations.AnyAsync(x => x.ReportId == rollbackReport.Id, ct));
                Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "http.post"
                    && x.OrganizationId == organization.Id && x.EmployerId == employer.Id
                    && x.CorrelationId == uploadCorrelationId, ct));
            }
        });
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, Guid sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-User-Id", userId.ToString());
        request.Headers.Add("X-Session-Id", sessionId.ToString());
        return request;
    }

    private static HttpRequestMessage Multipart(HttpMethod method, string path, Guid userId, Guid sessionId,
        byte[] content, string fileName, params (string Name, string Value)[] fields)
    {
        var request = Request(method, path, userId, sessionId);
        var multipart = new MultipartFormDataContent();
        foreach (var field in fields) multipart.Add(new StringContent(field.Value), field.Name);
        multipart.Add(new ByteArrayContent(content), "file", fileName);
        request.Content = multipart;
        return request;
    }

    private sealed class ControlledMalwareScanner : IMalwareScanner
    {
        public MalwareScanVerdict Verdict { get; set; } = MalwareScanVerdict.Clean;
        public Task<MalwareScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct) =>
            Task.FromResult(new MalwareScanResult(Verdict));
    }

    private sealed class InMemoryStorageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> Paths => _files.Keys.ToArray();
        public List<string> DeletedPaths { get; } = [];

        public void Replace(string path, byte[] content) => _files[path] = content;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string marker = "/alpha-payment-evidence/";
            var absolutePath = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var path = Uri.UnescapeDataString(absolutePath[(absolutePath.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
                _files[path] = await request.Content!.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Get)
            {
                var path = Uri.UnescapeDataString(absolutePath[(absolutePath.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
                return _files.TryGetValue(path, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Delete)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                foreach (var prefix in json.RootElement.GetProperty("prefixes").EnumerateArray())
                {
                    var path = prefix.GetString()!;
                    DeletedPaths.Add(path);
                    _files.Remove(path);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FailPaymentConfirmationSaveInterceptor : SaveChangesInterceptor
    {
        public bool FailNext { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNext && eventData.Context!.ChangeTracker.Entries<PaymentConfirmation>()
                    .Any(entry => entry.State == EntityState.Added))
            {
                FailNext = false;
                throw new DbUpdateException("A7.4 injected payment-confirmation persistence failure.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class EvidenceWebApplicationFactory(
        string connectionString,
        ControlledMalwareScanner scanner,
        InMemoryStorageHandler storage,
        FailPaymentConfirmationSaveInterceptor failure)
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
                    ["Security:DataProtectionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                    ["Security:MalwareScanner:Provider"] = "Cloudmersive",
                    ["Security:MalwareScanner:MaxFileBytes"] = "64",
                    ["Storage:SupabaseUrl"] = "https://a74-test.supabase.co",
                    ["Storage:ServiceRoleKey"] = "local-test-only",
                    ["Storage:PaymentEvidenceBucket"] = "alpha-payment-evidence"
                }));
            builder.ConfigureTestServices(services =>
            {
                foreach (var hostedService in services
                             .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(hostedService);
                services.RemoveAll<AlphaDbContext>();
                services.RemoveAll<DbContextOptions<AlphaDbContext>>();
                services.AddDbContext<AlphaDbContext>(options =>
                    options.UseNpgsql(connectionString).AddInterceptors(failure));
                services.RemoveAll<IMalwareScanner>();
                services.AddSingleton<IMalwareScanner>(scanner);
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(new HttpClient(storage)));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "A74TestHeaders";
                        options.DefaultChallengeScheme = "A74TestHeaders";
                        options.DefaultForbidScheme = "A74TestHeaders";
                    })
                    .AddScheme<AuthenticationSchemeOptions, A74TestAuthenticationHandler>("A74TestHeaders", null);
            });
        }
    }

    private sealed class A74TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User-Id", out var userValue)
                || !Guid.TryParse(userValue, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
            if (Request.Headers.TryGetValue("X-Session-Id", out var sessionValue)
                && Guid.TryParse(sessionValue, out var sessionId))
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
        var database = "alpha_a74_evidence_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
