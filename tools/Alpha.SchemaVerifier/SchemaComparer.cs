namespace Alpha.SchemaVerifier;

public enum Severity
{
    /// <summary>Informational only (names, extensions).</summary>
    Info = 0,
    /// <summary>Compatible but must be understood and documented before release.</summary>
    Review = 1,
    /// <summary>The application would fail or behave incorrectly against this database.</summary>
    Blocking = 2
}

public sealed record SchemaDifference(Severity Severity, string Object, string Expected, string Actual, string Reason);

/// <summary>Compares the schema the release expects with the schema a database actually has.</summary>
public static class SchemaComparer
{
    public static IReadOnlyList<SchemaDifference> Compare(SchemaCatalog expected, SchemaCatalog actual)
    {
        var differences = new List<SchemaDifference>();

        foreach (var schema in expected.Schemas.Except(actual.Schemas))
            differences.Add(new(Severity.Blocking, $"schema {schema}", "present", "missing", "schema required by the application is missing"));

        foreach (var (key, expectedValue) in expected.Objects)
        {
            if (actual.Objects.TryGetValue(key, out var actualValue))
            {
                if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
                    differences.Add(ClassifyChanged(key, expectedValue, actualValue));
            }
            else
            {
                differences.Add(ClassifyMissing(key, expectedValue));
            }
        }

        foreach (var (key, actualValue) in actual.Objects.Where(x => !expected.Objects.ContainsKey(x.Key)))
            differences.Add(ClassifyExtra(key, actualValue));

        foreach (var extension in actual.Extensions.Except(expected.Extensions))
            differences.Add(new(Severity.Info, $"extension {extension}", "", extension, "extension present only in this database"));

        return differences.OrderByDescending(x => x.Severity).ThenBy(x => x.Object, StringComparer.Ordinal).ToArray();
    }

    private static SchemaDifference ClassifyMissing(string key, string expected) => Kind(key) switch
    {
        // A missing non-unique index is a performance concern, not a correctness one.
        "index" when !key.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal) =>
            new(Severity.Review, key, expected, "", "non-unique index missing (performance)"),
        _ => new(Severity.Blocking, key, expected, "", $"{Kind(key)} required by the application is missing")
    };

    private static SchemaDifference ClassifyExtra(string key, string actual) => Kind(key) switch
    {
        // An extra NOT NULL column without default makes the application's INSERTs fail.
        "column" when actual.Contains(" NOT NULL", StringComparison.Ordinal) && !actual.Contains(" DEFAULT ", StringComparison.Ordinal) =>
            new(Severity.Blocking, key, "", actual, "extra NOT NULL column without default; application inserts would fail"),
        "constraint" when key.Contains(" FOREIGN KEY ", StringComparison.Ordinal) || key.Contains(" CHECK ", StringComparison.Ordinal) ||
                          key.Contains(" UNIQUE ", StringComparison.Ordinal) =>
            new(Severity.Review, key, "", actual, "extra constraint can reject writes the application considers valid"),
        "index" when key.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal) =>
            new(Severity.Review, key, "", actual, "extra unique index can reject writes the application considers valid"),
        "trigger" => new(Severity.Review, key, "", actual, "extra trigger changes write behavior"),
        _ => new(Severity.Info, key, "", actual, $"{Kind(key)} present only in this database")
    };

    private static SchemaDifference ClassifyChanged(string key, string expected, string actual)
    {
        if (Kind(key) is "column")
        {
            var (expectedType, expectedNotNull) = ParseColumn(expected);
            var (actualType, actualNotNull) = ParseColumn(actual);
            if (!string.Equals(expectedType, actualType, StringComparison.Ordinal))
                return new(Severity.Blocking, key, expected, actual, "column type differs");
            if (actualNotNull && !expectedNotNull)
                return new(Severity.Blocking, key, expected, actual, "column is NOT NULL but the application may write NULL");
            return new(Severity.Review, key, expected, actual, expectedNotNull && !actualNotNull
                ? "column allows NULL where the application expects NOT NULL"
                : "column default differs");
        }

        // Constraints and indexes are keyed by definition, so a changed value means only the name differs.
        return Kind(key) is "constraint" or "index" or "table" or "sequence"
            ? new(Severity.Info, key, expected, actual, "name or kind differs")
            : new(Severity.Review, key, expected, actual, $"{Kind(key)} definition differs");
    }

    private static string Kind(string key) => key[..key.IndexOf(' ')];

    private static (string Type, bool NotNull) ParseColumn(string value)
    {
        var notNull = value.Contains(" NOT NULL", StringComparison.Ordinal);
        var end = value.IndexOf(notNull ? " NOT NULL" : " NULL", StringComparison.Ordinal);
        return (end < 0 ? value : value[..end], notNull);
    }
}
