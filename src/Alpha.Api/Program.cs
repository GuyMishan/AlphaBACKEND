using Alpha.Api.Authentication;
using Alpha.Api.Payments;
using Alpha.Api.Errors;
using Alpha.Api.Security;
using Alpha.Infrastructure;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Auditing;
using Alpha.Modules.Auditing.Middleware;
using Alpha.Modules.Billing;
using Alpha.Modules.IdentityAccess;
using Alpha.Modules.Organizations;
using Alpha.Modules.ReferenceData;
using Alpha.Modules.Reporting;
using Alpha.Modules.Workforce;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Shared persistence, host security and provider adapters.
builder.Services.AddInfrastructure(configuration);
builder.Services.AddAlphaAuthentication(configuration, builder.Environment);
builder.Services.AddAlphaRateLimiting();
builder.Services.AddSingleton<IDataProtectionService, AesDataProtectionService>();
builder.Services.AddScoped<SensitiveDataBackfill>();
builder.Services.AddHostedService<SensitiveDataBackfillHostedService>();
builder.Services.AddPaymentProviders();

// Business modules.
builder.Services.AddIdentityAccessModule();
builder.Services.AddOrganizationsModule();
builder.Services.AddBillingModule();
builder.Services.AddReportingModule(configuration, builder.Environment);
builder.Services.AddReferenceDataModule();

builder.Services.AddAlphaErrorHandling();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();
StartupConfiguration.EnsureValid(app.Configuration, app.Environment);

// Idempotent schema initialization in the production-pinned order. Empty-database bootstrap
// (EF EnsureCreated, a no-op when any table exists) defaults to on only in Development.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
    var bootstrapEmptyDatabase = configuration.GetValue("Database:BootstrapEmptyDatabase", app.Environment.IsDevelopment());
    await AlphaDatabaseInitializer.InitializeAsync(db, bootstrapEmptyDatabase);
    await scope.ServiceProvider.InitializeReportingAsync(CancellationToken.None);
}

var prototypeAuthEnabled = !string.IsNullOrWhiteSpace(configuration["PrototypeAuth:SigningKey"]);
var demoDataEnabled = app.Environment.IsDevelopment() && configuration.GetValue("DemoData:Enabled", false);
if (prototypeAuthEnabled && demoDataEnabled)
{
    await using var scope = app.Services.CreateAsyncScope();
    await DemoDataSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AlphaDbContext>());
}

// Security middleware order is a pinned compatibility constraint (see docs/security.md).
app.UseAlphaReverseProxy(configuration, app.Logger);
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthentication();
app.UseMiddleware<SessionActivityMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<SecurityAuditMiddleware>();
app.UseReportingModule();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health", new HealthCheckOptions { AllowCachingResponses = false }).AllowAnonymous();
app.MapGet("/health/db", async (AlphaDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "healthy", database = "postgresql" })
        : Results.Json(new { status = "unhealthy", database = "postgresql" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous().WithTags("Health");

// Every API endpoint is mapped under one root group so the single error contract applies uniformly.
var api = app.MapGroup(string.Empty).AddEndpointFilter<ApiErrorResultFilter>();

// Route groups shared by more than one module are owned by the host.
var platform = api.MapGroup("/api/platform").RequireAuthorization().WithTags("Platform");
platform.MapAuditingPlatformEndpoints();
platform.MapIdentityAccessPlatformEndpoints();

var referenceData = api.MapGroup("/api/reference-data").RequireAuthorization().WithTags("Reference Data");
referenceData.MapGeneralReferenceData();
referenceData.MapReportingReferenceData();

api.MapIdentityAccessModule();
api.MapOrganizationsModule();
api.MapWorkforceModule();
api.MapBillingModule();
api.MapReportingModule();
api.MapReferenceDataModule();

app.Run();

public partial class Program;
