using Alpha.Domain.Common;
using Alpha.Domain.Billing;

namespace Alpha.Domain.Subscriptions;

public sealed class Plan : Entity
{
    private Plan() { }

    public Plan(string code, string name, int maxEmployers, int maxEmployees, int maxUsers, bool isActive = true)
    {
        Code = Require(code, nameof(code)).ToUpperInvariant();
        Name = Require(name, nameof(name));
        if (maxEmployers < 0 || maxEmployees < 0 || maxUsers < 0)
            throw new ArgumentOutOfRangeException(nameof(maxEmployers), "Plan limits cannot be negative.");
        MaxEmployers = maxEmployers;
        MaxEmployees = maxEmployees;
        MaxUsers = maxUsers;
        IsActive = isActive;
        EffectiveFrom = DateTimeOffset.UtcNow;
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Currency { get; private set; } = "ILS";
    public string BillingInterval { get; private set; } = "Monthly";
    public int Version { get; private set; } = 1;
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }
    public CorrectionBillingMode CorrectionBillingMode { get; private set; } = CorrectionBillingMode.Free;
    public decimal? CorrectionUnitPrice { get; private set; }
    public decimal IncludedCorrections { get; private set; }
    public decimal IncludedCorrectionRows { get; private set; }
    public int MaxEmployers { get; private set; }
    public int MaxEmployees { get; private set; }
    public int MaxUsers { get; private set; }
    public bool IsActive { get; private set; } = true;

    public void UpdateBillingDefinition(string? description, string? currency, string? billingInterval,
        CorrectionBillingMode correctionBillingMode, decimal? correctionUnitPrice,
        decimal includedCorrections, decimal includedCorrectionRows,
        DateTimeOffset? effectiveFrom = null, DateTimeOffset? effectiveTo = null)
    {
        if (correctionUnitPrice < 0 || includedCorrections < 0 || includedCorrectionRows < 0)
            throw new ArgumentOutOfRangeException(nameof(correctionUnitPrice));
        Description = description?.Trim() ?? string.Empty;
        Currency = string.IsNullOrWhiteSpace(currency) ? "ILS" : currency.Trim().ToUpperInvariant();
        BillingInterval = string.IsNullOrWhiteSpace(billingInterval) ? "Monthly" : billingInterval.Trim();
        CorrectionBillingMode = correctionBillingMode;
        CorrectionUnitPrice = correctionUnitPrice;
        IncludedCorrections = includedCorrections;
        IncludedCorrectionRows = includedCorrectionRows;
        EffectiveFrom = effectiveFrom ?? EffectiveFrom;
        EffectiveTo = effectiveTo;
        Touch();
    }

    public void BumpVersion(DateTimeOffset effectiveFrom)
    {
        Version++;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = null;
        Touch();
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
