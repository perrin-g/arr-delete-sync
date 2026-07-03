using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IDeleteOrchestrator
{
    Task<ResolutionResult> ResolveAsync(Guid jellyfinItemId, DeleteGranularity granularity);
    Task<DeleteOutcome> ExecuteDeleteAsync(DeleteRequest request);
    Task<bool> ProcessRetryEntryAsync(RetryQueueEntry entry);
}
