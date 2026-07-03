using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public static class SecretScrubber
{
    public static string? Scrub(string? text, IEnumerable<string> secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, "[REDACTED]");
            }
        }
        return result;
    }
}
