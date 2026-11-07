using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IDeleteOrchestrator
{
    Task<ResolutionResult> ResolveAsync(Guid jellyfinItemId, DeleteGranularity granularity);
    Task<DeleteOutcome> ExecuteDeleteAsync(DeleteRequest request);
    Task<bool> ProcessRetryEntryAsync(RetryQueueEntry entry);
    Task<IReadOnlyList<SeriesImpact>> GetSeriesImpactAsync(IReadOnlyList<DeleteRequest> requests);
    Task<bool> KeepFollowingAsync(Guid seriesItemId);
    Task<IReadOnlyList<FollowedSeries>> GetFollowedEmptySeriesAsync();
    Task<StopFollowingOutcome> StopFollowingAsync(int arrSeriesId);
}
