namespace Alpha.Api.Security;

/// <summary>
/// Fail-fast checks for configuration without which the API must not serve requests. Optional integrations
/// (payment providers, OTP delivery, malware scanner, storage) are not checked here: they fail closed per request.
/// </summary>
public static class StartupConfiguration
{
    public static IReadOnlyList<string> Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = new List<string>();

        var key = configuration["Security:DataProtectionKey"];
        if (string.IsNullOrWhiteSpace(key))
            errors.Add("Security:DataProtectionKey is required (32-byte Base64 key).");
        else if (!TryDecode(key, out var bytes) || bytes.Length != 32)
            errors.Add("Security:DataProtectionKey must be Base64 and decode to exactly 32 bytes.");

        if (!environment.IsDevelopment())
        {
            var signingKey = configuration["PrototypeAuth:SigningKey"];
            var authority = configuration["Authentication:Authority"];
            if (string.IsNullOrWhiteSpace(signingKey) && string.IsNullOrWhiteSpace(authority))
                errors.Add("Either PrototypeAuth:SigningKey or Authentication:Authority must be configured outside Development.");
            // Token issuing already fails below 32 bytes, so a working deployment always passes this check.
            else if (!string.IsNullOrWhiteSpace(signingKey) && System.Text.Encoding.UTF8.GetByteCount(signingKey) < 32)
                errors.Add("PrototypeAuth:SigningKey must be at least 32 bytes (HMAC-SHA256).");
        }

        return errors;
    }

    /// <summary>Throws with every problem at once; messages name settings, never values.</summary>
    public static void EnsureValid(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = Validate(configuration, environment);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", errors));
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = new byte[value.Length];
        return Convert.TryFromBase64String(value, bytes, out var written) && (bytes = bytes[..written]).Length > 0;
    }
}
