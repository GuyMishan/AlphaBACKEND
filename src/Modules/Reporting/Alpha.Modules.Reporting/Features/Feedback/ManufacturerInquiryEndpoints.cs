using System.Data;
using System.Net;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Reporting;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Features.FeedbackIngestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Reporting.Features.Feedback;

/// <summary>Case-bound, tenant-authorized manufacturer inquiries. Never trusts a client recipient.</summary>
public static class ManufacturerInquiryEndpoints
{
    public sealed record SendInquiryRequest(Guid ReportProductId, int ErrorCode, string? AdditionalInformation);
    private sealed record Inquiry(Guid ReportProductId, int ErrorCode, string Recipient, string Manufacturer,
        string Product, string Subject, string Body);
    private sealed record History(Guid Id, string Recipient, string Subject, string Status, string? FailureCode, DateTimeOffset CreatedAt, string Body);

    public static RouteGroupBuilder MapManufacturerInquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/external-cases/{caseId:guid}/manufacturer-inquiries", GetAsync);
        group.MapPost("/external-cases/{caseId:guid}/manufacturer-inquiries", SendAsync);
        return group;
    }

    private const string TestRecipient = "alphapensia@gmail.com";
    private static bool TestMode(IConfiguration config) => config.GetValue("ManufacturerMail:TestMode", true);

    private static async Task<IResult> GetAsync(Guid organizationId, Guid employerId, Guid caseId,
        AlphaDbContext db, IOrganizationAuthorization access, IConfiguration config, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var rows = await ResolveAsync(organizationId, employerId, caseId, db, ct);
        if (rows is null) return Results.NotFound();
        var history = await ReadHistoryAsync(caseId, db, ct);
        return Results.Ok(new { inquiries = rows, history, testMode = TestMode(config), testRecipient = TestMode(config) ? TestRecipient : null });
    }

    private static async Task<IResult> SendAsync(Guid organizationId, Guid employerId, Guid caseId,
        SendInquiryRequest request, AlphaDbContext db, IOrganizationAuthorization access,
        ICurrentUser currentUser, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var rows = await ResolveAsync(organizationId, employerId, caseId, db, ct);
        if (rows is null) return Results.NotFound();
        var item = rows.SingleOrDefault(x => x.ReportProductId == request.ReportProductId && x.ErrorCode == request.ErrorCode);
        if (item is null || string.IsNullOrWhiteSpace(item.Recipient) ||
            string.IsNullOrWhiteSpace(item.Subject) || string.IsNullOrWhiteSpace(item.Body))
            return Results.BadRequest(new { error = "manufacturer_inquiry_unavailable" });
        // The approved DB template is authoritative; client input may only append free text.
        var subject = item.Subject;
        var additional = request.AdditionalInformation?.Trim() ?? "";
        if (additional.Length > 3000 || additional.Contains((char)0) >= 0)
            return Results.BadRequest(new { error = "manufacturer_inquiry_invalid_message" });
        var body = item.Body + (additional.Length > 0
            ? "\n\nמידע נוסף מטעם הפונה:\n" + additional
            : "");
        if (subject.Length is < 1 or > 200 || body.Length > 8000)
            return Results.BadRequest(new { error = "manufacturer_inquiry_invalid_message" });

        var testMode = TestMode(config);
        var actualRecipient = testMode ? TestRecipient : item.Recipient;
        var actualSubject = testMode ? "[ALPHA - סביבת בדיקות] " + subject : subject;
        var actualBody = testMode
            ? "הודעת בדיקה של ALPHA — לא נשלחה ליצרן.\n" +
              "הנמען המקורי שהיה אמור לקבל את ההודעה: " + item.Recipient + "\n" +
              "יצרן: " + item.Manufacturer + "\n" +
              "מוצר: " + item.Product + "\n" +
              "קוד שגיאה: " + item.ErrorCode + "\n\n" + body
            : body;

        // Reuse the exact OTP email provider configuration and priority:
        // Google Apps Script -> Resend -> SMTP. Never accept a sender from the client.
        var scriptUrl = config["Otp:Email:AppsScript:Url"];
        var apiKey = config["Otp:Email:ResendApiKey"];
        var from = config["Otp:Email:From"];
        var smtpHost = config["Otp:Email:Host"];
        if (string.IsNullOrWhiteSpace(scriptUrl) && string.IsNullOrWhiteSpace(apiKey) &&
            (string.IsNullOrWhiteSpace(smtpHost) || string.IsNullOrWhiteSpace(from)))
            return Results.Conflict(new { error = "manufacturer_mail_delivery_not_configured" });
        if (string.IsNullOrWhiteSpace(scriptUrl) && string.IsNullOrWhiteSpace(from))
            return Results.Conflict(new { error = "manufacturer_mail_delivery_not_configured" });

        // Persist before contacting the external provider, preserving an audit trail even on failure.
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reporting.feedback_manufacturer_email_dispatches
              (id,case_id,organization_id,employer_id,report_product_id,error_code,recipient,subject,body,requested_by)
            VALUES ({id},{caseId},{organizationId},{employerId},{item.ReportProductId},{item.ErrorCode},
                    {actualRecipient},{actualSubject},{actualBody},{currentUser.UserId})
            """, ct);

        string status = "failed";
        string? messageId = null;
        string? failure = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(scriptUrl))
            {
                var secret = config["Otp:Email:AppsScript:Secret"];
                if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32 ||
                    !Uri.TryCreate(scriptUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "script.google.com" ||
                    !uri.AbsolutePath.StartsWith("/macros/s/", StringComparison.Ordinal) ||
                    !uri.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal))
                    throw new InvalidOperationException("OTP Apps Script is not safely configured.");
                using var outgoing = new HttpRequestMessage(HttpMethod.Post, uri);
                outgoing.Content = JsonContent.Create(new
                {
                    secret, to = actualRecipient, subject = actualSubject, text = actualBody, type = "manufacturer-inquiry"
                });
                using var response = await clients.CreateClient("otp-email").SendAsync(outgoing, ct);
                if (!response.IsSuccessStatusCode) failure = "provider_http_" + (int)response.StatusCode;
                else
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                        status = "sent";
                    else failure = "provider_response_invalid";
                }
            }
            else if (!string.IsNullOrWhiteSpace(apiKey))
            {
                using var outgoing = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
                outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                outgoing.Headers.TryAddWithoutValidation("Idempotency-Key", id.ToString("N"));
                outgoing.Content = JsonContent.Create(new
                {
                    from, to = new[] { actualRecipient }, subject = actualSubject, text = actualBody
                });
                using var response = await clients.CreateClient("otp-email").SendAsync(outgoing, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (json.RootElement.TryGetProperty("id", out var parsed) && parsed.ValueKind == JsonValueKind.String)
                    {
                        messageId = parsed.GetString();
                        status = "sent";
                    }
                    else failure = "provider_response_invalid";
                }
                else failure = "provider_http_" + (int)response.StatusCode;
            }
            else
            {
                using var message = new MailMessage(from!, actualRecipient, actualSubject, actualBody);
                using var smtp = new SmtpClient(smtpHost!, config.GetValue("Otp:Email:Port", 587))
                {
                    EnableSsl = config.GetValue("Otp:Email:EnableSsl", true),
                    Credentials = new NetworkCredential(config["Otp:Email:Username"], config["Otp:Email:Password"])
                };
                await smtp.SendMailAsync(message, ct);
                status = "sent";
            }
        }
        catch (HttpRequestException) { failure = "provider_network_error"; }
        catch (SmtpException) { failure = "provider_smtp_error"; }
        catch (JsonException) { failure = "provider_response_invalid"; }
        catch (InvalidOperationException) { failure = "provider_configuration_invalid"; }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reporting.feedback_manufacturer_email_dispatches
            SET status={status}, provider_message_id={messageId},failure_code={failure},updated_at=now()
            WHERE id={id} AND case_id={caseId} AND organization_id={organizationId} AND employer_id={employerId}
            """, ct);

        if (status != "sent") return Results.Json(new { error = "manufacturer_inquiry_send_failed", dispatchId = id }, statusCode: 502);
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(
            x => x.Id == caseId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (externalCase is not null)
        {
            externalCase.SetStatus("waiting");
            db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                caseId, testMode ? "manufacturer-mail-test" : "manufacturer-mail-sent",
                testMode ? $"הודעת בדיקה נשלחה לתיבת ALPHA (קוד {item.ErrorCode}); היצרן לא קיבל הודעה." :
                $"פנייה ליצרן נמסרה לספק הדוא״ל (קוד {item.ErrorCode}).", currentUser.UserId));
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { dispatchId = id, status, recipient = actualRecipient, intendedRecipient = item.Recipient, testMode });
    }

    private static async Task<List<Inquiry>?> ResolveAsync(Guid organizationId, Guid employerId, Guid caseId,
        AlphaDbContext db, CancellationToken ct)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            SELECT p."ReportProductId", p."ErrorCode", rp.fund_name,
                   COALESCE(g.manufacturer_name, ''),
                   CASE WHEN po.is_verified THEN po.email
                        WHEN mc.is_verified THEN mc.email ELSE '' END recipient,
                   COALESCE(t.subject, ''), COALESCE(t.body, ''),
                   COALESCE(mr."EmployerLegalNameSnapshot", ''), to_char(mr."ReportingMonth", 'MM/YYYY'), p."ReportId"::text
            FROM reporting.feedback_external_cases c
            JOIN reporting.feedback_external_case_problems p ON p."CaseId"=c."Id"
            JOIN reporting.manual_report_products rp ON rp."Id"=p."ReportProductId"
            JOIN reporting.manual_reports mr ON mr."Id"=p."ReportId" AND mr."OrganizationId"=c."OrganizationId" AND mr."EmployerId"=c."EmployerId"
            LEFT JOIN reference_data.manufacturer_company_groups g
                ON g.company_name=rp.fund_company_name AND g.mapping_status='verified_name'
            LEFT JOIN reference_data.product_contact_email_overrides po
                ON po.external_key=rp.fund_external_key
            LEFT JOIN reference_data.manufacturer_contact_emails mc
                ON mc.manufacturer_name=g.manufacturer_name
            LEFT JOIN reference_data.manufacturer_error_email_templates t
                ON t.error_code=p."ErrorCode" AND t.is_enabled=true
            WHERE c."Id"=@case_id AND c."OrganizationId"=@org AND c."EmployerId"=@employer
            ORDER BY p."CreatedAt"
            """;
        Add(cmd, "case_id", caseId);
        Add(cmd, "org", organizationId);
        Add(cmd, "employer", employerId);
        if (cmd.Connection!.State != ConnectionState.Open) await cmd.Connection.OpenAsync(ct);
        var rows = new List<Inquiry>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0)) continue;
                var productId = reader.GetGuid(0);
                var code = reader.GetInt32(1);
                var product = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var manufacturer = reader.GetString(3);
                var recipient = reader.GetString(4);
                var employer = reader.GetString(7);
                var month = reader.GetString(8);
                var reportId = reader.GetString(9);
                var subject = Render(reader.GetString(5), code, product, manufacturer, employer, month, reportId);
                var body = Render(reader.GetString(6), code, product, manufacturer, employer, month, reportId);
                rows.Add(new Inquiry(productId, code, recipient, manufacturer, product, subject, body));
            }
        }
        // Distinguish a missing case from a case with no product-level problems.
        await using var existence = db.Database.GetDbConnection().CreateCommand();
        existence.CommandText = "SELECT 1 FROM reporting.feedback_external_cases WHERE \"Id\"=@id AND \"OrganizationId\"=@org AND \"EmployerId\"=@employer";
        Add(existence, "id", caseId);
        Add(existence, "org", organizationId);
        Add(existence, "employer", employerId);
        if (existence.Connection!.State != ConnectionState.Open) await existence.Connection.OpenAsync(ct);
        if (await existence.ExecuteScalarAsync(ct) is null) return null;
        return rows.DistinctBy(row => (row.ReportProductId, row.ErrorCode)).ToList();
    }

    private static string Render(string template, int code, string product, string manufacturer, string employer, string month, string reportId)
    {
        return template.Replace("{errorCode}", code.ToString(), StringComparison.Ordinal)
            .Replace("{errorDescription}", EmployerInterfaceLineFeedbackParser.Description(code), StringComparison.Ordinal)
            .Replace("{manufacturerName}", manufacturer, StringComparison.Ordinal)
            .Replace("{productName}", product, StringComparison.Ordinal)
            .Replace("{employerName}", employer, StringComparison.Ordinal)
            .Replace("{reportMonth}", month, StringComparison.Ordinal)
            .Replace("{reportReference}", reportId, StringComparison.Ordinal)
            .Replace("{paymentReference}", "", StringComparison.Ordinal);
    }

    private static async Task<List<History>> ReadHistoryAsync(Guid caseId, AlphaDbContext db, CancellationToken ct)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = """
            SELECT id, recipient, subject, status, failure_code, created_at, body
            FROM reporting.feedback_manufacturer_email_dispatches
            WHERE case_id=@case_id ORDER BY created_at DESC LIMIT 50
            """;
        Add(cmd, "case_id", caseId);
        if (cmd.Connection!.State != ConnectionState.Open) await cmd.Connection.OpenAsync(ct);
        var result = new List<History>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new History(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6)));
        return result;
    }

    private static void Add(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }
}
