using Alpha.Api.Services;
using System.Xml;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterfaceLineFeedbackParserTests
{
    [Fact]
    public void Parses_each_record_status_without_inventing_a_product_match()
    {
        var first = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var second = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var xml = $"<Root><PirteiOved><StatosPirteiKlitatReshuma>" +
            $"<MISPAR-MEZAHE-RESHUMA>{first}</MISPAR-MEZAHE-RESHUMA>" +
            "<RESHUMA-NIKLETA>1</RESHUMA-NIKLETA><SUG-SHGIHA>1</SUG-SHGIHA>" +
            "</StatosPirteiKlitatReshuma><StatosPirteiKlitatReshuma>" +
            $"<MISPAR-MEZAHE-RESHUMA>{second}</MISPAR-MEZAHE-RESHUMA>" +
            "<RESHUMA-NIKLETA>2</RESHUMA-NIKLETA><SUG-SHGIHA>53</SUG-SHGIHA>" +
            "</StatosPirteiKlitatReshuma></PirteiOved></Root>";
        var items = EmployerInterfaceLineFeedbackParser.Parse(xml);
        Assert.Equal(2, items.Count);
        Assert.Equal(first, items[0].RecordIdentifier);
        Assert.Equal(1, items[0].ErrorCode);
        Assert.Equal(second, items[1].RecordIdentifier);
        Assert.Equal(53, items[1].ErrorCode);
        Assert.Contains("השכר", items[1].Description);
    }

    [Fact]
    public void Rejects_dtd_and_does_not_treat_arbitrary_identifiers_as_records()
    {
        Assert.ThrowsAny<XmlException>(() => EmployerInterfaceLineFeedbackParser.Parse(
            "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///etc/passwd'>]><Root>&a;</Root>"));
        Assert.Empty(EmployerInterfaceLineFeedbackParser.Parse(
            "<Root><StatosPirteiKlitatReshuma><MISPAR-MEZAHE-RESHUMA>bad</MISPAR-MEZAHE-RESHUMA></StatosPirteiKlitatReshuma></Root>"));
    }
}
