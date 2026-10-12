namespace Alpha.Modules.IdentityAccess.Contracts;

/// <summary>
/// Application-facing tenant authorization capabilities for the current actor.
/// Implementations must validate organization/resource ownership server-side.
/// </summary>
public interface IOrganizationAuthorization
{
    Task<bool> CanAccessOrganizationScopeAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<bool> CanViewOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<bool> CanManageOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<bool> CanEditOrganizationGeneralAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<bool> HasFullOrganizationEmployerScopeAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<bool> CanAccessEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
    Task<bool> CanCreateEmployerAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<bool> CanManageEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
    Task<bool> CanEditEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
    Task<bool> CanManageEmployerBillingAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);

    Task<bool> CanCreateEmployeeAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
    Task<bool> CanEditEmployeeAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
    Task<bool> CanCreateReportAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
    Task<bool> CanTransmitReportAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken);
}
