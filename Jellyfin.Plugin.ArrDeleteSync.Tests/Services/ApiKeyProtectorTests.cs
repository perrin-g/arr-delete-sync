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

    // PluginConfiguration's *ApiKeyEncrypted fields default to string.Empty, and Unprotect is
    // called eagerly for every configured service at singleton construction time — an
    // unconfigured (empty) key must never throw and crash the whole DI graph.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unprotect_ReturnsEmptyString_ForNullOrEmptyInput(string? encrypted)
    {
        var keyRingDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-keyring-" + System.Guid.NewGuid());
        Directory.CreateDirectory(keyRingDir);
        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingDir));
            var protector = new ApiKeyProtector(provider);

            var result = protector.Unprotect(encrypted!);

            Assert.Equal(string.Empty, result);
        }
        finally
        {
            Directory.Delete(keyRingDir, recursive: true);
        }
    }

    [Fact]
    public void Unprotect_ReturnsEmptyString_WhenDecryptionFails()
    {
        var keyRingDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-keyring-" + System.Guid.NewGuid());
        Directory.CreateDirectory(keyRingDir);
        try
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingDir));
            var protector = new ApiKeyProtector(provider);

            var result = protector.Unprotect("not-a-real-encrypted-value");

            Assert.Equal(string.Empty, result);
        }
        finally
        {
            Directory.Delete(keyRingDir, recursive: true);
        }
    }
}
