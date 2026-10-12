using System.Security.Claims;
using System.Text.Encodings.Web;
using Alpha.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
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

namespace Alpha.Api.Tests.TestInfrastructure;

/// <summary>
/// In-process API host over a freshly created, empty PostgreSQL database on the loopback test server.
/// The database is bootstrapped by the production startup path (EF model + pinned schema initializers),
/// identities come from <c>X-User-Id</c>/<c>X-Session-Id</c>/<c>X-Platform-Admin</c> headers and are
/// then validated by the real session middleware. Hosted services are removed.
/// </summary>
internal static class AlphaApiTestHost
{
    public const string TestDataProtectionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static async Task RunAsync(Func<Factory, HttpClient, CancellationToken, Task> test,
        IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_host_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using var factory = new Factory(connection.ConnectionString, settings, configureServices);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            await test(factory, client, ct);
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

    public static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, Guid sessionId,
        bool platformAdminClaim = false, object? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-User-Id", userId.ToString());
        request.Headers.Add("X-Session-Id", sessionId.ToString());
        if (platformAdminClaim) request.Headers.Add("X-Platform-Admin", "true");
        if (json is not null) request.Content = System.Net.Http.Json.JsonContent.Create(json);
        return request;
    }

    public static async Task WithDbAsync(this Factory factory, Func<AlphaDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AlphaDbContext>());
    }

    internal sealed class Factory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:AlphaDatabase", connectionString);
            // Settings read while Program.cs registers services must be host settings: app-configuration sources
            // below are applied only after registration.
            builder.UseSetting("EmployerInterface006:ClearinghouseProvider",
                settings?.GetValueOrDefault("EmployerInterface006:ClearinghouseProvider") ?? "Mock");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AlphaDatabase"] = connectionString,
                    ["Database:BootstrapEmptyDatabase"] = "true",
                    ["DemoData:Enabled"] = "false",
                    ["Security:DataProtectionKey"] = TestDataProtectionKey
                }
                // A test's own settings override these defaults.
                .Where(entry => settings?.ContainsKey(entry.Key) != true)
                .Concat(settings ?? new Dictionary<string, string?>())));
            builder.ConfigureTestServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestPeerStartupFilter>());
            builder.ConfigureTestServices(services =>
            {
                foreach (var hostedService in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(hostedService);
                services.RemoveAll<AlphaDbContext>();
                services.RemoveAll<DbContextOptions<AlphaDbContext>>();
                services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = HeaderAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, null);
                configureServices?.Invoke(services);
            });
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "AlphaTestHeaders";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User-Id", out var userValue) || !Guid.TryParse(userValue, out var userId) ||
                !Request.Headers.TryGetValue("X-Session-Id", out var sessionValue) || !Guid.TryParse(sessionValue, out var sessionId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new("alpha:session_id", sessionId.ToString())
            };
            if (Request.Headers.TryGetValue("X-Platform-Admin", out var admin) && admin == "true")
                claims.Add(new Claim("alpha:platform_admin", "true"));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    /// <summary>
    /// Test-only stand-in for the TCP peer: <c>X-Test-Peer</c> sets <c>Connection.RemoteIpAddress</c> before
    /// the application pipeline, so trusted and untrusted proxies can be simulated in-process.
    /// </summary>
    private sealed class TestPeerStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer) && System.Net.IPAddress.TryParse(peer, out var address))
                    context.Connection.RemoteIpAddress = address;
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
