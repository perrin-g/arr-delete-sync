using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

// Decorator over CircuitBreaker that adds file-backed persistence, so a tripped breaker survives
// a Jellyfin restart and can only be cleared by a manual admin Reset() — matching the plan's
// requirement. All in-memory trip-condition logic (consecutive vs rolling-window) still lives
// entirely in CircuitBreaker; this class never re-implements it, it only loads/saves the state.
public class PersistentCircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreaker _inner;
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PersistentCircuitBreaker(CircuitBreaker inner, string dataDirectory)
    {
        _inner = inner;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "circuit-breaker.json");

        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<CircuitBreakerState>(json);
            if (state != null)
            {
                _inner.LoadState(state);
            }
        }
    }

    // Reads never need to touch the file — the in-memory state is always the source of truth for
    // the running process; persistence only matters for surviving a restart.
    public bool IsTripped => _inner.IsTripped;

    public void RecordFailure()
    {
        _inner.RecordFailure();
        Persist();
    }

    public void RecordSuccess()
    {
        _inner.RecordSuccess();
        Persist();
    }

    public void Reset()
    {
        _inner.Reset();
        Persist();
    }

    public CircuitBreakerState GetState() => _inner.GetState();

    public void LoadState(CircuitBreakerState state)
    {
        _inner.LoadState(state);
        Persist();
    }

    // Same atomic-write pattern as RetryQueueStore/AuditLogStore: write to a temp file, then
    // File.Move(..., overwrite: true), which is an OS-level atomic rename on the target
    // deployment platform (Linux) — never leaves a partially-written circuit-breaker.json behind.
    private void Persist()
    {
        var state = _inner.GetState();
        var json = JsonSerializer.Serialize(state);
        var tempPath = _filePath + ".tmp";

        _lock.Wait();
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
