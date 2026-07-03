namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class DeleteRequest
{
    public required Guid JellyfinItemId { get; set; }
    public required DeleteGranularity Granularity { get; set; }
    public bool Force { get; set; }
}

public class DeleteOutcome
{
    public required bool ArrDeleted { get; set; } // true only when *arr actually removed the file (tracked content)
    public required bool JellyfinCleanedUp { get; set; } // Jellyfin catalog entry removed (both tracked and untracked content)
    public required bool SeerrUpdated { get; set; }
    public bool QueuedForRetry { get; set; }
    public string? BlockedReason { get; set; }
    public bool RequiresManualFileCleanup { get; set; } // true for untracked content: file remains on disk
    public string? FilePath { get; set; } // populated when RequiresManualFileCleanup is true
}
