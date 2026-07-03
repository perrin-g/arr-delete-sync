using System;
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

    // Degrades gracefully rather than throwing: PluginConfiguration's *ApiKeyEncrypted fields
    // default to string.Empty, and this is called eagerly for all configured arr/Seerr services
    // at singleton construction time — an admin who hasn't configured every service yet (or a
    // fresh install) must not crash the whole DI graph just from resolving IArrClientFactory /
    // ISeerrClient. An empty return here just means ArrClient/SeerrClient get 401s from the real
    // API, which they already handle safely (see ArrClient.FindByProviderIdAsync — any
    // non-success response collapses to ArrTrackingState.Indeterminate, never an exception).
    public string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            return string.Empty;
        }

        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
