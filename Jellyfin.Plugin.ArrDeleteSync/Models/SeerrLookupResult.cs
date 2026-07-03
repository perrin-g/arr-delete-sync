using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class SeerrLookupResult
{
    public required ArrTrackingState State { get; set; }
    public int? MediaId { get; set; }
    public int? TmdbId { get; set; }
}
