using System.Text;
using Alpha.Api.Authentication;
using Alpha.Api.Endpoints;
using Alpha.Api.Validation;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
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
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication("DevelopmentHeaders")
        .AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>("DevelopmentHeaders", null);
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var prototypeSigningKey = builder.Configuration["PrototypeAuth:SigningKey"];
            if (!string.IsNullOrWhiteSpace(prototypeSigningKey))
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "alpha-prototype",
                    ValidateAudience = true,
                    ValidAudience = "alpha-frontend",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(prototypeSigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            }
            else
            {
                options.Authority = builder.Configuration["Authentication:Authority"];
                options.Audience = builder.Configuration["Authentication:Audience"];
            }
        });
}

builder.Services.AddAuthorization();
var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
    await db.Database.MigrateAsync();
}

// The original database was created before migrations were committed to the repository.
// Keep prototype schema additions idempotent until all changes move to deploy-time migrations.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
    await IdentitySchemaInitializer.EnsureUpdatedAsync(db);
    await ReportingSchemaInitializer.EnsureCreatedAsync(db);
}

var prototypeAuthEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["PrototypeAuth:SigningKey"]);
var demoDataEnabled = builder.Configuration.GetValue("DemoData:Enabled", true);
if (prototypeAuthEnabled && demoDataEnabled)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
    await DemoDataSeeder.SeedAsync(db);
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseReportingInputValidation();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health", new HealthCheckOptions
{
    AllowCachingResponses = false
}).AllowAnonymous();

app.MapGet("/health/db", async (AlphaDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "healthy", database = "postgresql" })
        : Results.Json(new { status = "unhealthy", database = "postgresql" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous().WithTags("Health");

app.MapAuthEndpoints();
app.MapPlatformEndpoints();
app.MapOrganizationEndpoints();
app.MapEmployerEndpoints();
app.MapAccessEndpoints();
app.MapManualReportEndpoints();
app.MapDerivedReportEndpoints();
app.MapReportValidationEndpoints();
app.Run();

public partial class Program;
