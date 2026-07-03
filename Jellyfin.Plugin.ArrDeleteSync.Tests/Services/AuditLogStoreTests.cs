using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class AuditLogStoreTests : IDisposable
{
    private readonly string _tempDir;

    public AuditLogStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-audit-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private AuditLogEntry MakeEntry(string action = "SyncedDelete", string outcome = "Success") => new()
    {
        Id = Guid.NewGuid(),
        TimestampUtc = DateTime.UtcNow,
        JellyfinItemId = Guid.NewGuid(),
        ItemDisplayName = "Test Movie",
        Granularity = DeleteGranularity.Movie,
        Action = action,
        Outcome = outcome
    };

    [Fact]
    public async Task Append_ThenGetAll_ReturnsEntry()
    {
        var store = new AuditLogStore(_tempDir);
        await store.AppendAsync(MakeEntry());

        var all = await store.GetAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task Append_Twice_KeepsBothEntries_NeverOverwrites()
    {
        var store = new AuditLogStore(_tempDir);
        await store.AppendAsync(MakeEntry(outcome: "Failed"));
        await store.AppendAsync(MakeEntry(outcome: "Success"));

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Failed", all[0].Outcome);
        Assert.Equal("Success", all[1].Outcome);
    }

    [Fact]
    public async Task ConcurrentAppends_DoNotLoseEntries()
    {
        var store = new AuditLogStore(_tempDir);
        var tasks = new Task[15];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = store.AppendAsync(MakeEntry());
        }
        await Task.WhenAll(tasks);

        var all = await store.GetAllAsync();
        Assert.Equal(15, all.Count);
    }
}
