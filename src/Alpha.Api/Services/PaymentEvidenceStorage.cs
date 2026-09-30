using System.Net.Http.Headers;

namespace Alpha.Api.Services;

/// <summary>Private service-role access only. Never expose the service key or Storage paths to browsers.</summary>
public sealed class PaymentEvidenceStorage(IHttpClientFactory clients, IConfiguration config)
{
    private string? Endpoint => config["Storage:SupabaseUrl"]?.TrimEnd('/');
    private string? Key => config["Storage:ServiceRoleKey"];
    private string Bucket => config["Storage:PaymentEvidenceBucket"] ?? "alpha-payment-evidence";
    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Key);

    private HttpRequestMessage Create(HttpMethod method, string suffix)
    {
        if (!IsConfigured) throw new InvalidOperationException("Private evidence storage is not configured.");
        var request = new HttpRequestMessage(method, $"{Endpoint}/storage/v1/object/{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        request.Headers.Add("apikey", Key);
        return request;
    }
    public async Task<bool> UploadAsync(string path, byte[] data, string contentType, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Post, $"{Uri.EscapeDataString(Bucket)}/{path}");
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await clients.CreateClient("payment-evidence-storage").SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }
    public async Task<byte[]?> DownloadAsync(string path, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, $"authenticated/{Uri.EscapeDataString(Bucket)}/{path}");
        using var response = await clients.CreateClient("payment-evidence-storage").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    public async Task DeleteOrphanAsync(string path, CancellationToken ct)
    {
        if (!IsConfigured) return;
        using var request = Create(HttpMethod.Delete, Uri.EscapeDataString(Bucket));
        request.Content = System.Net.Http.Json.JsonContent.Create(new { prefixes = new[] { path } });
        using var response = await clients.CreateClient("payment-evidence-storage").SendAsync(request, ct);
    }
}
