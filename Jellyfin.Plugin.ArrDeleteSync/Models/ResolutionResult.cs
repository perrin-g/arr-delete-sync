namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class ResolutionResult
{
    public required ArrTrackingState State { get; set; }
    public int? ArrInternalId { get; set; }
    public string? ArrTitle { get; set; }
    public int? ArrYear { get; set; }
    public int? SeerrMediaId { get; set; }
    public bool SeerrMatchFromFallback { get; set; }
    public string? ProviderIdType { get; set; } // "Tmdb" or "Tvdb"
    public string? ProviderIdValue { get; set; }
    public bool HasUsableProviderId { get; set; }
}
