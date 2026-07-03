using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class ArrLookupResult
{
    public required ArrTrackingState State { get; set; }
    public int? InternalId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
}
