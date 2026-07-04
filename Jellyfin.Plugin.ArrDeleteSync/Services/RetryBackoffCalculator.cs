using System;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

// Shared by RetryQueueTask's scheduled loop and ArrDeleteSyncController's manual "Retry now"
// endpoint, so the two call sites can't drift on what "failed again" means.
public static class RetryBackoffCalculator
{
    public static void RecordFailedAttempt(RetryQueueEntry entry, int maxAttempts)
    {
        entry.AttemptCount++;
        if (entry.AttemptCount >= maxAttempts)
        {
            // Stop auto-retrying entirely rather than backing off forever -- the entry stays in
            // the queue (visible in the Delete Manager UI, flagged) until an admin manually
            // retries or gives up on it, instead of silently consuming a retry cycle every few
            // hours indefinitely with nothing ever surfacing that it's stuck.
            entry.MaxAttemptsExceeded = true;
            entry.NextRetryAtUtc = DateTime.MaxValue;
        }
        else
        {
            entry.NextRetryAtUtc = DateTime.UtcNow.AddMinutes(Math.Pow(2, entry.AttemptCount) * 5);
        }
    }
}
