using Alpha.Domain.Common;

namespace Alpha.Domain.Employers;

public enum DebitAuthorizationStatus
{
    NotConfigured = 1,
    Pending = 2,
    Active = 3,
    Revoked = 4
}

public sealed class EmployerPensionPaymentAccount : Entity
{
    private EmployerPensionPaymentAccount() { }

    public EmployerPensionPaymentAccount(Guid organizationId, Guid employerId, string accountName,
        int bankCode, int branchCode, string accountNumber, string accountHolderName)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("Organization is required.", nameof(organizationId));
        if (employerId == Guid.Empty) throw new ArgumentException("Employer is required.", nameof(employerId));
        OrganizationId = organizationId;
        EmployerId = employerId;
        Update(accountName, bankCode, branchCode, accountNumber, accountHolderName);
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public string AccountName { get; private set; } = string.Empty;
    public int BankCode { get; private set; }
    public int BranchCode { get; private set; }
    public string AccountNumber { get; private set; } = string.Empty;
    public string AccountHolderName { get; private set; } = string.Empty;
    public bool IsDefault { get; private set; }
    public DebitAuthorizationStatus DebitAuthorizationStatus { get; private set; } = DebitAuthorizationStatus.NotConfigured;

    public void Update(string accountName, int bankCode, int branchCode, string accountNumber, string accountHolderName)
    {
        if (bankCode <= 0) throw new ArgumentOutOfRangeException(nameof(bankCode));
        if (branchCode <= 0) throw new ArgumentOutOfRangeException(nameof(branchCode));
        AccountName = Require(accountName, nameof(accountName), 100);
        AccountNumber = RequireDigits(accountNumber, nameof(accountNumber), 30);
        AccountHolderName = Require(accountHolderName, nameof(accountHolderName), 150);
        BankCode = bankCode;
        BranchCode = branchCode;
        Touch();
    }

    public void SetDefault(bool value)
    {
        IsDefault = value;
        Touch();
    }

    public void SetDebitAuthorizationStatus(DebitAuthorizationStatus status)
    {
        DebitAuthorizationStatus = status;
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
