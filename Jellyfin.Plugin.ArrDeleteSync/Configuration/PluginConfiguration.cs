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
    public int RetryBackoffBaseSeconds { get; set; } = 300;
    public int RetryMaxAttempts { get; set; } = 5;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerWindowMinutes { get; set; } = 15;
}
