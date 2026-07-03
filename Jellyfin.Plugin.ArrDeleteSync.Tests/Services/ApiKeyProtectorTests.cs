using System.IO;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class ApiKeyProtectorTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var keyRingDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-keyring-" + System.Guid.NewGuid());
        Directory.CreateDirectory(keyRingDir);
        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingDir));
            var protector = new ApiKeyProtector(provider);

            var encrypted = protector.Protect("supersecretapikey");

            Assert.NotEqual("supersecretapikey", encrypted);
            Assert.Equal("supersecretapikey", protector.Unprotect(encrypted));
        }
        finally
        {
            Directory.Delete(keyRingDir, recursive: true);
        }
    }

    [Fact]
    public void EncryptedValue_IsNotReadablePlaintext_InRawStorage()
    {
        var keyRingDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-keyring-" + System.Guid.NewGuid());
        Directory.CreateDirectory(keyRingDir);
        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingDir));
            var protector = new ApiKeyProtector(provider);

            var encrypted = protector.Protect("radarr-real-key-value");

            Assert.DoesNotContain("radarr-real-key-value", encrypted);
        }
        finally
        {
            Directory.Delete(keyRingDir, recursive: true);
        }
    }
}
