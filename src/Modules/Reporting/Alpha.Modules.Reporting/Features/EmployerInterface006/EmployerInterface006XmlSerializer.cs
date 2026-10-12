using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Alpha.Modules.Reporting.Features.EmployerInterface006;

public static class EmployerInterface006XmlSerializer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static byte[] Serialize(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = false,
            OmitXmlDeclaration = false,
            NewLineHandling = NewLineHandling.None
        };
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        return stream.ToArray();
    }
}
