namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class RetryQueueEntry
{
    public required Guid Id { get; set; }
    public required Guid JellyfinItemId { get; set; }
    public required DeleteGranularity Granularity { get; set; }
    public string? ProviderIdType { get; set; }
    public string? ProviderIdValue { get; set; }
    public DeleteStepStatus ArrDeleteStatus { get; set; } = DeleteStepStatus.Pending;
    public DeleteStepStatus JellyfinCleanupStatus { get; set; } = DeleteStepStatus.Pending;
    public DeleteStepStatus SeerrUpdateStatus { get; set; } = DeleteStepStatus.Pending;
    public bool RequiresManualFileCleanup { get; set; }
    public string? FilePath { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public required DateTime NextRetryAtUtc { get; set; }
    public bool IsStructuralFailure { get; set; }
    public bool MaxAttemptsExceeded { get; set; }
}
