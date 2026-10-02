using Alpha.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class PaymentProviderResolverTests
{
    [Fact]
    public void Explicit_provider_name_overrides_changed_default_provider()
    {
        var resolver = CreateResolver("PayPlus");

        Assert.Equal("PayPlus", resolver.Resolve().Name);
        Assert.Equal("CardCom", resolver.Resolve("CardCom").Name);
    }

    [Fact]
    public void Provider_names_are_case_insensitive()
    {
        var resolver = CreateResolver("CardCom");

        Assert.Equal("CardCom", resolver.Resolve("cardcom").Name);
        Assert.Equal("PayPlus", resolver.Resolve("PAYPLUS").Name);
    }

    [Fact]
    public void Unknown_provider_is_rejected()
    {
        var resolver = CreateResolver("CardCom");

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("UnknownProvider"));
    }


    [Fact]
    public void Missing_default_provider_fails_closed()
    {
        var resolver = CreateResolver(null);

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve());
    }

    [Fact]
    public void Fake_provider_requires_explicit_opt_in()
    {
        var disabled = CreateResolver("Fake");
        Assert.Throws<InvalidOperationException>(() => disabled.Resolve());

        var enabled = CreateResolver("Fake", allowFake: true);
        Assert.Equal("Fake", enabled.Resolve().Name);
    }

    private static PaymentProviderResolver CreateResolver(string? defaultProvider, bool allowFake = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:DefaultProvider"] = defaultProvider,
                ["Payments:AllowFakeProvider"] = allowFake.ToString()
            })
            .Build();

        var clients = new NoopHttpClientFactory();
        return new PaymentProviderResolver(
            new FakePaymentProvider(),
            new CardComPaymentProvider(clients, configuration),
            new PayPlusPaymentProvider(clients, configuration),
            configuration);
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoopHandler());

        private sealed class NoopHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
