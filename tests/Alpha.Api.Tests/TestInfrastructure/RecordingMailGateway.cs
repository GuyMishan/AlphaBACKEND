using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Api.Tests.TestInfrastructure;

/// <summary>Stands in for the Google Apps Script mail gateway: records each request body and confirms delivery.</summary>
internal sealed class RecordingMailGateway : HttpMessageHandler
{
    /// <summary>Configuration that routes email through the gateway (the handler replaces the real HTTP call).</summary>
    public static readonly IReadOnlyDictionary<string, string?> Settings = new Dictionary<string, string?>
    {
        ["Otp:Email:AppsScript:Url"] = "https://script.google.com/macros/s/test-deployment/exec",
        ["Otp:Email:AppsScript:Secret"] = new string('s', 40)
    };

    private readonly ConcurrentQueue<string> bodies = new();

    public int Deliveries => bodies.Count;
    public IReadOnlyCollection<string> Bodies => bodies.ToArray();

    public void Install(IServiceCollection services) =>
        services.AddHttpClient("otp-email").ConfigurePrimaryHttpMessageHandler(() => this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bodies.Enqueue(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) };
    }

    // The HTTP client factory disposes handlers when their lifetime ends; the recorder outlives that.
    protected override void Dispose(bool disposing) { }
}
