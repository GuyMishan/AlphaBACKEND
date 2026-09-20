using System.Net;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Alpha.Api.Services;

public sealed class OtpDelivery(IConfiguration configuration, IHttpClientFactory clients)
{
    public async Task SendAsync(string channel, string destination, string code, CancellationToken ct)
    {
        if (channel == "sms")
        {
            var url = configuration["Otp:Sms:Url"];
            var token = configuration["Otp:Sms:Token"];
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("SMS delivery is not configured.");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new { to = destination, message = $"קוד הכניסה שלך למערכת Alpha: {code}. הקוד תקף ל-5 דקות." });
            using var response = await clients.CreateClient("otp-sms").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        else if (channel == "email")
        {
            var apiKey = configuration["Otp:Email:ResendApiKey"];
            var fromAddress = configuration["Otp:Email:From"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (string.IsNullOrWhiteSpace(fromAddress)) throw new InvalidOperationException("Email sender is not configured.");
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = JsonContent.Create(new { from = fromAddress, to = new[] { destination }, subject = "קוד כניסה למערכת Alpha", text = $"קוד הכניסה שלך: {code}\nהקוד תקף ל-5 דקות." });
                using var response = await clients.CreateClient("otp-email").SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return;
            }
            var host = configuration["Otp:Email:Host"];
            var from = configuration["Otp:Email:From"];
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
                throw new InvalidOperationException("Email delivery is not configured.");
            using var message = new MailMessage(from, destination, "קוד כניסה למערכת Alpha", $"קוד הכניסה שלך: {code}\nהקוד תקף ל-5 דקות.");
            using var smtp = new SmtpClient(host, configuration.GetValue("Otp:Email:Port", 587))
            {
                EnableSsl = configuration.GetValue("Otp:Email:EnableSsl", true),
                Credentials = new NetworkCredential(configuration["Otp:Email:Username"], configuration["Otp:Email:Password"])
            };
            await smtp.SendMailAsync(message, ct);
        }
        else throw new ArgumentOutOfRangeException(nameof(channel));
    }
}
