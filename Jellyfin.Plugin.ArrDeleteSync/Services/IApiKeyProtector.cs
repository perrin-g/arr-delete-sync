namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IApiKeyProtector
{
    string Protect(string plaintext);
    string Unprotect(string encrypted);
}
