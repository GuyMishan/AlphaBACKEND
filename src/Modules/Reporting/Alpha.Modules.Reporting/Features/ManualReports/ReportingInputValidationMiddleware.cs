using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Reporting;
using Alpha.Modules.Reporting.Features.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Modules.Reporting.Features.ManualReports;

public static class ReportingInputValidationMiddleware
{
    public static IApplicationBuilder UseReportingInputValidation(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.Value?.Contains("/manual-reports", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            await next();
            return;
        }

        if (context.Request.Method is not ("POST" or "PUT"))
        {
            await next();
            return;
        }

        context.Request.EnableBuffering();
        try
        {
            if (context.Request.Method == "PUT" && context.Request.Path.Value!.Contains("/employees/", StringComparison.OrdinalIgnoreCase))
            {
                var request = await JsonSerializer.DeserializeAsync<SaveManualReportEmployeeRequest>(context.Request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
                context.Request.Body.Position = 0;
                if (request is not null)
                {
                    var db = context.RequestServices.GetRequiredService<AlphaDbContext>();
                    var years = request.Products.Select(x => x.SalaryMonth.Year).Distinct().ToArray();
                    var limits = await db.ContributionPercentageLimits.AsNoTracking()
                        .Where(x => years.Contains(x.Year)).ToListAsync(context.RequestAborted);
                    var errors = ReportingValidationRules.Products(request.Products, limits);
                    if (errors.Count > 0)
                    {
                        await WriteErrors(context, errors);
                        return;
                    }
                }
            }
            else if (context.Request.Method == "PUT" && context.Request.Path.Value!.Contains("/deposits/", StringComparison.OrdinalIgnoreCase))
            {
                var request = await JsonSerializer.DeserializeAsync<SaveManualReportPaymentRequest>(context.Request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
                context.Request.Body.Position = 0;
                if (request is not null)
                {
                    var errors = ReportingValidationRules.Payment(request);
                    if (errors.Count > 0)
                    {
                        await WriteErrors(context, errors);
                        return;
                    }
                }
            }
            else if (context.Request.Method == "PUT" && context.Request.Path.Value!.EndsWith("/details", StringComparison.OrdinalIgnoreCase))
            {
                var request = await JsonSerializer.DeserializeAsync<UpdateManualReportDetailsRequest>(context.Request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
                context.Request.Body.Position = 0;
                if (request is not null)
                {
                    var errors = ValidateReportDates(request.ReportingMonth, request.SalaryPaymentDate);
                    if (errors.Count > 0)
                    {
                        await WriteErrors(context, errors);
                        return;
                    }
                }
            }
            else if (context.Request.Method == "POST" && context.Request.Path.Value!.EndsWith("/manual-reports/", StringComparison.OrdinalIgnoreCase))
            {
                var request = await JsonSerializer.DeserializeAsync<CreateManualReportRequest>(context.Request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
                context.Request.Body.Position = 0;
                if (request is not null)
                {
                    var errors = ValidateReportDates(request.ReportingMonth, request.SalaryPaymentDate);
                    if (errors.Count > 0)
                    {
                        await WriteErrors(context, errors);
                        return;
                    }
                }
            }
        }
        catch (JsonException)
        {
            context.Request.Body.Position = 0;
            await WriteErrors(context, ["מבנה הבקשה אינו תקין."]);
            return;
        }

        context.Request.Body.Position = 0;
        await next();
    });

    private static List<string> ValidateReportDates(DateOnly month, DateOnly? salaryPaymentDate)
    {
        var errors = new List<string>();
        if (month.Year < 2000 || month > new DateOnly(DateTime.UtcNow.Year + 1, 12, 1))
            errors.Add("חודש הדיווח אינו תקין.");
        if (salaryPaymentDate is null)
            errors.Add("תאריך תשלום שכר הוא שדה חובה.");
        if (salaryPaymentDate is { } date && date < new DateOnly(month.Year, month.Month, 1).AddMonths(-1))
            errors.Add("תאריך תשלום השכר מוקדם מדי ביחס לחודש הדיווח.");
        if (salaryPaymentDate is { } future && future > DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(3)))
            errors.Add("תאריך תשלום השכר רחוק מדי בעתיד.");
        return errors;
    }

    /// <summary>
    /// Writes the API error contract (VALIDATION_FAILED with field-independent messages under <c>errors.request</c>)
    /// through the host's Problem Details writer, like every other error.
    /// </summary>
    private static async Task WriteErrors(HttpContext context, IReadOnlyCollection<string> errors)
    {
        var messages = errors.Take(10).ToArray();
        var summary = string.Join(" ", messages);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status400BadRequest,
                Detail = summary,
                Extensions =
                {
                    ["errorCode"] = "VALIDATION_FAILED",
                    ["errors"] = new Dictionary<string, string[]> { ["request"] = messages },
                    ["error"] = summary // deprecated alias of the pre-A12.3 body
                }
            }
        });
    }
}
