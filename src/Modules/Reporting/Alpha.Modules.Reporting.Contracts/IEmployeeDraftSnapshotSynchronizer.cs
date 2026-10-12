namespace Alpha.Modules.Reporting.Contracts;

public interface IEmployeeDraftSnapshotSynchronizer
{
    Task SyncPostalAddressAsync(
        Guid organizationId,
        Guid employerId,
        Guid employmentId,
        string previousPostalCode,
        string previousPostOfficeBox,
        string currentPostalCode,
        string currentPostOfficeBox,
        CancellationToken cancellationToken = default);
}
