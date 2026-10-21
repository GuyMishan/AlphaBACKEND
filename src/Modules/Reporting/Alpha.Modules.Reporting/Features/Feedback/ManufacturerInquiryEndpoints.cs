using System.Data;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
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
    public sealed record SendInquiryRequest(Guid ReportProductId, int ErrorCode, string Subject, string Body);
    private sealed record Inquiry(Guid ReportProductId, int ErrorCode, string Recipient, string Manufacturer,
        string Product, string Subject, string Body);
    private sealed record History(Guid Id, string Recipient, string Subject, string Status, string? FailureCode, DateTimeOffset CreatedAt);

    public static RouteGroupBuilder MapManufacturerInquiryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/external-cases/{caseId:guid}/manufacturer-inquiries", GetAsync);
        group.MapPost("/external-cases/{caseId:guid}/manufacturer-inquiries", SendAsync);
        return group;
    }

    private static async Task<IResult> GetAsync(Guid organizationId, Guid employerId, Guid caseId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var rows = await ResolveAsync(organizationId, employerId, caseId, db, ct);
        if (rows is null) return Results.NotFound();
        var history = await ReadHistoryAsync(caseId, db, ct);
        return Results.Ok(new { inquiries = rows, history });
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
        var subject = request.Subject?.Trim() ?? "";
        var body = request.Body?.Trim() ?? "";
        if (subject.Length is < 1 or > 200 || body.Length is < 1 or > 8000 ||
            subject.Contains('\r') || subject.Contains('\n'))
            return Results.BadRequest(new { error = "manufacturer_inquiry_invalid_message" });

        var apiKey = config["ManufacturerMail:ResendApiKey"];
        var from = config["ManufacturerMail:From"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(from))
            return Results.Conflict(new { error = "manufacturer_mail_delivery_not_configured" });

        // Persist before contacting the external provider, preserving an audit trail even on failure.
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reporting.feedback_manufacturer_email_dispatches
              (id,case_id,organization_id,employer_id,report_product_id,error_code,recipient,subject,body,requested_by)
            VALUES ({id},{caseId},{organizationId},{employerId},{item.ReportProductId},{item.ErrorCode},
                    {item.Recipient},{subject},{body},{currentUser.UserId})
            """, ct);

        string status = "failed";
        string? messageId = null;
        string? failure = null;
        try
        {
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            outgoing.Headers.TryAddWithoutValidation("Idempotency-Key", id.ToString("N"));
            outgoing.Content = JsonContent.Create(new
            {
                from, to = new[] { item.Recipient }, subject, text = body
            });
            using var response = await clients.CreateClient().SendAsync(outgoing, ct);
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
        catch (HttpRequestException) { failure = "provider_network_error"; }
        catch (JsonException) { failure = "provider_response_invalid"; }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reporting.feedback_manufacturer_email_dispatches
            SET status={status}, provider_message_id={messageId},failure_code={failure},updated_at=now()
            WHERE id={id} AND case_id={caseId} AND organization_id={organizationId} AND employer_id={employerId}
            """, ct);

        if (status != "sent") return Results.Json(new { error = "manufacturer_inquiry_send_failed", dispatchId = id }, statusCode: 502);
        return Results.Ok(new { dispatchId = id, status, recipient = item.Recipient });
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
                   COALESCE(mr."EmployerLegalNameSnapshot", ''), mr."ReportingMonth"::text, p."ReportId"::text
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
            SELECT id, recipient, subject, status, failure_code, created_at
            FROM reporting.feedback_manufacturer_email_dispatches
            WHERE case_id=@case_id ORDER BY created_at DESC LIMIT 50
            """;
        Add(cmd, "case_id", caseId);
        if (cmd.Connection!.State != ConnectionState.Open) await cmd.Connection.OpenAsync(ct);
        var result = new List<History>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new History(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
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
