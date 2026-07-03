using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IRetryQueueStore
{
    Task<IReadOnlyList<RetryQueueEntry>> GetAllAsync();
    Task<RetryQueueEntry?> FindByItemIdAsync(Guid jellyfinItemId);
    Task UpsertAsync(RetryQueueEntry entry);
    Task RemoveAsync(Guid entryId);
}
