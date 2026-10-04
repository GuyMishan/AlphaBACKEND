namespace Alpha.Api.Services;

/// <summary>
/// Clearing-house initial feedback identifiers and the file-level technical error codes
/// documented for FEDBKA (initial feedback stage A).
/// FEDBKB content-level codes are request/interface-specific and are deliberately not
/// synthesized without the matching official Events Interface specification.
/// </summary>
public static class ClearinghouseInitialFeedbackCatalog
{
    public const string TechnicalInterface = "FEDBKA";
    public const string ContentInterface = "FEDBKB";

    public sealed record TechnicalError(int Code, string Description);

    public static IReadOnlyList<TechnicalError> StageAFileErrors { get; } =
    [
        new(1, "שם קובץ לא תקין"),
        new(2, "קובץ לא קריא"),
        new(3, "מבנה XML לא חוקי"),
        new(4, "היררכיה ראשית בקובץ לא תקינה"),
        new(11, "תאריך קובץ עתידי")
    ];

    public static bool IsStageAFileError(int code) => StageAFileErrors.Any(x => x.Code == code);

    public static string StageADescription(int code) =>
        StageAFileErrors.FirstOrDefault(x => x.Code == code)?.Description
        ?? throw new ArgumentOutOfRangeException(nameof(code), "Unknown FEDBKA file-level technical error code.");

    public static string DuplicateFileDetail(string fileName) =>
        $"קובץ בשם {Path.GetFileName(fileName)} התקבל כקובץ כפול";
}
