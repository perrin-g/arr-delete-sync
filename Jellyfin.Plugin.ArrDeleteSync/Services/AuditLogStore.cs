using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class AuditLogStore : IAuditLogStore
{
    private readonly string _filePath;
    private readonly int _retentionDays;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuditLogStore(string dataDirectory, int retentionDays = 15)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "audit-log.json");
        _retentionDays = retentionDays;
    }

    // Same crash-mid-write caveat as RetryQueueStore.WriteAllUnlocked: atomicity relies on
    // File.Move being an OS-level atomic rename on the target deployment platform (Linux),
    // not re-verified here via process-kill testing.
    //
    // Retention is enforced here (prune-on-append) rather than via a separate scheduled task —
    // simpler, and guarantees the file never grows past one retention window's worth of entries
    // regardless of whether any scheduled task ever runs.
    public async Task AppendAsync(AuditLogEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAllUnlocked();
            all.Add(entry);
            var cutoffUtc = DateTime.UtcNow.AddDays(-_retentionDays);
            all = all.Where(e => e.TimestampUtc >= cutoffUtc).ToList();
            var json = JsonSerializer.Serialize(all);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAllAsync()
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

    private List<AuditLogEntry> ReadAllUnlocked()
    {
        if (!File.Exists(_filePath))
        {
            return new List<AuditLogEntry>();
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<List<AuditLogEntry>>(json) ?? new List<AuditLogEntry>();
    }
}
