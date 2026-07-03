using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArrDeleteSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string RadarrUrl { get; set; } = string.Empty;
    public string RadarrApiKeyEncrypted { get; set; } = string.Empty;
    public string SonarrUrl { get; set; } = string.Empty;
    public string SonarrApiKeyEncrypted { get; set; } = string.Empty;
    public string SeerrUrl { get; set; } = string.Empty;
    public string SeerrApiKeyEncrypted { get; set; } = string.Empty;

    // Transient write-only fields: the config page sets these to a freshly-entered
    // plaintext API key when the admin saves settings. Plugin.UpdateConfiguration
    // intercepts the save, encrypts the value into the corresponding *ApiKeyEncrypted
    // field via IApiKeyProtector, and clears these back to empty before the base class
    // persists the configuration to PluginConfiguration.xml — so plaintext never reaches
    // disk. Not intended to ever hold a non-empty value outside of a single save request.
    public string RadarrApiKeyPlaintext { get; set; } = string.Empty;
    public string SonarrApiKeyPlaintext { get; set; } = string.Empty;
    public string SeerrApiKeyPlaintext { get; set; } = string.Empty;
    public int RetryBackoffBaseSeconds { get; set; } = 300;
    public int RetryMaxAttempts { get; set; } = 5;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerWindowMinutes { get; set; } = 15;
}
