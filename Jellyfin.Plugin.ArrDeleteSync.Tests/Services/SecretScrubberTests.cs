using System.Collections.Generic;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class SecretScrubberTests
{
    [Fact]
    public void Scrub_RemovesExactSecretOccurrences()
    {
        var result = SecretScrubber.Scrub(
            "Request to http://radarr:7878/api/v3/movie?apikey=abc123 failed",
            new[] { "abc123" });

        Assert.DoesNotContain("abc123", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Scrub_HandlesMultipleSecrets()
    {
        var result = SecretScrubber.Scrub(
            "radarr key aaa111 and seerr key bbb222 both failed",
            new[] { "aaa111", "bbb222" });

        Assert.DoesNotContain("aaa111", result);
        Assert.DoesNotContain("bbb222", result);
    }

    [Fact]
    public void Scrub_HandlesNullOrEmptyInput()
    {
        Assert.Equal(string.Empty, SecretScrubber.Scrub(string.Empty, new[] { "x" }));
        Assert.Null(SecretScrubber.Scrub(null, new[] { "x" }));
    }
}
