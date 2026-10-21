namespace Alpha.Modules.Reporting.Application;

// User-facing availability, independent of internal playbook Decision/Edit/External types.
// An external case is not always a manufacturer e-mail: recipient must be resolved from
// the verified product / institutional route before presenting a send action.
public enum FeedbackTreatmentChoice
{
    CorrectionOnly,
    CorrectionOrExternalInquiry,
    ExternalInquiryOnly,
    InformationOnly
}

public sealed record FeedbackTreatmentPolicy(
    int Code,
    FeedbackTreatmentChoice Choice,
    FeedbackResolverType Resolver,
    FeedbackResolutionGroupStrategy GroupStrategy,
    bool RequiresCaseReview,
    bool CanCorrect,
    bool CanOpenExternalCase);

public static class FeedbackTreatmentPolicyCatalog
{
    // Explicit, exhaustive V006 mapping. Do not infer permissions from FeedbackResolutionType.Decision:
    // Review/Confirm alone is not a business correction or an error-closing action.
    private static readonly int[] CorrectionOnlyCodes = [6, 7, 16, 17, 18, 19, 20, 21, 23, 27, 28, 32, 33, 43, 44, 51, 53, 54, 59, 62, 71, 72, 73, 74, 75, 81, 82, 86, 88, 93, 96, 99, 100, 101, 104, 105, 106, 107, 108, 113, 114];
    private static readonly int[] CorrectionOrExternalInquiryCodes = [2, 3, 4, 5, 11, 15, 24, 25, 26, 42, 45, 47, 49, 50, 55, 56, 57, 58, 61, 63, 64, 67, 68, 69, 70, 78, 79, 80, 83, 84, 85, 97, 98, 102, 103, 109, 110, 111, 112, 115, 116];
    private static readonly int[] ExternalInquiryOnlyCodes = [13, 29, 30, 34, 39, 46, 48, 52, 66, 77, 87, 89, 90, 91, 92, 94, 95];
    private static readonly int[] InformationOnlyCodes = [1, 31];

    private static readonly IReadOnlyDictionary<int, FeedbackTreatmentPolicy> ByCode = Build();

    public static IReadOnlyCollection<FeedbackTreatmentPolicy> All => ByCode.Values.ToArray();

    public static bool TryGet(int code, out FeedbackTreatmentPolicy policy) =>
        ByCode.TryGetValue(code, out policy!);

    public static FeedbackTreatmentPolicy Get(int code) =>
        ByCode.TryGetValue(code, out var policy)
            ? policy
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown V006 feedback error code.");

    private static IReadOnlyDictionary<int, FeedbackTreatmentPolicy> Build()
    {
        var result = new Dictionary<int, FeedbackTreatmentPolicy>();
        Add(CorrectionOnlyCodes, FeedbackTreatmentChoice.CorrectionOnly);
        Add(CorrectionOrExternalInquiryCodes, FeedbackTreatmentChoice.CorrectionOrExternalInquiry);
        Add(ExternalInquiryOnlyCodes, FeedbackTreatmentChoice.ExternalInquiryOnly);
        Add(InformationOnlyCodes, FeedbackTreatmentChoice.InformationOnly);

        var catalogCodes = FeedbackResolutionPlaybookCatalog.All.Select(item => item.Code).OrderBy(x => x);
        if (!result.Keys.OrderBy(x => x).SequenceEqual(catalogCodes))
            throw new InvalidOperationException("Feedback treatment choices must cover every official playbook code exactly once.");

        return result;

        void Add(IEnumerable<int> codes, FeedbackTreatmentChoice choice)
        {
            foreach (var code in codes)
            {
                var playbook = FeedbackResolutionPlaybookCatalog.Get(code);
                var canCorrect = choice is FeedbackTreatmentChoice.CorrectionOnly
                    or FeedbackTreatmentChoice.CorrectionOrExternalInquiry;
                var canInquire = choice is FeedbackTreatmentChoice.CorrectionOrExternalInquiry
                    or FeedbackTreatmentChoice.ExternalInquiryOnly;
                if (canInquire != playbook.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase))
                    throw new InvalidOperationException($"External-case action mismatch for V006 code {code}.");
                if (!result.TryAdd(code, new FeedbackTreatmentPolicy(
                    code, choice, playbook.Resolver, playbook.GroupStrategy,
                    playbook.ResolutionType == FeedbackResolutionType.Decision,
                    canCorrect, canInquire)))
                    throw new InvalidOperationException($"Duplicate V006 treatment classification: {code}");
            }
        }
    }
}
