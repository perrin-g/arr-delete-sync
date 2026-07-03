using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class RetryQueueStore : IRetryQueueStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RetryQueueStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "retry-queue.json");
    }

    public async Task<IReadOnlyList<RetryQueueEntry>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return ReadAllUnlocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RetryQueueEntry?> FindByItemIdAsync(Guid jellyfinItemId)
    {
        await _lock.WaitAsync();
        try
        {
            return ReadAllUnlocked().FirstOrDefault(e => e.JellyfinItemId == jellyfinItemId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(RetryQueueEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAllUnlocked().ToList();
            var existingIndex = all.FindIndex(e => e.JellyfinItemId == entry.JellyfinItemId);
            if (existingIndex >= 0)
            {
                all[existingIndex] = entry;
            }
            else
            {
                all.Add(entry);
            }
            WriteAllUnlocked(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(Guid entryId)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAllUnlocked().Where(e => e.Id != entryId).ToList();
            WriteAllUnlocked(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<RetryQueueEntry> ReadAllUnlocked()
    {
        if (!File.Exists(_filePath))
        {
            return new List<RetryQueueEntry>();
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<List<RetryQueueEntry>>(json) ?? new List<RetryQueueEntry>();
    }

    private void WriteAllUnlocked(List<RetryQueueEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
