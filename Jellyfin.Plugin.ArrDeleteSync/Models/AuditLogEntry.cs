namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class AuditLogEntry
{
    public required Guid Id { get; set; }
    public required DateTime TimestampUtc { get; set; }
    public required Guid JellyfinItemId { get; set; }
    public required string ItemDisplayName { get; set; }
    public required DeleteGranularity Granularity { get; set; }
    public required string Action { get; set; } // SyncedDelete | JellyfinCatalogOnly | Blocked | Forced | Dismissed
    public required string Outcome { get; set; } // Success | Partial | Failed
    public string? ErrorDetail { get; set; }
    public bool RequiresManualFileCleanup { get; set; }
    public string? FilePath { get; set; }
}
