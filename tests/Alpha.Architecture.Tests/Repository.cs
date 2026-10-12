using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Alpha.Architecture.Tests;

/// <summary>Read-only access to repository sources and project files. No production assembly is loaded.</summary>
internal static class Repository
{
    public static readonly string Root = FindRoot();

    public static readonly string[] BusinessModules =
        ["IdentityAccess", "Organizations", "Workforce", "Billing", "Reporting", "ReferenceData", "Auditing"];

    public static string Path(params string[] segments) =>
        System.IO.Path.Combine([Root, .. segments.SelectMany(s => s.Split('/'))]);

    public static string Read(params string[] segments) => File.ReadAllText(Path(segments));

    public static IEnumerable<string> SourceFiles(string relativeDirectory) =>
        Directory.EnumerateFiles(Path(relativeDirectory), "*.cs", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p));

    public static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p));

    /// <summary>"from.csproj -> to.csproj" pairs, repository-relative with forward slashes.</summary>
    public static IEnumerable<(string From, string To)> ProjectReferences() =>
        ProjectFiles().SelectMany(project => XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => (Relative(project),
                Relative(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, include!))))));

    public static string Relative(string path) => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');

    public static bool IsBuildOutput(string path) =>
        path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj");

    public static string Fingerprint(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();

    /// <summary>The business module that owns a repository-relative path, or null for shared projects.</summary>
    public static string? ModuleOf(string relativePath)
    {
        const string prefix = "src/Modules/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return relativePath[prefix.Length..].Split('/')[0];
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "AlphaBackend.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
