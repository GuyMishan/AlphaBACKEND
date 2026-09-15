using System.Text.Json;
using Alpha.Api.Endpoints;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Validation;

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
                    var db = context.RequestServices.GetRequiredService<IAlphaDbContext>();
                    var years = request.Products.Select(x => x.SalaryMonth.Year).Distinct().ToArray();
                    var limits = await db.ContributionPercentageLimits.AsNoTracking()
                        .Where(x => years.Contains(x.Year)).ToListAsync(context.RequestAborted);
                    var errors = ApiInputValidation.Products(request.Products, limits);
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
                    var errors = ApiInputValidation.Payment(request);
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

    private static async Task WriteErrors(HttpContext context, IReadOnlyCollection<string> errors)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = string.Join(" ", errors.Take(10)), errors }, context.RequestAborted);
    }
}
