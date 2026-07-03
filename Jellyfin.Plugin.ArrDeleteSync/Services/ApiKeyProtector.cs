using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class ApiKeyProtector : IApiKeyProtector
{
    private readonly IDataProtector _protector;

    public ApiKeyProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Jellyfin.Plugin.ArrDeleteSync.ApiKeys.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string encrypted) => _protector.Unprotect(encrypted);
}
