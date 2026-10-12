using System.Text.RegularExpressions;

namespace Alpha.Api.Errors;

/// <summary>
/// The single API error contract (RFC 9457 Problem Details). Every error response has:
/// <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c>, <c>instance</c>, <c>errorCode</c>, <c>traceId</c>;
/// <c>errors</c> (field → messages) is present exactly when <c>errorCode</c> is <see cref="ValidationFailed"/>.
/// <para>
/// <c>error</c> is a deprecated alias that carries the pre-A12.3 value so the current frontend keeps working during
/// the migration (and after a frontend-only rollback). Remove it once the frontend reads <c>errorCode</c>.
/// </para>
/// See docs/architecture.md#error-handling.
/// </summary>
public static partial class ApiErrors
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string MalformedRequest = "MALFORMED_REQUEST";
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
    public const string SessionExpired = "AUTH_SESSION_EXPIRED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string Conflict = "CONFLICT";
    public const string DuplicateResource = "DUPLICATE_RESOURCE";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string InternalError = "INTERNAL_ERROR";
    public const string ExternalProviderUnavailable = "EXTERNAL_PROVIDER_UNAVAILABLE";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string ExternalProviderTimeout = "EXTERNAL_PROVIDER_TIMEOUT";

    /// <summary>Key in <c>errors</c> for messages that are not tied to one field.</summary>
    public const string RequestErrorsKey = "request";

    internal const string ErrorCodeMember = "errorCode";
    internal const string TraceIdMember = "traceId";
    internal const string ErrorsMember = "errors";
    internal const string DeprecatedErrorMember = "error";

    private static readonly Dictionary<int, (string Code, string Detail)> Defaults = new()
    {
        [400] = (ValidationFailed, "The request is invalid."),
        [401] = (AuthenticationRequired, "Authentication is required."),
        [403] = (AccessDenied, "You do not have permission to perform this action."),
        [404] = (ResourceNotFound, "The requested resource was not found."),
        [405] = (MethodNotAllowed, "The HTTP method is not allowed for this resource."),
        [409] = (Conflict, "The request conflicts with the current state of the resource."),
        [413] = (PayloadTooLarge, "The request body is too large."),
        [415] = (UnsupportedMediaType, "The request content type is not supported."),
        [429] = (RateLimitExceeded, "Too many requests. Try again later."),
        [500] = (InternalError, "An unexpected error occurred."),
        [502] = (ExternalProviderUnavailable, "An external service is unavailable."),
        [503] = (ServiceUnavailable, "The service is temporarily unavailable."),
        [504] = (ExternalProviderTimeout, "An external service did not respond in time.")
    };

    /// <summary>Default code for a status; 4xx/5xx statuses without a dedicated code fall back by class.</summary>
    public static string DefaultCode(int status) =>
        Defaults.TryGetValue(status, out var value) ? value.Code : status >= 500 ? InternalError : ValidationFailed;

    public static string DefaultDetail(int status) =>
        Defaults.TryGetValue(status, out var value) ? value.Detail : status >= 500 ? Defaults[500].Detail : Defaults[400].Detail;

    /// <summary>A legacy <c>error</c> value that is a snake_case code (for example <c>report_not_editable</c>).</summary>
    public static bool IsLegacyCode(string? value) => value is not null && LegacyCodePattern().IsMatch(value);

    /// <summary><c>report_not_editable</c> → <c>REPORT_NOT_EDITABLE</c>.</summary>
    public static string FromLegacyCode(string legacyCode) => legacyCode.ToUpperInvariant();

    /// <summary>
    /// Completes any Problem Details response with the contract members. Runs for status-code pages, exception
    /// handling, <c>Results.Problem</c> and converted module results alike.
    /// </summary>
    public static void Enrich(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var http = context.HttpContext;
        var status = problem.Status ?? http.Response.StatusCode;
        problem.Status = status;
        problem.Instance ??= http.Request.Path.Value;
        // The framework has no default type for statuses outside RFC 9110 (429 is RFC 6585); about:blank is the
        // RFC 9457 default meaning "the status code is the problem type".
        problem.Type ??= status == StatusCodes.Status429TooManyRequests ? "https://www.rfc-editor.org/rfc/rfc6585#section-4" : "about:blank";
        problem.Title ??= Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : "Error";

        if (!problem.Extensions.TryGetValue(ErrorCodeMember, out var codeValue) || codeValue is not string code)
        {
            code = status == StatusCodes.Status401Unauthorized && http.Response.Headers.ContainsKey("X-Alpha-Session-Expired")
                ? SessionExpired
                : DefaultCode(status);
            problem.Extensions[ErrorCodeMember] = code;
        }

        problem.Detail ??= DefaultDetail(status);
        // The request trace identifier: also in logs, audit rows and (after the audit middleware) X-Correlation-ID.
        problem.Extensions[TraceIdMember] = http.TraceIdentifier;
        problem.Extensions.TryAdd(DeprecatedErrorMember, code.ToLowerInvariant());

        if (code == ValidationFailed)
            problem.Extensions.TryAdd(ErrorsMember, new Dictionary<string, string[]> { [RequestErrorsKey] = [problem.Detail] });
        else
            problem.Extensions.Remove(ErrorsMember);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyCodePattern();
}
