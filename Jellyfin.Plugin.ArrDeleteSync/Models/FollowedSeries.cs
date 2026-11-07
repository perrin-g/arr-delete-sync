namespace Jellyfin.Plugin.ArrDeleteSync.Models;

// A Sonarr series summary. The Delete Manager lists monitored series with no episode files and
// no Jellyfin entry as "Followed -- no episodes yet" (e.g. after a keep-series delete), since
// Jellyfin -- the TV list's normal source -- has nothing to show for them.
public class FollowedSeries
{
    public required int ArrSeriesId { get; set; }
    public required string Title { get; set; }
    public bool Ended { get; set; }
    public bool Monitored { get; set; }
    public int EpisodeFileCount { get; set; }
    public string? TvdbId { get; set; }
    public int? TmdbId { get; set; }
}

public class StopFollowingOutcome
{
    public required bool Ok { get; set; }
    public string? Message { get; set; }
}
