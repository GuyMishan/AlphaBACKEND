namespace Alpha.Modules.Billing.Contracts;

public sealed record TransmissionBillingGateDecision(
    bool Allowed,
    string? Error,
    string? BillingMode,
    string? Source,
    string? BilledThroughName,
    int? PaymentMethodType,
    int? PaymentMethodStatus,
    bool Configured);

public interface ITransmissionBillingGate
{
    Task<TransmissionBillingGateDecision> CanTransmitAsync(
        Guid employerId,
        CancellationToken cancellationToken = default);
}
