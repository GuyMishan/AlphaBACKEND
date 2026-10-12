using Alpha.Api.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alpha.Api.Tests.Security;

public sealed class StartupConfigurationTests
{
    private const string ValidKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string ValidSigningKey = "a-signing-key-that-is-at-least-32-bytes-long";

    [Fact]
    public void Production_requires_a_valid_data_protection_key_and_a_token_validation_source()
    {
        Assert.Empty(StartupConfiguration.Validate(Config(("Security:DataProtectionKey", ValidKey), ("PrototypeAuth:SigningKey", ValidSigningKey)), Env("Production")));
        Assert.Empty(StartupConfiguration.Validate(Config(("Security:DataProtectionKey", ValidKey), ("Authentication:Authority", "https://issuer.test")), Env("Production")));

        Assert.Contains(StartupConfiguration.Validate(Config(("PrototypeAuth:SigningKey", ValidSigningKey)), Env("Production")),
            e => e.Contains("DataProtectionKey is required", StringComparison.Ordinal));
        Assert.Contains(StartupConfiguration.Validate(Config(("Security:DataProtectionKey", "AAAA"), ("PrototypeAuth:SigningKey", ValidSigningKey)), Env("Production")),
            e => e.Contains("exactly 32 bytes", StringComparison.Ordinal));
        Assert.Contains(StartupConfiguration.Validate(Config(("Security:DataProtectionKey", ValidKey)), Env("Production")),
            e => e.Contains("Authentication:Authority", StringComparison.Ordinal));
        Assert.Contains(StartupConfiguration.Validate(Config(("Security:DataProtectionKey", ValidKey), ("PrototypeAuth:SigningKey", "short")), Env("Production")),
            e => e.Contains("at least 32 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void Development_needs_only_the_data_protection_key_and_errors_never_echo_values()
    {
        Assert.Empty(StartupConfiguration.Validate(Config(("Security:DataProtectionKey", ValidKey)), Env("Development")));
        var error = Assert.Throws<InvalidOperationException>(() => StartupConfiguration.EnsureValid(
            Config(("Security:DataProtectionKey", "c2VjcmV0LXZhbHVl"), ("PrototypeAuth:SigningKey", "secret-value")), Env("Production")));
        Assert.DoesNotContain("c2VjcmV0LXZhbHVl", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    private static IHostEnvironment Env(string name) => new TestEnvironment { EnvironmentName = name };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "Alpha.Api";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
