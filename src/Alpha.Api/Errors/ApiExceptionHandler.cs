using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Alpha.Api.Errors;

/// <summary>
/// Maps known failure types to stable statuses and error codes. Anything else is left to the framework handler,
/// which logs it and returns 500 <c>INTERNAL_ERROR</c>. Bodies never contain exception text, SQL or stack traces.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    // Established user-facing message for optimistic concurrency conflicts.
    private const string ConcurrencyDetail = "הנתונים השתנו במקביל על ידי פעולה אחרת. רעננו את המסך ונסו שוב.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Headers are gone: nothing can be written; the framework aborts the response and logs.
        if (httpContext.Response.HasStarted) return false;

        // The client went away: nothing to answer and nothing to alert on.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        // EF wraps PostgreSQL errors differently for writes and for transient failures; match the root cause.
        var sqlState = FindPostgresError(exception)?.SqlState;
        var (status, code, detail, legacy) = exception switch
        {
            BadHttpRequestException badRequest => (badRequest.StatusCode,
                badRequest.StatusCode == StatusCodes.Status400BadRequest ? ApiErrors.MalformedRequest : ApiErrors.DefaultCode(badRequest.StatusCode),
                "The request body or parameters could not be read.", "bad_request"),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict, ConcurrencyDetail, "concurrency_conflict"),
            _ when sqlState == PostgresErrorCodes.UniqueViolation =>
                (StatusCodes.Status409Conflict, ApiErrors.DuplicateResource, "A record with the same identifying values already exists.", "duplicate"),
            _ when sqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected =>
                (StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict, ConcurrencyDetail, "concurrency_conflict"),
            HttpRequestException => (StatusCodes.Status502BadGateway, ApiErrors.ExternalProviderUnavailable, (string?)null, "upstream_unavailable"),
            TimeoutException or TaskCanceledException => (StatusCodes.Status504GatewayTimeout, ApiErrors.ExternalProviderTimeout, null, "upstream_timeout"),
            _ => (0, "", null, "")
        };
        if (status == 0) return false;

        // Type and status only: messages of database and HTTP exceptions can contain data or host names.
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
            "Request failed with {Status} {ErrorCode} ({ExceptionType}). Trace identifier: {TraceIdentifier}",
            status, code, exception.GetType().Name, httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Detail = detail,
                Extensions = { [ApiErrors.ErrorCodeMember] = code, [ApiErrors.DeprecatedErrorMember] = legacy }
            }
        });
    }

    private static PostgresException? FindPostgresError(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
            if (exception is PostgresException postgres) return postgres;
        return null;
    }
}
