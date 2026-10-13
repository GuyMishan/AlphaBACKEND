using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Alpha.Api.Errors;

/// <summary>
/// Converts every error result a module endpoint returns with a body (<c>Results.BadRequest(new { error, ... })</c>,
/// <c>Results.Conflict(...)</c>, <c>Results.NotFound(...)</c>, <c>Results.Json(..., statusCode)</c>) into the single
/// Problem Details contract. Modules keep describing *what* failed; the host owns *how* errors are serialized.
/// A 400 validation message may name the request's JSON property with <c>field</c>
/// (<c>new { error = "&lt;message&gt;", field = "legalName" }</c>); the message is then reported under that key in
/// <c>errors</c> instead of <c>request</c>.
/// </summary>
public sealed class ApiErrorResultFilter : IEndpointFilter
{
    internal const string FieldMember = "field";

    private static readonly HashSet<string> ReservedMembers =
        new(["type", "title", "status", "detail", "instance", ApiErrors.ErrorCodeMember, ApiErrors.TraceIdMember, ApiErrors.ErrorsMember],
            StringComparer.OrdinalIgnoreCase);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        return result is IStatusCodeHttpResult { StatusCode: >= 400 } statusResult
               && result is IValueHttpResult { Value: { } value and not ProblemDetails }
            ? ToProblem(statusResult.StatusCode!.Value, value)
            : result;
    }

    /// <summary>Maps a legacy error body to Problem Details. Public for unit tests.</summary>
    public static ProblemHttpResult ToProblem(int status, object body)
    {
        var problem = new ProblemDetails { Status = status };
        var element = JsonSerializer.SerializeToElement(body, JsonSerializerOptions.Web);
        if (element.ValueKind != JsonValueKind.Object)
            return TypedResults.Problem(problem);

        string? legacyError = null;
        string? field = null;
        foreach (var member in element.EnumerateObject())
        {
            if (member.NameEquals(ApiErrors.DeprecatedErrorMember) && member.Value.ValueKind == JsonValueKind.String)
                legacyError = member.Value.GetString();
            else if (member.NameEquals(FieldMember) && member.Value.ValueKind == JsonValueKind.String)
                field = member.Value.GetString();
            else if (member.NameEquals("detail") && member.Value.ValueKind == JsonValueKind.String)
                problem.Detail = member.Value.GetString();
            else
                // Additional context (limits, ids, states) stays at the top level; names that would collide with the
                // contract are prefixed (for example an invitation "status" becomes "resourceStatus").
                problem.Extensions[ReservedMembers.Contains(member.Name) ? "resource" + char.ToUpperInvariant(member.Name[0]) + member.Name[1..] : member.Name]
                    = member.Value.Clone();
        }

        if (ApiErrors.IsLegacyCode(legacyError))
        {
            problem.Extensions[ApiErrors.ErrorCodeMember] = ApiErrors.FromLegacyCode(legacyError!);
        }
        else if (!string.IsNullOrWhiteSpace(legacyError))
        {
            // A human-readable message: it becomes the detail (unless one was given) under the status' generic code.
            problem.Detail ??= legacyError;
            if (status == StatusCodes.Status400BadRequest && (problem.Detail != legacyError || !string.IsNullOrWhiteSpace(field)))
                problem.Extensions[ApiErrors.ErrorsMember] = new Dictionary<string, string[]> { [ErrorKey(field)] = [legacyError] };
        }

        if (legacyError is not null) problem.Extensions[ApiErrors.DeprecatedErrorMember] = legacyError;
        return TypedResults.Problem(problem);
    }

    /// <summary>JSON property names are camelCase on the wire (<c>LegalName</c> → <c>legalName</c>).</summary>
    private static string ErrorKey(string? field) =>
        string.IsNullOrWhiteSpace(field) ? ApiErrors.RequestErrorsKey : char.ToLowerInvariant(field[0]) + field[1..];
}
