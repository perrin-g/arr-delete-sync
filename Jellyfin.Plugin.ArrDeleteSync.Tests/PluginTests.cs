using System;
using System.IO;
using Jellyfin.Plugin.ArrDeleteSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests;

// Regression coverage for Plugin.UpdateConfiguration: the security-critical hook that
// encrypts freshly-submitted plaintext API keys and blanks the plaintext field before
// BasePlugin<T>.UpdateConfiguration persists the configuration to PluginConfiguration.xml.
// This guarantees plaintext API keys never hit disk. A real Plugin is constructed against
// a temp directory (for the DataProtectionProvider key ring) with IApplicationPaths and
// IXmlSerializer mocked, mirroring the throwaway probe the original implementer used to
// verify this behavior (see Plugin.cs's UpdateConfiguration doc comment).
public class PluginTests
{
    private static Plugin CreatePlugin(out string dataPath)
    {
        dataPath = Path.Combine(Path.GetTempPath(), "arrdeletesync-plugintests-" + Guid.NewGuid());
        Directory.CreateDirectory(dataPath);

        // BasePlugin<T>'s constructor needs PluginsPath (to compute the plugin's data
        // folder) and PluginConfigurationsPath (to compute the path to
        // PluginConfiguration.xml), in addition to DataPath (used by Plugin.cs to build
        // the Data Protection key ring path) — a loose mock returns null for all three
        // unless set, which blows up Path.Combine inside the base constructor.
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.Setup(p => p.DataPath).Returns(dataPath);
        applicationPaths.Setup(p => p.PluginConfigurationsPath).Returns(dataPath);
        applicationPaths.Setup(p => p.PluginsPath).Returns(dataPath);

        var xmlSerializer = new Mock<IXmlSerializer>(MockBehavior.Loose);

        return new Plugin(applicationPaths.Object, xmlSerializer.Object);
    }

    [Fact]
    public void UpdateConfiguration_WithRadarrPlaintextKeySet_EncryptsAndClearsPlaintext()
    {
        var plugin = CreatePlugin(out var dataPath);
        try
        {
            var config = new PluginConfiguration { RadarrApiKeyPlaintext = "my-secret-key" };

            plugin.UpdateConfiguration(config);

            Assert.False(string.IsNullOrEmpty(config.RadarrApiKeyEncrypted));
            Assert.NotEqual("my-secret-key", config.RadarrApiKeyEncrypted);
            Assert.Equal(string.Empty, config.RadarrApiKeyPlaintext);
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void UpdateConfiguration_WithEmptyPlaintextKey_LeavesExistingEncryptedValueUntouched()
    {
        var plugin = CreatePlugin(out var dataPath);
        try
        {
            var config = new PluginConfiguration
            {
                RadarrApiKeyPlaintext = string.Empty,
                RadarrApiKeyEncrypted = "already-encrypted-value"
            };

            plugin.UpdateConfiguration(config);

            Assert.Equal("already-encrypted-value", config.RadarrApiKeyEncrypted);
            Assert.Equal(string.Empty, config.RadarrApiKeyPlaintext);
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public void UpdateConfiguration_WithOnlySonarrPlaintextKeySet_DoesNotCrossWireOtherProviders()
    {
        var plugin = CreatePlugin(out var dataPath);
        try
        {
            var config = new PluginConfiguration
            {
                SonarrApiKeyPlaintext = "sonarr-secret-key",
                RadarrApiKeyEncrypted = "radarr-existing-encrypted",
                SeerrApiKeyEncrypted = "seerr-existing-encrypted"
            };

            plugin.UpdateConfiguration(config);

            Assert.False(string.IsNullOrEmpty(config.SonarrApiKeyEncrypted));
            Assert.NotEqual("sonarr-secret-key", config.SonarrApiKeyEncrypted);
            Assert.Equal(string.Empty, config.SonarrApiKeyPlaintext);

            // Unrelated providers' already-encrypted values must be left alone.
            Assert.Equal("radarr-existing-encrypted", config.RadarrApiKeyEncrypted);
            Assert.Equal("seerr-existing-encrypted", config.SeerrApiKeyEncrypted);
            Assert.Equal(string.Empty, config.RadarrApiKeyPlaintext);
            Assert.Equal(string.Empty, config.SeerrApiKeyPlaintext);
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
