using System.Text.RegularExpressions;
using Xunit;
using static Alpha.Architecture.Tests.Repository;

namespace Alpha.Architecture.Tests;

/// <summary>
/// The Google Apps Script mail gateway is deployed by hand from <c>integrations/google-apps-script/Code.gs</c>; every
/// message type the backend posts to it must be one the script accepts, or that delivery silently fails in production.
/// </summary>
public sealed class MailGatewayContractTests
{
    [Fact]
    public void Gateway_script_accepts_every_message_type_the_backend_sends()
    {
        var delivery = Read("src/Modules/IdentityAccess/Alpha.Modules.IdentityAccess/Infrastructure/Delivery/OtpDelivery.cs");
        var script = Read("integrations/google-apps-script/Code.gs");

        var sentTypes = Regex.Matches(delivery, @"\btype\s*=\s*""(?<type>[a-z]+)""").Select(m => m.Groups["type"].Value).Distinct().ToArray();
        Assert.Contains("invitation", sentTypes);
        foreach (var type in sentTypes)
            Assert.Matches($@"body\.type\s*===\s*'{type}'", script);
    }
}
