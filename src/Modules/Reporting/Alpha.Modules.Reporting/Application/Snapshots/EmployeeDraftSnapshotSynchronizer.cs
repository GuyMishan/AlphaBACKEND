using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Reporting.Contracts;

namespace Alpha.Modules.Reporting.Application.Snapshots;

public sealed class EmployeeDraftSnapshotSynchronizer(AlphaDbContext db) : IEmployeeDraftSnapshotSynchronizer
{
    public Task SyncPostalAddressAsync(
        Guid organizationId,
        Guid employerId,
        Guid employmentId,
        string previousPostalCode,
        string previousPostOfficeBox,
        string currentPostalCode,
        string currentPostOfficeBox,
        CancellationToken cancellationToken = default) =>
        EmployeeDraftSnapshotSync.SyncPostalAddressAsync(
            db,
            organizationId,
            employerId,
            employmentId,
            previousPostalCode,
            previousPostOfficeBox,
            currentPostalCode,
            currentPostOfficeBox,
            cancellationToken);
}
