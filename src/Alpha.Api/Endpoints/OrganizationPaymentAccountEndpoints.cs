using System.Data.Common;
using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class OrganizationPaymentAccountEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationPaymentAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/payment-account")
            .RequireAuthorization().WithTags("Organization Payment Account");

        group.MapGet("/", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{accountId:guid}", UpdateAsync);
        group.MapGet("/{accountId:guid}/edit", GetForEditAsync);
        group.MapPut("/{accountId:guid}/mandate", UpdateMandateAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(Guid organizationId, AlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct);
        if (account is null) return Results.Ok(new { account = (object?)null });

        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);
        return Results.Ok(new { account = Summary(account, mandate) });
    }

    private static async Task<IResult> GetForEditAsync(Guid organizationId, Guid accountId, AlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct);
        if (account is null) return Results.NotFound();
        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);
        return Results.Ok(new
        {
            account.Id, account.OrganizationId, account.EmployerId, account.BankId, account.BranchId,
            account.AccountNumber, account.AccountHolderName, account.AccountHolderId,
            account.IsDefault, account.IsActive, source = "Organization", mandate = MandateDto(mandate)
        });
    }

    private static async Task<IResult> CreateAsync(Guid organizationId, EmployerPaymentAccountRequest request,
        AlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (!await db.Organizations.AnyAsync(x => x.Id == organizationId, ct)) return Results.NotFound();
        if (!await BankBranchExistsAsync(db, request.BankId, request.BranchId, ct))
            return Results.BadRequest(new { error = "bank_or_branch_not_found" });
        if (await db.EmployerPaymentAccounts.AnyAsync(x => x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct))
            return Results.Conflict(new { error = "organization_payment_account_already_exists" });

        var account = new EmployerPaymentAccount(organizationId, null, request.BankId, request.BranchId,
            request.AccountNumber, request.AccountHolderName, request.AccountHolderId);
        account.SetDefault(true);
        db.EmployerPaymentAccounts.Add(account);
        var mandate = new BankDebitMandate(account.Id);
        db.BankDebitMandates.Add(mandate);
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "organization.payment-account.created",
            nameof(EmployerPaymentAccount), account.Id, organizationId, null,
            JsonSerializer.Serialize(new { account.BankId, account.BranchId }), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/payment-account/{account.Id}", Summary(account, mandate));
    }

    private static async Task<IResult> UpdateAsync(Guid organizationId, Guid accountId,
        EmployerPaymentAccountRequest request, AlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (!await BankBranchExistsAsync(db, request.BankId, request.BranchId, ct))
            return Results.BadRequest(new { error = "bank_or_branch_not_found" });

        var account = await db.EmployerPaymentAccounts.SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct);
        if (account is null) return Results.NotFound();

        account.Update(request.BankId, request.BranchId, request.AccountNumber,
            request.AccountHolderName, request.AccountHolderId);
        account.SetDefault(true);
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "organization.payment-account.updated",
            nameof(EmployerPaymentAccount), account.Id, organizationId, null,
            JsonSerializer.Serialize(new { account.BankId, account.BranchId }), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);
        return Results.Ok(Summary(account, mandate));
    }

    private static async Task<IResult> UpdateMandateAsync(Guid organizationId, Guid accountId,
        BankDebitMandateRequest request, AlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.Status)) return Results.BadRequest(new { error = "invalid_mandate_status" });
        var accountExists = await db.EmployerPaymentAccounts.AnyAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct);
        if (!accountExists) return Results.NotFound();

        var mandate = await db.BankDebitMandates.SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == accountId, ct);
        if (mandate is null)
        {
            mandate = new BankDebitMandate(accountId);
            db.BankDebitMandates.Add(mandate);
        }
        mandate.Update(request.Status, request.ExternalMandateId, request.ApprovedAt, request.CancelledAt, request.DocumentId);
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "organization.payment-account.mandate-updated",
            nameof(BankDebitMandate), mandate.Id, organizationId, null,
            JsonSerializer.Serialize(new { accountId, mandate.Status }), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        return Results.Ok(MandateDto(mandate));
    }

    private static object Summary(EmployerPaymentAccount account, BankDebitMandate? mandate) => new
    {
        account.Id, account.OrganizationId, account.EmployerId, account.BankId, account.BranchId,
        maskedAccountNumber = Mask(account.AccountNumber), account.AccountHolderName,
        maskedAccountHolderId = MaskIdentity(account.AccountHolderId), account.IsDefault, account.IsActive,
        source = "Organization", mandate = MandateDto(mandate), mandateIsActive = mandate?.IsActive ?? false
    };

    private static object? MandateDto(BankDebitMandate? mandate) => mandate is null ? null : new
    {
        mandate.Id, mandate.EmployerPaymentAccountId, mandate.Status, mandate.ExternalMandateId,
        mandate.ApprovedAt, mandate.CancelledAt, mandate.DocumentId, mandate.IsActive
    };

    private static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var visible = value.Length <= 4 ? value : value[^4..];
        return $"••••{visible}";
    }

    private static string MaskIdentity(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var visible = value.Length <= 3 ? value : value[^3..];
        return $"••••••{visible}";
    }

    private static async Task<bool> BankBranchExistsAsync(AlphaDbContext db, int bankId, int branchId, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM reference_data.banks b
                JOIN reference_data.bank_branches br ON br.bank_code = b.bank_code
                WHERE b.bank_code = @bank_id AND br.branch_code = @branch_id
                  AND b.is_active = true AND br.is_active = true
            )
            """;
        AddParameter(command, "bank_id", bankId);
        AddParameter(command, "branch_id", branchId);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
