namespace Alpha.Modules.Billing.Contracts;

public sealed record BillingInheritanceProjection(
    Guid EmployerId,
    Guid OrganizationId,
    int BillingMode,
    string Source,
    string BilledThroughName,
    bool Configured,
    int? PaymentMethodStatus);

public interface IBillingInheritanceService
{
    Task<BillingInheritanceProjection?> ResolveAsync(
        Guid employerId,
        CancellationToken cancellationToken = default);

    Task ApplyDefaultsForNewEmployerAsync(
        Guid organizationId,
        Guid employerId,
        CancellationToken cancellationToken = default);

    Task NormalizeDefaultsAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
