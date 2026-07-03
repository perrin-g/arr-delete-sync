using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class AuditLogStore : IAuditLogStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuditLogStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "audit-log.json");
    }

    public async Task AppendAsync(AuditLogEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAllUnlocked();
            all.Add(entry);
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
