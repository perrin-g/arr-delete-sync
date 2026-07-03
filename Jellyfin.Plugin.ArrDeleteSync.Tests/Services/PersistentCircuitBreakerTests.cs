using System;
using System.IO;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

// Only the persistence behavior PersistentCircuitBreaker adds is tested here — the trip-condition
// logic itself (consecutive vs rolling-window) is already covered by CircuitBreakerTests and is
// never re-implemented by this decorator, only delegated to.
public class PersistentCircuitBreakerTests : IDisposable
{
    private readonly string _tempDir;

    public PersistentCircuitBreakerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static CircuitBreaker MakeInner() => new(threshold: 5, windowMinutes: 15);

    [Fact]
    public void Construction_NoExistingFile_StartsUntripped()
    {
        var breaker = new PersistentCircuitBreaker(MakeInner(), _tempDir);

        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void RecordFailure_TripsBreaker_AndPersistsAcrossNewInstance()
    {
        var breaker = new PersistentCircuitBreaker(MakeInner(), _tempDir);
        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        Assert.True(breaker.IsTripped);

        // Simulate a restart: a brand new object graph reading the same file.
        var restored = new PersistentCircuitBreaker(MakeInner(), _tempDir);

        Assert.True(restored.IsTripped);
    }

    [Fact]
    public void Reset_ClearsPersistedState_AcrossNewInstance()
    {
        var breaker = new PersistentCircuitBreaker(MakeInner(), _tempDir);
        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        Assert.True(breaker.IsTripped);

        breaker.Reset();

        var restored = new PersistentCircuitBreaker(MakeInner(), _tempDir);
        Assert.False(restored.IsTripped);
    }
}
