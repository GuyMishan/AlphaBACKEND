using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceSchemaRegistry
{
    public const string Version = "006";

    private static readonly IReadOnlyDictionary<EmployerInterfaceDocumentType, string[]> FileCandidates =
        new Dictionary<EmployerInterfaceDocumentType, string[]>
        {
            [EmployerInterfaceDocumentType.CurrentReport] = ["employer-current-006.xsd", "Mimshak_Maasikim_Shotef_XSD_Schema_v6.xsd", "1.xsd"],
            [EmployerInterfaceDocumentType.NegativeReport] = ["employer-negative-006.xsd", "Mimshak_Maasikim_Shliliim_XSD_Schema_v6.xsd", "1Neg.xsd"],
            [EmployerInterfaceDocumentType.SummaryFeedback] = ["employer-summary-feedback-006.xsd", "Mimshak_Mesakem_XSD_Schema_v6.xsd"],
            [EmployerInterfaceDocumentType.AnnualSummaryFeedback] = ["employer-annual-summary-feedback-006.xsd", "Mimshak_Mesakem_Shnati_XSD_Schema_v6.xsd", "Mimshak_Mesakem_Yearly_XSD_Schema_v6.xsd"]
        };

    private readonly string _schemaDirectory;
    private readonly Dictionary<string, XmlSchemaSet> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public EmployerInterfaceSchemaRegistry()
    {
        _schemaDirectory = Path.Combine(AppContext.BaseDirectory, "Specifications", "EmployerInterface", Version);
    }

    public string SchemaDirectory => _schemaDirectory;

    public IReadOnlyList<string> MissingSchemas() =>
        Enum.GetValues<EmployerInterfaceDocumentType>()
            .Where(type => ResolveSchemaPath(type) is null)
            .Select(type => $"{type}: {string.Join(" | ", FileCandidates[type])}")
            .ToArray();

    public SchemaValidation Validate(byte[] xmlBytes, EmployerInterfaceDocumentType documentType)
    {
        var path = ResolveSchemaPath(documentType);
        if (path is null)
            return new(false, documentType, null, [$"Official Employer Interface {Version} XSD is missing for {documentType}. Expected one of: {string.Join(", ", FileCandidates[documentType])}. Runtime schema directory: {_schemaDirectory}"]);

        try
        {
            var document = LoadXml(xmlBytes);
            var issues = new List<string>();
            document.Validate(GetSchemaSet(path), (_, e) => issues.Add(FormatValidationIssue(e)), true);
            return new(issues.Count == 0, documentType, Path.GetFileName(path), issues);
        }
        catch (XmlSchemaException ex)
        {
            return new(false, documentType, Path.GetFileName(path), [$"XSD schema error: {ex.Message}"]);
        }
        catch (XmlException ex)
        {
            return new(false, documentType, Path.GetFileName(path), [$"Invalid XML: {ex.Message}"]);
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
            .Where(type => ResolveSchemaPath(type) is not null)
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

    private string? ResolveSchemaPath(EmployerInterfaceDocumentType type)
    {
        if (!Directory.Exists(_schemaDirectory)) return null;
        foreach (var candidate in FileCandidates[type])
        {
            var path = Path.Combine(_schemaDirectory, candidate);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private XmlSchemaSet GetSchemaSet(string path)
    {
        lock (_cacheLock)
        {
            var stamp = $"{path}:{File.GetLastWriteTimeUtc(path).Ticks}";
            if (_cache.TryGetValue(stamp, out var existing)) return existing;
            _cache.Clear();
            var set = new XmlSchemaSet { XmlResolver = null };
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
            set.Compile();
            _cache[stamp] = set;
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

    public sealed record SchemaValidation(bool IsValid, EmployerInterfaceDocumentType DocumentType, string? SchemaFileName, IReadOnlyList<string> Issues);
    public sealed record DetectionResult(EmployerInterfaceDocumentType? DocumentType, string? SchemaFileName, bool IsValid, IReadOnlyList<string> Issues, IReadOnlyList<SchemaValidation> Attempts);
}
