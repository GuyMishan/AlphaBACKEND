using Alpha.Api.Authentication;
using Alpha.Api.Endpoints;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Infrastructure;
using Alpha.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

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
    builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = "AlphaAuth";
            options.DefaultChallengeScheme = "AlphaAuth";
        })
        .AddPolicyScheme("AlphaAuth", "JWT or internal proxy", options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey("X-Alpha-Internal-Secret")
                    ? "InternalProxy"
                    : JwtBearerDefaults.AuthenticationScheme;
        })
        .AddScheme<AuthenticationSchemeOptions, InternalProxyAuthenticationHandler>("InternalProxy", null)
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = builder.Configuration["Authentication:Authority"];
            options.Audience = builder.Configuration["Authentication:Audience"];
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

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
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

app.MapPlatformEndpoints();
app.MapOrganizationEndpoints();
app.MapEmployerEndpoints();
app.MapAccessEndpoints();
app.Run();

public partial class Program;
