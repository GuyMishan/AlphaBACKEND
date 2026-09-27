namespace Alpha.Api.Security;

public interface IDataProtectionService
{
    string Protect(string plaintext, string purpose);
    string Unprotect(string protectedValue, string purpose);
    string LookupHash(string value, string purpose);
}
