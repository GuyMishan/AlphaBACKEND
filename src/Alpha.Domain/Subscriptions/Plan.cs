using Alpha.Domain.Common;

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
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public int MaxEmployers { get; private set; }
    public int MaxEmployees { get; private set; }
    public int MaxUsers { get; private set; }
    public bool IsActive { get; private set; } = true;

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
