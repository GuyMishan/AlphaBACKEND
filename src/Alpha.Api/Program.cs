using System.Text;
using Alpha.Api.Authentication;
using Alpha.Api.Endpoints;
using Alpha.Api.Services;
using Alpha.Api.Validation;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Billing;
using Alpha.Application.Entitlements;
using Alpha.Application.Identity;
using Alpha.Application.Reporting;
using Alpha.Infrastructure;
using Alpha.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<OrganizationAccessService>();
builder.Services.AddScoped<BillingInheritanceService>();
builder.Services.AddScoped<BillingGateService>();
builder.Services.AddScoped<FakePaymentProvider>();
builder.Services.AddScoped<CardComPaymentProvider>();
builder.Services.AddScoped<PayPlusPaymentProvider>();
builder.Services.AddScoped<PaymentProviderResolver>();
builder.Services.AddScoped<IPaymentProviderResolver>(sp => sp.GetRequiredService<PaymentProviderResolver>());
builder.Services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<PaymentProviderResolver>());
builder.Services.AddScoped<IBillingCalculator, BillingCalculator>();
builder.Services.AddScoped<IBillingUsageCollector, BillingUsageCollector>();
builder.Services.AddScoped<IBillingCycleService, BillingCycleService>();
builder.Services.AddHostedService<BillingCycleHostedService>();
builder.Services.AddHttpClient("cardcom", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("payplus", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<ReportPaymentAccountService>();
builder.Services.Configure<EmployerInterface006Options>(builder.Configuration.GetSection(EmployerInterface006Options.SectionName));
builder.Services.AddSingleton<EmployerInterfaceSchemaRegistry>();
builder.Services.AddScoped<EmployerInterfaceService>();
builder.Services.AddScoped<EmployerInterface006ExportService>();
builder.Services.AddScoped<IReportTransmissionProvider, MockReportTransmissionProvider>();
builder.Services.AddHttpClient("otp-sms", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("otp-email", c => c.Timeout = TimeSpan.FromSeconds(25));
builder.Services.AddScoped<OtpDelivery>();
builder.Services.AddHttpClient<ReferenceDataSyncService>(client => { client.Timeout = TimeSpan.FromMinutes(5); client.DefaultRequestHeaders.UserAgent.ParseAdd("AlphaReferenceDataSync/1.0"); });
builder.Services.AddProblemDetails(); builder.Services.AddOpenApi(); builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(); builder.Services.AddHealthChecks();

if (builder.Environment.IsDevelopment()) builder.Services.AddAuthentication("DevelopmentHeaders").AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>("DevelopmentHeaders", null);
else builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => { var key = builder.Configuration["PrototypeAuth:SigningKey"]; if (!string.IsNullOrWhiteSpace(key)) options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "alpha-prototype", ValidateAudience = true, ValidAudience = "alpha-frontend", ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1) }; else { options.Authority = builder.Configuration["Authentication:Authority"]; options.Audience = builder.Configuration["Authentication:Audience"]; } });
builder.Services.AddAuthorization();
var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations")) { await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>(); await db.Database.MigrateAsync(); }
await using (var scope = app.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>(); await IdentitySchemaInitializer.EnsureUpdatedAsync(db); await EmployerAccessSchemaInitializer.EnsureUpdatedAsync(db); await AccessPermissionsSchemaInitializer.EnsureUpdatedAsync(db); await EmployerProfileSchemaInitializer.EnsureCreatedAsync(db); await OrganizationProfileSchemaInitializer.EnsureCreatedAsync(db); await BillingSchemaInitializer.EnsureCreatedAsync(db); await BillingInheritanceSchemaInitializer.EnsureUpdatedAsync(db); await BillingV2SchemaInitializer.EnsureUpdatedAsync(db); await SubscriptionSchemaInitializer.EnsureCreatedAsync(db); await BillingPlanSeedInitializer.EnsureSeededAsync(db); await OtpSchemaInitializer.EnsureCreatedAsync(db); await RegistrationOtpSchemaInitializer.EnsureCreatedAsync(db); await InvitationSchemaInitializer.EnsureCreatedAsync(db); await ReportingSchemaInitializer.EnsureCreatedAsync(db); await Section14SchemaInitializer.EnsureUpdatedAsync(db); await SalaryAllocationSchemaInitializer.EnsureUpdatedAsync(db); await PensionFundSnapshotSchemaInitializer.EnsureUpdatedAsync(db); await ReportLifecycleSchemaInitializer.EnsureUpdatedAsync(db); await ReportTransmissionSchemaInitializer.EnsureUpdatedAsync(db); await EmployerInterfaceFeedbackSchemaInitializer.EnsureCreatedAsync(db); await EmployerInterface006SchemaInitializer.EnsureUpdatedAsync(db); await ReferenceDataSchemaInitializer.EnsureCreatedAsync(db); await SelectOptionsSchemaInitializer.EnsureCreatedAsync(db); await EmployerInterface006CodebookInitializer.EnsureUpdatedAsync(db); await EmployerInterface006ReferenceRulesInitializer.EnsureUpdatedAsync(db); await EmployerInterface006ErrorCodeInitializer.EnsureUpdatedAsync(db); await SalaryLayerSchemaInitializer.EnsureCreatedAsync(db); }
var prototypeAuthEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["PrototypeAuth:SigningKey"]); var demoDataEnabled = builder.Configuration.GetValue("DemoData:Enabled", true);
if (prototypeAuthEnabled && demoDataEnabled) { await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>(); await DemoDataSeeder.SeedAsync(db); }
app.UseExceptionHandler(); app.UseHttpsRedirection(); app.UseAuthentication(); app.UseAuthorization(); app.UseReportingInputValidation();
if (app.Environment.IsDevelopment()) { app.MapOpenApi(); app.UseSwagger(); app.UseSwaggerUI(); }
app.MapHealthChecks("/health", new HealthCheckOptions { AllowCachingResponses = false }).AllowAnonymous();
app.MapGet("/health/db", async (AlphaDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "healthy", database = "postgresql" }) : Results.Json(new { status = "unhealthy", database = "postgresql" }, statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous().WithTags("Health");
app.MapAuthEndpoints(); app.MapScopeEndpoints(); app.MapInvitationEndpoints(); app.MapOnboardingEndpoints(); app.MapPlatformEndpoints(); app.MapSubscriptionEndpoints(); app.MapBillingManagementEndpoints(); app.MapReferenceDataEndpoints(); app.MapPublicReferenceDataEndpoints(); app.MapOrganizationEndpoints(); app.MapOrganizationProfileEndpoints(); app.MapBillingAccountEndpoints(); app.MapPaymentProviderEndpoints(); app.MapEmployerEndpoints(); app.MapAccessEndpoints(); app.MapUserPreferencesEndpoints(); app.MapEmployeePensionMixEndpoints(); app.MapManualReportEndpoints(); app.MapReportAttachmentEndpoints(); app.MapDerivedReportEndpoints(); app.MapReportValidationEndpoints(); app.MapEmployerInterfaceEndpoints(); app.MapEmployerInterfaceProfileEndpoints(); app.MapEmployerProfileCenterEndpoints(); app.MapEmployerPaymentAccountEndpoints(); app.MapOrganizationPaymentAccountEndpoints(); app.MapEmployerInterfacePreviousReferenceEndpoints(); app.MapReportTransmissionEndpoints(); app.MapReportFeedbackEndpoints(); app.Run();
public partial class Program;
