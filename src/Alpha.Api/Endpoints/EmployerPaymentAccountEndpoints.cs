using System.Data.Common;
using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record EmployerPaymentAccountRequest(
    int BankId,
    int BranchId,
    string AccountNumber,
    string AccountHolderName,
    string AccountHolderId,
    bool IsDefault);

public sealed record BankDebitMandateRequest(
    BankDebitMandateStatus Status,
    string? ExternalMandateId,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? CancelledAt,
    string? DocumentId);

public static class EmployerPaymentAccountEndpoints
{
    public static IEndpointRouteBuilder MapEmployerPaymentAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/payment-accounts")
            .RequireAuthorization().WithTags("Employer Payment Accounts");

        group.MapGet("/", ListAsync);
        group.MapGet("/resolution", ResolveAsync);
        group.MapGet("/{accountId:guid}", GetForEditAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{accountId:guid}", UpdateAsync);
        group.MapPost("/{accountId:guid}/set-default", SetDefaultAsync);
        group.MapDelete("/{accountId:guid}", DeactivateAsync);
        group.MapPut("/{accountId:guid}/mandate", UpdateMandateAsync);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid organizationId, Guid employerId, AlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var resolution = await ResolveAccountAsync(organizationId, employerId, db, ct);
        return Results.Ok(resolution.Account is null
            ? Array.Empty<object>()
            : new[] { AccountSummary(resolution.Account, resolution.Mandate, resolution.Source) });
    }

    private static async Task<IResult> ResolveAsync(Guid organizationId, Guid employerId, AlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();

        var resolution = await ResolveAccountAsync(organizationId, employerId, db, ct);
        var organizationAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct);
        var employerAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        var organizationMandate = organizationAccount is null ? null : await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == organizationAccount.Id, ct);
        var employerMandate = employerAccount is null ? null : await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == employerAccount.Id, ct);

        return Results.Ok(new
        {
            employerId,
            organizationId,
            mode = resolution.Mode,
            source = resolution.Source,
            inherited = resolution.Source == "Organization",
            account = resolution.Account is null ? null : AccountSummary(resolution.Account, resolution.Mandate, resolution.Source),
            organizationAccount = organizationAccount is null ? null : AccountSummary(organizationAccount, organizationMandate, "Organization"),
            employerAccount = employerAccount is null ? null : AccountSummary(employerAccount, employerMandate, "Employer")
        });
    }

    private static async Task<IResult> GetForEditAsync(Guid organizationId, Guid employerId, Guid accountId,
        AlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var account = await db.EmployerPaymentAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        if (account is null) return Results.NotFound();

        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);

        return Results.Ok(new
        {
            account.Id,
            account.OrganizationId,
            account.EmployerId,
            account.BankId,
            account.BranchId,
            account.AccountNumber,
            account.AccountHolderName,
            account.AccountHolderId,
            account.IsDefault,
            account.IsActive,
            mandate = MandateDto(mandate)
        });
    }

    private static async Task<IResult> CreateAsync(Guid organizationId, Guid employerId,
        EmployerPaymentAccountRequest request, AlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();
        if (!await BankBranchExistsAsync(db, request.BankId, request.BranchId, ct))
            return Results.BadRequest(new { error = "bank_or_branch_not_found" });

        if (await db.EmployerPaymentAccounts.AnyAsync(x =>
                x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct))
            return Results.Conflict(new { error = "employer_payment_account_already_exists" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockEmployerAsync(db, employerId, ct);

        EmployerPaymentAccount account;
        try
        {
            account = new EmployerPaymentAccount(organizationId, employerId, request.BankId, request.BranchId,
                request.AccountNumber, request.AccountHolderName, request.AccountHolderId);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var hasActive = await db.EmployerPaymentAccounts.AnyAsync(x => x.EmployerId == employerId && x.IsActive, ct);
        if (request.IsDefault || !hasActive)
        {
            await ClearDefaultAsync(db, employerId, null, ct);
            account.SetDefault(true);
        }

        db.EmployerPaymentAccounts.Add(account);
        var mandate = new BankDebitMandate(account.Id);
        db.BankDebitMandates.Add(mandate);

        AddAudit(db, currentUser, http, "employer.payment-account.created", account.Id, organizationId, employerId,
            new { account.BankId, account.BranchId, account.IsDefault });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/payment-accounts/{account.Id}",
            AccountSummary(account, mandate));
    }

    private static async Task<IResult> UpdateAsync(Guid organizationId, Guid employerId, Guid accountId,
        EmployerPaymentAccountRequest request, AlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await BankBranchExistsAsync(db, request.BankId, request.BranchId, ct))
            return Results.BadRequest(new { error = "bank_or_branch_not_found" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockEmployerAsync(db, employerId, ct);

        var account = await db.EmployerPaymentAccounts.SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        if (account is null) return Results.NotFound();

        try
        {
            account.Update(request.BankId, request.BranchId, request.AccountNumber,
                request.AccountHolderName, request.AccountHolderId);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (request.IsDefault && !account.IsDefault)
        {
            await ClearDefaultAsync(db, employerId, account.Id, ct);
            account.SetDefault(true);
        }
        else if (!request.IsDefault && account.IsDefault)
        {
            return Results.Conflict(new { error = "default_account_required" });
        }

        AddAudit(db, currentUser, http, "employer.payment-account.updated", account.Id, organizationId, employerId,
            new { account.BankId, account.BranchId, account.IsDefault });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var mandate = await db.BankDebitMandates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);
        return Results.Ok(AccountSummary(account, mandate));
    }

    private static async Task<IResult> SetDefaultAsync(Guid organizationId, Guid employerId, Guid accountId,
        AlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockEmployerAsync(db, employerId, ct);

        var account = await db.EmployerPaymentAccounts.SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        if (account is null) return Results.NotFound();

        await ClearDefaultAsync(db, employerId, account.Id, ct);
        account.SetDefault(true);
        AddAudit(db, currentUser, http, "employer.payment-account.default-changed", account.Id,
            organizationId, employerId, new { accountId });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateAsync(Guid organizationId, Guid employerId, Guid accountId,
        AlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockEmployerAsync(db, employerId, ct);

        var account = await db.EmployerPaymentAccounts.SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        if (account is null) return Results.NoContent();

        var wasDefault = account.IsDefault;
        account.Deactivate();

        var mandate = await db.BankDebitMandates.SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);
        if (mandate is not null && mandate.Status is BankDebitMandateStatus.Pending or BankDebitMandateStatus.Active)
            mandate.Update(BankDebitMandateStatus.Cancelled, mandate.ExternalMandateId, mandate.ApprovedAt,
                DateTimeOffset.UtcNow, mandate.DocumentId);

        if (wasDefault)
        {
            var replacement = await db.EmployerPaymentAccounts
                .Where(x => x.EmployerId == employerId && x.IsActive && x.Id != account.Id)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            replacement?.SetDefault(true);
        }

        AddAudit(db, currentUser, http, "employer.payment-account.deactivated", account.Id,
            organizationId, employerId, new { accountId });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateMandateAsync(Guid organizationId, Guid employerId, Guid accountId,
        BankDebitMandateRequest request, AlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.Status)) return Results.BadRequest(new { error = "invalid_mandate_status" });

        var accountExists = await db.EmployerPaymentAccounts.AnyAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        if (!accountExists) return Results.NotFound();

        var mandate = await db.BankDebitMandates.SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == accountId, ct);
        if (mandate is null)
        {
            mandate = new BankDebitMandate(accountId);
            db.BankDebitMandates.Add(mandate);
        }

        mandate.Update(request.Status, request.ExternalMandateId, request.ApprovedAt, request.CancelledAt, request.DocumentId);
        AddAudit(db, currentUser, http, "employer.payment-account.mandate-updated", mandate.Id,
            organizationId, employerId, new { accountId, mandate.Status, mandate.ExternalMandateId, mandate.DocumentId });
        await db.SaveChangesAsync(ct);

        return Results.Ok(MandateDto(mandate));
    }

    private static object AccountSummary(EmployerPaymentAccount account, BankDebitMandate? mandate, string source = "Employer") => new
    {
        account.Id,
        account.OrganizationId,
        account.EmployerId,
        account.BankId,
        account.BranchId,
        maskedAccountNumber = Mask(account.AccountNumber),
        account.AccountHolderName,
        maskedAccountHolderId = MaskIdentity(account.AccountHolderId),
        account.IsDefault,
        account.IsActive,
        source,
        mandate = MandateDto(mandate),
        mandateIsActive = mandate?.IsActive ?? false
    };

    private sealed record PaymentAccountResolution(
        EmployerPensionPaymentMode Mode,
        string Source,
        EmployerPaymentAccount? Account,
        BankDebitMandate? Mandate);

    private static async Task<PaymentAccountResolution> ResolveAccountAsync(
        Guid organizationId, Guid employerId, AlphaDbContext db, CancellationToken ct)
    {
        var settings = await db.EmployerProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employerId, ct);
        var directAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);

        var mode = settings?.PensionPaymentMode
            ?? (directAccount is not null ? EmployerPensionPaymentMode.EmployerDirect : EmployerPensionPaymentMode.InheritOrganization);

        EmployerPaymentAccount? account;
        string source;
        if (mode == EmployerPensionPaymentMode.EmployerDirect)
        {
            account = directAccount;
            source = "Employer";
        }
        else
        {
            account = await db.EmployerPaymentAccounts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null && x.IsActive, ct);
            source = "Organization";
        }

        BankDebitMandate? mandate = null;
        if (account is not null)
            mandate = await db.BankDebitMandates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.EmployerPaymentAccountId == account.Id, ct);

        return new PaymentAccountResolution(mode, source, account, mandate);
    }

    private static object? MandateDto(BankDebitMandate? mandate) => mandate is null ? null : new
    {
        mandate.Id,
        mandate.EmployerPaymentAccountId,
        mandate.Status,
        mandate.ExternalMandateId,
        mandate.ApprovedAt,
        mandate.CancelledAt,
        mandate.DocumentId,
        mandate.IsActive
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

    private static async Task ClearDefaultAsync(AlphaDbContext db, Guid employerId, Guid? exceptId, CancellationToken ct)
    {
        var defaults = await db.EmployerPaymentAccounts
            .Where(x => x.EmployerId == employerId && x.IsActive && x.IsDefault &&
                        (!exceptId.HasValue || x.Id != exceptId.Value))
            .ToListAsync(ct);
        foreach (var item in defaults) item.SetDefault(false);
    }

    private static async Task LockEmployerAsync(AlphaDbContext db, Guid employerId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({employerId.ToString()}))", ct);

    private static async Task<bool> BankBranchExistsAsync(AlphaDbContext db, int bankId, int branchId, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM reference_data.banks b
                JOIN reference_data.bank_branches br ON br.bank_code = b.bank_code
                WHERE b.bank_code = @bank_id
                  AND br.branch_code = @branch_id
                  AND b.is_active = true
                  AND br.is_active = true
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

    private static void AddAudit(AlphaDbContext db, ICurrentUser currentUser, HttpContext http,
        string action, Guid entityId, Guid organizationId, Guid employerId, object details)
    {
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, action, "EmployerPaymentAccount", entityId,
            organizationId, employerId, JsonSerializer.Serialize(details), http.TraceIdentifier));
    }
}
