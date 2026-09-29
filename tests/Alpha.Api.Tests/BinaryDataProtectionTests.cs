using Xunit;
using Alpha.Api.Security;
using Microsoft.Extensions.Configuration;

namespace Alpha.Api.Tests;

public sealed class BinaryDataProtectionTests
{
    [Fact]
    public void Binary_evidence_round_trips_and_is_not_plaintext()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:DataProtectionKey"] = key }).Build();
        var protector = new AesDataProtectionService(configuration);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("sensitive-v006-payload");
        var encrypted = protector.ProtectBytes(plaintext, "report-transmission:test");
        Assert.NotEqual(plaintext, encrypted);
        Assert.Equal((byte)'A', encrypted[0]); Assert.Equal((byte)'L', encrypted[1]); Assert.Equal((byte)'P', encrypted[2]);
        Assert.Equal(plaintext, protector.UnprotectBytes(encrypted, "report-transmission:test"));
    }

    [Fact]
    public void Legacy_plaintext_bytes_remain_readable_for_backfill()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:DataProtectionKey"] = key }).Build();
        var protector = new AesDataProtectionService(configuration);
        var legacy = System.Text.Encoding.UTF8.GetBytes("legacy");
        Assert.Equal(legacy, protector.UnprotectBytes(legacy, "report-transmission:test"));
    }
}
