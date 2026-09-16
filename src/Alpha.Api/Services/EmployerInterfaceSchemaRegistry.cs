using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceSchemaRegistry
{
    public const string Version = "006";

    private static readonly IReadOnlyDictionary<EmployerInterfaceDocumentType, string> OfficialPackageEntries =
        new Dictionary<EmployerInterfaceDocumentType, string>
        {
            [EmployerInterfaceDocumentType.CurrentReport] = "mimshak_maasikim_shotef_xsd_schema_006.xsd.xml",
            [EmployerInterfaceDocumentType.NegativeReport] = "mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml",
            [EmployerInterfaceDocumentType.SummaryFeedback] = "mimshak_maasikim_mesakem_xsd_schema_006.xsd.xml",
            [EmployerInterfaceDocumentType.AnnualSummaryFeedback] = "mimshak_maasikim_mesakem_shnati_xsd_schema_006.xsd.xml"
        };

    private static readonly IReadOnlyDictionary<EmployerInterfaceDocumentType, string[]> FileCandidates =
        new Dictionary<EmployerInterfaceDocumentType, string[]>
        {
            [EmployerInterfaceDocumentType.CurrentReport] = [OfficialPackageEntries[EmployerInterfaceDocumentType.CurrentReport], "employer-current-006.xsd", "Mimshak_Maasikim_Shotef_XSD_Schema_v6.xsd", "1.xsd"],
            [EmployerInterfaceDocumentType.NegativeReport] = [OfficialPackageEntries[EmployerInterfaceDocumentType.NegativeReport], "employer-negative-006.xsd", "Mimshak_Maasikim_Shliliim_XSD_Schema_v6.xsd", "1Neg.xsd"],
            [EmployerInterfaceDocumentType.SummaryFeedback] = [OfficialPackageEntries[EmployerInterfaceDocumentType.SummaryFeedback], "employer-summary-feedback-006.xsd", "Mimshak_Mesakem_XSD_Schema_v6.xsd"],
            [EmployerInterfaceDocumentType.AnnualSummaryFeedback] = [OfficialPackageEntries[EmployerInterfaceDocumentType.AnnualSummaryFeedback], "employer-annual-summary-feedback-006.xsd", "Mimshak_Mesakem_Shnati_XSD_Schema_v6.xsd", "Mimshak_Mesakem_Yearly_XSD_Schema_v6.xsd"]
        };

    private static readonly string[] OfficialPackageParts =
    [
        "official-employer-interface-006.zip.b64.part01",
        "official-employer-interface-006.zip.b64.part02",
        "official-employer-interface-006.zip.b64.part03",
        "official-employer-interface-006.zip.b64.part04"
    ];

    private const string OfficialPackageSha256 = "2cf768191c036031431ce7221ea0bd1ff56532fe59f39d056b4d7b4d2b520c95";

    private readonly string _schemaDirectory;
    private readonly string _sourceDirectory;
    private readonly Dictionary<string, XmlSchemaSet> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private IReadOnlyDictionary<string, byte[]>? _packageEntries;

    public EmployerInterfaceSchemaRegistry()
    {
        _schemaDirectory = Path.Combine(AppContext.BaseDirectory, "Specifications", "EmployerInterface", Version);
        _sourceDirectory = Path.Combine(_schemaDirectory, "source");
    }

    public string SchemaDirectory => _schemaDirectory;

    public IReadOnlyList<string> MissingSchemas() =>
        Enum.GetValues<EmployerInterfaceDocumentType>()
            .Where(type => TryResolveSchema(type) is null)
            .Select(type => $"{type}: official package entry {OfficialPackageEntries[type]} or one of {string.Join(" | ", FileCandidates[type])}")
            .ToArray();

    public SchemaValidation Validate(byte[] xmlBytes, EmployerInterfaceDocumentType documentType)
    {
        var source = TryResolveSchema(documentType);
        if (source is null)
            return new(false, documentType, null,
                [$"Official Employer Interface {Version} XSD is missing for {documentType}. Runtime schema directory: {_schemaDirectory}. Expected the regulator package under {_sourceDirectory} or an extracted XSD file."]);

        try
        {
            var document = LoadXml(xmlBytes);
            var issues = new List<string>();
            document.Validate(GetSchemaSet(source), (_, e) => issues.Add(FormatValidationIssue(e)), true);
            return new(issues.Count == 0, documentType, source.DisplayName, issues);
        }
        catch (XmlSchemaException ex)
        {
            return new(false, documentType, source.DisplayName, [$"XSD schema error: {ex.Message}"]);
        }
        catch (XmlException ex)
        {
            return new(false, documentType, source.DisplayName, [$"Invalid XML: {ex.Message}"]);
        }
        catch (InvalidDataException ex)
        {
            return new(false, documentType, source.DisplayName, [ex.Message]);
        }
    }

    public DetectionResult DetectAndValidate(byte[] xmlBytes)
    {
        try
        {
            _ = LoadXml(xmlBytes);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            return new(null, null, false, [ex.Message], []);
        }

        var availableTypes = Enum.GetValues<EmployerInterfaceDocumentType>()
            .Where(type => TryResolveSchema(type) is not null)
            .ToArray();
        var missing = MissingSchemas();
        if (availableTypes.Length != 4)
            return new(null, null, false,
                [$"Employer Interface {Version} schema set is incomplete. All four official XSD files are required before XML can be accepted.", .. missing], []);

        var attempts = availableTypes.Select(type => Validate(xmlBytes, type)).ToArray();
        var matches = attempts.Where(x => x.IsValid).ToArray();
        if (matches.Length == 1)
            return new(matches[0].DocumentType, matches[0].SchemaFileName, true, [], attempts);
        if (matches.Length > 1)
            return new(null, null, false, ["XML matches more than one Employer Interface schema; document type is ambiguous."], attempts);

        var best = attempts.OrderBy(x => x.Issues.Count).First();
        return new(null, null, false,
            ["XML did not validate against any of the four official Employer Interface 006 schemas.", .. best.Issues.Take(20)], attempts);
    }

    public static XDocument LoadXml(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidDataException("XML payload is empty.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20 * 1024 * 1024,
            IgnoreComments = false,
            IgnoreWhitespace = false
        };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
    }

    private SchemaSource? TryResolveSchema(EmployerInterfaceDocumentType type)
    {
        if (Directory.Exists(_schemaDirectory))
        {
            foreach (var candidate in FileCandidates[type])
            {
                var path = Path.Combine(_schemaDirectory, candidate);
                if (File.Exists(path))
                    return new(candidate, File.ReadAllBytes(path), $"file:{path}:{File.GetLastWriteTimeUtc(path).Ticks}");
            }
        }

        var package = TryLoadOfficialPackage();
        var entryName = OfficialPackageEntries[type];
        if (package is not null && package.TryGetValue(entryName, out var bytes))
            return new(entryName, bytes, $"package:{OfficialPackageSha256}:{entryName}");

        return null;
    }

    private IReadOnlyDictionary<string, byte[]>? TryLoadOfficialPackage()
    {
        lock (_cacheLock)
        {
            if (_packageEntries is not null) return _packageEntries;
            if (!Directory.Exists(_sourceDirectory)) return null;

            var partPaths = OfficialPackageParts.Select(x => Path.Combine(_sourceDirectory, x)).ToArray();
            if (partPaths.Any(path => !File.Exists(path))) return null;

            string encoded;
            try
            {
                encoded = string.Concat(partPaths.Select(File.ReadAllText));
            }
            catch (IOException)
            {
                return null;
            }

            byte[] zipBytes;
            try
            {
                zipBytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The preserved Employer Interface 006 source package is not valid Base64.", ex);
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, OfficialPackageSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Employer Interface 006 source package hash mismatch. Expected {OfficialPackageSha256}, got {actualHash}.");

            using var stream = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (!OfficialPackageEntries.Values.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);
                result[fileName] = buffer.ToArray();
            }

            _packageEntries = result;
            return _packageEntries;
        }
    }

    private XmlSchemaSet GetSchemaSet(SchemaSource source)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(source.CacheKey, out var existing)) return existing;
            var set = new XmlSchemaSet { XmlResolver = null };
            using var stream = new MemoryStream(source.Bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
            set.Compile();
            _cache[source.CacheKey] = set;
            return set;
        }
    }

    private static string FormatValidationIssue(ValidationEventArgs e)
    {
        var ex = e.Exception;
        return ex is null || ex.LineNumber <= 0
            ? $"{e.Severity}: {e.Message}"
            : $"{e.Severity} line {ex.LineNumber}, position {ex.LinePosition}: {e.Message}";
    }

    private sealed record SchemaSource(string DisplayName, byte[] Bytes, string CacheKey);
    public sealed record SchemaValidation(bool IsValid, EmployerInterfaceDocumentType DocumentType, string? SchemaFileName, IReadOnlyList<string> Issues);
    public sealed record DetectionResult(EmployerInterfaceDocumentType? DocumentType, string? SchemaFileName, bool IsValid, IReadOnlyList<string> Issues, IReadOnlyList<SchemaValidation> Attempts);
}
