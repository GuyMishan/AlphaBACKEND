using Alpha.Api.Authentication;
using Alpha.Api.Endpoints;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

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
    builder.Services.AddAuthentication("DevelopmentHeaders")
        .AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>("DevelopmentHeaders", null);
else
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.Audience = builder.Configuration["Authentication:Audience"];
    });

builder.Services.AddAuthorization();
var app = builder.Build();
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
app.MapPlatformEndpoints();
app.MapOrganizationEndpoints();
app.MapEmployerEndpoints();
app.MapAccessEndpoints();
app.Run();

public partial class Program;
