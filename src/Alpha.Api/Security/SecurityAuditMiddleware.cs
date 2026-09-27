using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Domain.Auditing;

namespace Alpha.Api.Security;

public sealed class SecurityAuditMiddleware(RequestDelegate next, ILogger<SecurityAuditMiddleware> logger)
{
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, IAlphaDbContext db)
    {
        var correlationId = context.TraceIdentifier;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await next(context);

        var sensitiveRead = HttpMethods.IsGet(context.Request.Method) &&
            (context.Request.Path.Value?.EndsWith("/file", StringComparison.OrdinalIgnoreCase) == true ||
             context.Request.Path.Value?.Contains("/export", StringComparison.OrdinalIgnoreCase) == true);
        if (!currentUser.IsAuthenticated || (!MutatingMethods.Contains(context.Request.Method) && !sensitiveRead)) return;
        try
        {
            Guid? organizationId = TryGuid(context.Request.RouteValues["organizationId"]);
            Guid? employerId = TryGuid(context.Request.RouteValues["employerId"]);
            var entityId = TryGuid(context.Request.RouteValues["id"])
                ?? TryGuid(context.Request.RouteValues["reportId"])
                ?? TryGuid(context.Request.RouteValues["employeeId"])
                ?? TryGuid(context.Request.RouteValues["attachmentId"])
                ?? employerId ?? organizationId ?? Guid.Empty;
            var result = context.Response.StatusCode < 400 ? "success" : context.Response.StatusCode is 401 or 403 ? "denied" : "failed";
            var data = JsonSerializer.Serialize(new
            {
                method = context.Request.Method,
                path = context.Request.Path.Value,
                result,
                statusCode = context.Response.StatusCode,
                ip = context.Connection.RemoteIpAddress?.ToString(),
                userAgent = context.Request.Headers.UserAgent.ToString()[..Math.Min(context.Request.Headers.UserAgent.ToString().Length, 300)]
            });
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, $"http.{context.Request.Method.ToLowerInvariant()}",
                "api", entityId, organizationId, employerId, data, correlationId));
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to persist security audit event {CorrelationId}", correlationId); }
    }

    private static Guid? TryGuid(object? value) => value is not null && Guid.TryParse(value.ToString(), out var id) ? id : null;
}
