using System;

namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class JellyfinItemInfo
{
    public required Guid Id { get; set; }
    public required string Name { get; set; }
    public required DeleteGranularity Granularity { get; set; }
    public string? TmdbId { get; set; }
    public string? TvdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Path { get; set; }
    public bool HasPhysicalPath { get; set; }
    public Guid? SeriesItemId { get; set; }
    public string? SeriesTvdbId { get; set; }
    public string? SeriesTmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
}
