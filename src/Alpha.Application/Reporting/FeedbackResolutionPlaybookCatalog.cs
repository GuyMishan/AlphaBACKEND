namespace Alpha.Application.Reporting;

public enum FeedbackResolutionType
{
    Edit,
    Decision,
    External,
    Informational
}

public enum FeedbackResolutionFamily
{
    Informational,
    Identity,
    Employment,
    ProductPolicy,
    Contribution,
    ReportCorrection,
    Payment,
    Refund,
    Documents,
    Split,
    ConditionalField,
    ExternalInstitution
}

public enum FeedbackResolverType
{
    Information,
    Employee,
    EmploymentStatus,
    ProductPolicy,
    Contribution,
    ReportCorrection,
    Payment,
    Refund,
    Documents,
    Split,
    ConditionalField,
    ExternalCase
}

public enum FeedbackResolutionScope
{
    Contribution,
    Deposit,
    Employee,
    Money,
    Report,
    Informational
}

public enum FeedbackResolutionGroupStrategy
{
    PerError,
    PerEmployee,
    PerEmployeeProduct,
    PerContribution,
    PerReport,
    PerEmployer,
    PerTransfer,
    PerDocumentRequirement,
    PerOriginalMovement
}

public enum FeedbackCorrectionBehavior
{
    None,
    RevalidateOnly,
    CorrectionWorkspace,
    PaymentReconciliation,
    ExternalFollowUp,
    Dynamic
}

[Flags]
public enum FeedbackResolutionAction
{
    None = 0,
    EditEmployee = 1 << 0,
    EditEmployment = 1 << 1,
    EditProduct = 1 << 2,
    EditContribution = 1 << 3,
    EditPayment = 1 << 4,
    UploadDocument = 1 << 5,
    Review = 1 << 6,
    Confirm = 1 << 7,
    LinkOriginalRecord = 1 << 8,
    PrepareCorrection = 1 << 9,
    PrepareNegative = 1 << 10,
    Reconcile = 1 << 11,
    OpenExternalCase = 1 << 12,
    RetryAfterResolution = 1 << 13
}

/// <summary>
/// Product routing metadata for a single official Employer Interface 006 summary-feedback code.
/// This catalog does not mutate reports or close feedback. It only describes the resolution
/// route that later orchestration/UI layers may use.
/// </summary>
public sealed record FeedbackResolutionPlaybook(
    int Code,
    FeedbackResolutionType ResolutionType,
    FeedbackResolutionFamily Family,
    FeedbackResolverType Resolver,
    FeedbackResolutionScope Scope,
    FeedbackResolutionGroupStrategy GroupStrategy,
    FeedbackCorrectionBehavior CorrectionBehavior,
    FeedbackResolutionAction Actions,
    bool CanEscalateExternally = false);

/// <summary>
/// ALPHA's product-level resolution routing for the official Employer Interface 006 feedback catalog.
/// Official code meanings stay in the committed V006 specification/parser; this catalog describes
/// how ALPHA intends to route each code through the future resolution workflow.
/// </summary>
public static class FeedbackResolutionPlaybookCatalog
{
    private static FeedbackResolutionPlaybook P(
        int code,
        FeedbackResolutionType type,
        FeedbackResolutionFamily family,
        FeedbackResolverType resolver,
        FeedbackResolutionGroupStrategy group,
        FeedbackCorrectionBehavior correction,
        FeedbackResolutionAction actions,
        bool canEscalateExternally = false) =>
        new(code, type, family, resolver, ScopeFor(code), group, correction, actions, canEscalateExternally);

    public static IReadOnlyList<FeedbackResolutionPlaybook> All { get; } =
    [
        P(1, FeedbackResolutionType.Informational, FeedbackResolutionFamily.Informational, FeedbackResolverType.Information, FeedbackResolutionGroupStrategy.PerError, FeedbackCorrectionBehavior.None, FeedbackResolutionAction.None),
        P(2, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Split, FeedbackResolverType.Split, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.Confirm | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(3, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Split, FeedbackResolverType.Split, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.Confirm | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(4, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Identity, FeedbackResolverType.Employee, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(5, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(6, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(7, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.Reconcile),
        P(11, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Identity, FeedbackResolverType.Employee, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(13, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(15, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(16, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(17, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(18, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ConditionalField, FeedbackResolverType.ConditionalField, FeedbackResolutionGroupStrategy.PerError, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.PrepareCorrection),
        P(19, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Employment, FeedbackResolverType.EmploymentStatus, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditEmployment | FeedbackResolutionAction.PrepareCorrection),
        P(20, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(21, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(23, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(24, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(25, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(26, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(27, FeedbackResolutionType.Edit, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.PrepareCorrection),
        P(28, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.PrepareCorrection),
        P(29, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(30, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(31, FeedbackResolutionType.Informational, FeedbackResolutionFamily.Informational, FeedbackResolverType.Information, FeedbackResolutionGroupStrategy.PerError, FeedbackCorrectionBehavior.None, FeedbackResolutionAction.None),
        P(32, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerReport, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.RetryAfterResolution),
        P(33, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Employment, FeedbackResolverType.EmploymentStatus, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditEmployment | FeedbackResolutionAction.PrepareCorrection),
        P(34, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Employment, FeedbackResolverType.EmploymentStatus, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.OpenExternalCase, true),
        P(39, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution),
        P(42, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(43, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.PrepareCorrection),
        P(44, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Employment, FeedbackResolverType.EmploymentStatus, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditEmployment | FeedbackResolutionAction.PrepareCorrection),
        P(45, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Payment, FeedbackResolverType.Payment, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditPayment | FeedbackResolutionAction.Reconcile | FeedbackResolutionAction.OpenExternalCase, true),
        P(46, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(47, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Employment, FeedbackResolverType.EmploymentStatus, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(48, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Employment, FeedbackResolverType.EmploymentStatus, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.OpenExternalCase, true),
        P(49, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(50, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Payment, FeedbackResolverType.Payment, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.RevalidateOnly, FeedbackResolutionAction.EditPayment | FeedbackResolutionAction.RetryAfterResolution),
        P(51, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Refund, FeedbackResolverType.Refund, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.Review | FeedbackResolutionAction.PrepareNegative),
        P(52, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(53, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(54, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.PrepareCorrection),
        P(55, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(56, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Payment, FeedbackResolverType.Payment, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.EditPayment | FeedbackResolutionAction.Reconcile | FeedbackResolutionAction.OpenExternalCase, true),
        P(57, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Split, FeedbackResolverType.Split, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.Confirm | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(58, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.EditProduct, true),
        P(59, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(61, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(62, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Identity, FeedbackResolverType.Employee, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.PrepareCorrection),
        P(63, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(64, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(66, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(67, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(68, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.OpenExternalCase, true),
        P(69, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(70, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(71, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(72, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(73, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(74, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(75, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(77, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution),
        P(78, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Identity, FeedbackResolverType.Employee, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.OpenExternalCase, true),
        P(79, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Payment, FeedbackResolverType.Payment, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditPayment | FeedbackResolutionAction.Reconcile | FeedbackResolutionAction.OpenExternalCase, true),
        P(80, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.EditProduct, true),
        P(81, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(82, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.Reconcile),
        P(83, FeedbackResolutionType.External, FeedbackResolutionFamily.Payment, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.PaymentReconciliation, FeedbackResolutionAction.Reconcile | FeedbackResolutionAction.OpenExternalCase),
        P(84, FeedbackResolutionType.External, FeedbackResolutionFamily.Payment, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.PaymentReconciliation, FeedbackResolutionAction.Reconcile | FeedbackResolutionAction.OpenExternalCase),
        P(85, FeedbackResolutionType.External, FeedbackResolutionFamily.Payment, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerTransfer, FeedbackCorrectionBehavior.PaymentReconciliation, FeedbackResolutionAction.Reconcile | FeedbackResolutionAction.OpenExternalCase),
        P(86, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Identity, FeedbackResolverType.Employee, FeedbackResolutionGroupStrategy.PerEmployee, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.PrepareCorrection),
        P(87, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(88, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Refund, FeedbackResolverType.Refund, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.PaymentReconciliation, FeedbackResolutionAction.Review | FeedbackResolutionAction.Reconcile),
        P(89, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(90, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(91, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution),
        P(92, FeedbackResolutionType.External, FeedbackResolutionFamily.ExternalInstitution, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(93, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Refund, FeedbackResolverType.Refund, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.Review | FeedbackResolutionAction.PrepareNegative),
        P(94, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(95, FeedbackResolutionType.External, FeedbackResolutionFamily.Refund, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.OpenExternalCase),
        P(96, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Refund, FeedbackResolverType.Refund, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.PaymentReconciliation, FeedbackResolutionAction.Review | FeedbackResolutionAction.Reconcile),
        P(97, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.LinkOriginalRecord | FeedbackResolutionAction.OpenExternalCase, true),
        P(98, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.LinkOriginalRecord | FeedbackResolutionAction.OpenExternalCase, true),
        P(99, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Payment, FeedbackResolverType.Payment, FeedbackResolutionGroupStrategy.PerOriginalMovement, FeedbackCorrectionBehavior.PaymentReconciliation, FeedbackResolutionAction.Review | FeedbackResolutionAction.Reconcile),
        P(100, FeedbackResolutionType.Edit, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerReport, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.PrepareNegative),
        P(101, FeedbackResolutionType.Edit, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerReport, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.PrepareNegative),
        P(102, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(103, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(104, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(105, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(106, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.Reconcile),
        P(107, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(108, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.Reconcile),
        P(109, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(110, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(111, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(112, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement, FeedbackCorrectionBehavior.ExternalFollowUp, FeedbackResolutionAction.UploadDocument | FeedbackResolutionAction.OpenExternalCase | FeedbackResolutionAction.RetryAfterResolution, true),
        P(113, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.CorrectionWorkspace, FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection),
        P(114, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditContribution | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.Reconcile),
        P(115, FeedbackResolutionType.Decision, FeedbackResolutionFamily.ProductPolicy, FeedbackResolverType.ProductPolicy, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true),
        P(116, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Split, FeedbackResolverType.Split, FeedbackResolutionGroupStrategy.PerEmployeeProduct, FeedbackCorrectionBehavior.Dynamic, FeedbackResolutionAction.Review | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.OpenExternalCase, true)
    ];

    private static readonly IReadOnlyDictionary<int, FeedbackResolutionPlaybook> ByCode =
        All.ToDictionary(x => x.Code);

    public static bool TryGet(int code, out FeedbackResolutionPlaybook playbook) =>
        ByCode.TryGetValue(code, out playbook!);

    public static FeedbackResolutionPlaybook Get(int code) =>
        ByCode.TryGetValue(code, out var playbook)
            ? playbook
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown Employer Interface 006 feedback code.");

    private static FeedbackResolutionScope ScopeFor(int code) => code switch
    {
        1 or 31 => FeedbackResolutionScope.Informational,
        4 or 11 or 19 or 33 or 34 or 44 or 47 or 48 or 49 or 62 or 78 or 80 or 86 or 109 or 110 => FeedbackResolutionScope.Employee,
        6 or 7 or 16 or 17 or 20 or 21 or 23 or 53 or 59 or 63 or 64 or 74 or 81 or 82 or 99 or 104 or 105 or 106 or 107 or 108 or 113 or 114 => FeedbackResolutionScope.Contribution,
        45 or 50 or 56 or 79 or 83 or 84 or 85 => FeedbackResolutionScope.Money,
        18 or 100 or 101 or 102 or 103 or 111 or 112 => FeedbackResolutionScope.Report,
        _ => FeedbackResolutionScope.Deposit
    };
}
