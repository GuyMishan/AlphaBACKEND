namespace Alpha.Api.Tests.TestInfrastructure;

/// <summary>Reads production source for characterization tests that pin security-relevant code patterns.</summary>
internal static class RepositorySource
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AlphaBackend.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    });

    /// <summary>
    /// Returns the file's text followed by every partial sibling (<c>Name.*.cs</c>), so a pinned pattern
    /// keeps applying to the whole type after it is split by capability.
    /// </summary>
    public static string ReadType(string relativePath)
    {
        var path = Path.Combine(RootPath.Value, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var siblings = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.*.cs")
            .Order(StringComparer.Ordinal);
        return string.Join('\n', new[] { path }.Concat(siblings).Select(File.ReadAllText));
    }

    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RootPath.Value, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
