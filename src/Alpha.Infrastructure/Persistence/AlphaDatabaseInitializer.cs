using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

/// <summary>
/// Single owner of database startup. The schema initializers are idempotent compatibility DDL
/// and run on every start in the production-pinned order below; changing that order is a
/// reviewed schema change (see docs/database.md).
/// </summary>
public static class AlphaDatabaseInitializer
{
    /// <param name="bootstrapEmptyDatabase">
    /// Creates the EF model on a database that has no tables before the initializers run.
    /// <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.EnsureCreatedAsync"/>
    /// is a no-op when any table already exists, so an existing database is never recreated.
    /// </param>
    public static async Task InitializeAsync(AlphaDbContext db, bool bootstrapEmptyDatabase, CancellationToken ct = default)
    {
        if (bootstrapEmptyDatabase)
            await db.Database.EnsureCreatedAsync(ct);

        await RunSchemaInitializersAsync(db);
    }

    public static async Task RunSchemaInitializersAsync(AlphaDbContext db)
    {
        await IdentitySchemaInitializer.EnsureUpdatedAsync(db);
        await ReferentSchemaInitializer.EnsureUpdatedAsync(db);
        await EmployeeIdentitySchemaInitializer.EnsureUpdatedAsync(db);
        await EmployerAccessSchemaInitializer.EnsureUpdatedAsync(db);
        await AccessPermissionsSchemaInitializer.EnsureUpdatedAsync(db);
        await EmployerProfileSchemaInitializer.EnsureCreatedAsync(db);
        await EmployerDocumentSchemaInitializer.EnsureUpdatedAsync(db);
        await OrganizationProfileSchemaInitializer.EnsureCreatedAsync(db);
        await BillingSchemaInitializer.EnsureCreatedAsync(db);
        await BillingInheritanceSchemaInitializer.EnsureUpdatedAsync(db);
        await BillingV2SchemaInitializer.EnsureUpdatedAsync(db);
        await OtpSchemaInitializer.EnsureCreatedAsync(db);
        await RegistrationOtpSchemaInitializer.EnsureCreatedAsync(db);
        await InvitationSchemaInitializer.EnsureCreatedAsync(db);
        await ReportingSchemaInitializer.EnsureCreatedAsync(db);
        await Section14SchemaInitializer.EnsureUpdatedAsync(db);
        await SalaryAllocationSchemaInitializer.EnsureUpdatedAsync(db);
        await PensionFundSnapshotSchemaInitializer.EnsureUpdatedAsync(db);
        await ReportLifecycleSchemaInitializer.EnsureUpdatedAsync(db);
        await PaymentConfirmationSchemaInitializer.EnsureUpdatedAsync(db);
        await ReportTransmissionSchemaInitializer.EnsureUpdatedAsync(db);
        await EmployerInterfaceFeedbackSchemaInitializer.EnsureCreatedAsync(db);
        await ReportFeedbackOperationsSchemaInitializer.EnsureUpdatedAsync(db);
        await EmployerInterface006SchemaInitializer.EnsureUpdatedAsync(db);
        await ReferenceDataSchemaInitializer.EnsureCreatedAsync(db);
        await SelectOptionsSchemaInitializer.EnsureCreatedAsync(db);
        await EmployerInterface006CodebookInitializer.EnsureUpdatedAsync(db);
        await EmployerInterface006FieldMapInitializer.EnsureUpdatedAsync(db);
        await EmployerInterface006ReferenceRulesInitializer.EnsureUpdatedAsync(db);
        await EmployerInterface006ErrorCodeInitializer.EnsureUpdatedAsync(db);
        await SalaryLayerSchemaInitializer.EnsureCreatedAsync(db);
        await LegalDocumentSchemaInitializer.EnsureCreatedAsync(db);
    }
}
