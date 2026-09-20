using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public sealed class EmployerPaymentAccount : Entity
{
    private EmployerPaymentAccount() { }

    public EmployerPaymentAccount(Guid organizationId, Guid employerId, int bankId, int branchId,
        string accountNumber, string accountHolderName, string accountHolderId)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("Organization is required.", nameof(organizationId));
        if (employerId == Guid.Empty) throw new ArgumentException("Employer is required.", nameof(employerId));
        OrganizationId = organizationId;
        EmployerId = employerId;
        Update(bankId, branchId, accountNumber, accountHolderName, accountHolderId);
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public int BankId { get; private set; }
    public int BranchId { get; private set; }
    public string AccountNumber { get; private set; } = string.Empty;
    public string AccountHolderName { get; private set; } = string.Empty;
    public string AccountHolderId { get; private set; } = string.Empty;
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;

    public void Update(int bankId, int branchId, string accountNumber, string accountHolderName, string accountHolderId)
    {
        if (bankId <= 0) throw new ArgumentOutOfRangeException(nameof(bankId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        BankId = bankId;
        BranchId = branchId;
        AccountNumber = RequireDigits(accountNumber, nameof(accountNumber), 30);
        AccountHolderName = Require(accountHolderName, nameof(accountHolderName), 150);
        AccountHolderId = RequireDigits(accountHolderId, nameof(accountHolderId), 20);
        Touch();
    }

    public void SetDefault(bool value)
    {
        IsDefault = value;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        IsDefault = false;
        Touch();
    }

    public void Activate()
    {
        IsActive = true;
        Touch();
    }

    private static string Require(string value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, max)];
    }

    private static string RequireDigits(string value, string name, int max)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(digits)) throw new ArgumentException("Value is required.", name);
        return digits[..Math.Min(digits.Length, max)];
    }
}

public enum BankDebitMandateStatus
{
    Pending = 1,
    Active = 2,
    Rejected = 3,
    Cancelled = 4,
    Expired = 5
}

public sealed class BankDebitMandate : Entity
{
    private BankDebitMandate() { }

    public BankDebitMandate(Guid employerPaymentAccountId)
    {
        if (employerPaymentAccountId == Guid.Empty)
            throw new ArgumentException("Payment account is required.", nameof(employerPaymentAccountId));
        EmployerPaymentAccountId = employerPaymentAccountId;
    }

    public Guid EmployerPaymentAccountId { get; private set; }
    public BankDebitMandateStatus Status { get; private set; } = BankDebitMandateStatus.Pending;
    public string ExternalMandateId { get; private set; } = string.Empty;
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string DocumentId { get; private set; } = string.Empty;

    public bool IsActive => Status == BankDebitMandateStatus.Active &&
                            ApprovedAt.HasValue &&
                            (!CancelledAt.HasValue || CancelledAt > DateTimeOffset.UtcNow);

    public void Update(BankDebitMandateStatus status, string? externalMandateId, DateTimeOffset? approvedAt,
        DateTimeOffset? cancelledAt, string? documentId)
    {
        Status = status;
        ExternalMandateId = Clean(externalMandateId, 120);
        ApprovedAt = approvedAt;
        CancelledAt = cancelledAt;
        DocumentId = Clean(documentId, 200);

        if (status == BankDebitMandateStatus.Active && !ApprovedAt.HasValue)
            ApprovedAt = DateTimeOffset.UtcNow;
        if (status == BankDebitMandateStatus.Cancelled && !CancelledAt.HasValue)
            CancelledAt = DateTimeOffset.UtcNow;

        Touch();
    }

    private static string Clean(string? value, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed[..Math.Min(trimmed.Length, max)];
    }
}
