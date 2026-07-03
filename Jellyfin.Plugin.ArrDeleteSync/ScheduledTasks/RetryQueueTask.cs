// NOTE: The installed Jellyfin.Model (10.11.11) shape for TaskTriggerInfo.Type is the enum
// MediaBrowser.Model.Tasks.TaskTriggerInfoType (IntervalTrigger/DailyTrigger/WeeklyTrigger/
// StartupTrigger) — not a string constant `TaskTriggerInfo.TriggerInterval` as in older
// Jellyfin API docs/snippets. Verified via reflection against the installed
// MediaBrowser.Model.dll. Using TaskTriggerInfoType.IntervalTrigger below.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ArrDeleteSync.ScheduledTasks;

public class RetryQueueTask : IScheduledTask
{
    private readonly IDeleteOrchestrator _orchestrator;
    private readonly IRetryQueueStore _retryQueueStore;
    private readonly ICircuitBreaker _circuitBreaker;

    public RetryQueueTask(IDeleteOrchestrator orchestrator, IRetryQueueStore retryQueueStore, ICircuitBreaker circuitBreaker)
    {
        _orchestrator = orchestrator;
        _retryQueueStore = retryQueueStore;
        _circuitBreaker = circuitBreaker;
    }

    public string Name => "Process ArrDeleteSync retry queue";
    public string Key => "ArrDeleteSyncRetryQueue";
    public string Description => "Retries pending arr/Seerr sync steps for prior deletions.";
    public string Category => "Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(5).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_circuitBreaker.IsTripped)
        {
            return;
        }

        var entries = (await _retryQueueStore.GetAllAsync())
            .Where(e => e.NextRetryAtUtc <= DateTime.UtcNow)
            .ToList();

        foreach (var entry in entries)
        {
            if (_circuitBreaker.IsTripped)
            {
                break;
            }

            try
            {
                var resolved = await _orchestrator.ProcessRetryEntryAsync(entry);
                if (resolved)
                {
                    await _retryQueueStore.RemoveAsync(entry.Id);
                }
                else
                {
                    entry.AttemptCount++;
                    entry.NextRetryAtUtc = DateTime.UtcNow.AddMinutes(Math.Pow(2, entry.AttemptCount) * 5);
                    await _retryQueueStore.UpsertAsync(entry);
                }
            }
            catch (Exception)
            {
                // Never let a single entry's exception stop the batch or escape into
                // Jellyfin's shared task scheduler.
            }
        }
    }
}
