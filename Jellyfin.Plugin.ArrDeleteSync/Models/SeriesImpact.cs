using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public readonly record struct MonitoredEpisode(int SeasonNumber, int EpisodeNumber, bool Monitored);

// Sonarr's view of a series' monitoring, used to decide whether a season/episode delete would
// leave the series with nothing monitored.
public class SeriesMonitorState
{
    public required string Title { get; set; }
    public required bool Ended { get; set; } // Sonarr status == "ended"
    public required bool SeriesMonitored { get; set; }
    public bool MonitorsNewSeasons { get; set; } // Sonarr monitorNewItems == "all"
    public required IReadOnlyList<MonitoredEpisode> Episodes { get; set; }
}

// A series that would have zero monitored episodes left once the selected seasons/episodes are
// deleted (and unmonitored). The Delete Manager offers a "also remove the whole series" tickbox
// for each one -- pre-ticked when Ended.
public class SeriesImpact
{
    public required Guid SeriesItemId { get; set; }
    public required string SeriesName { get; set; }
    public required bool Ended { get; set; }
    // False when Sonarr wouldn't download new seasons as things stand (series unmonitored, or its
    // "Monitor New Seasons" is off). Keeping the series then switches both on.
    public bool FollowsNewSeasons { get; set; }
    public required List<Guid> ItemIds { get; set; }
}
