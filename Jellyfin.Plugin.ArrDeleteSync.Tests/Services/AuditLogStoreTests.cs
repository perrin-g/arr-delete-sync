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

    [Fact]
    public async Task StaleTempFile_DoesNotLeakIntoReads()
    {
        // Same caveat as RetryQueueStoreTests' equivalent: this proves reads ignore a leftover
        // .tmp file, not crash-mid-write atomicity by itself (ReadAllUnlocked never reads the
        // .tmp path). See Append_CleansUpTempFileAfterSuccessfulWrite for the test that
        // actually exercises the temp-file+rename mechanic.
        var store = new AuditLogStore(_tempDir);
        var entry = MakeEntry();
        await store.AppendAsync(entry);

        var strayTemp = Path.Combine(_tempDir, "audit-log.json.tmp");
        await File.WriteAllTextAsync(strayTemp, "{not valid json");

        var all = await store.GetAllAsync();

        Assert.Single(all);
        Assert.Equal(entry.Id, all[0].Id);
    }

    [Fact]
    public async Task Append_CleansUpTempFileAfterSuccessfulWrite()
    {
        var store = new AuditLogStore(_tempDir);
        await store.AppendAsync(MakeEntry());

        var expectedFilePath = Path.Combine(_tempDir, "audit-log.json");
        var expectedTempPath = expectedFilePath + ".tmp";

        Assert.True(File.Exists(expectedFilePath), "the real file should exist after a successful write");
        Assert.False(File.Exists(expectedTempPath), "the temp file should not survive a successful rename");
    }

    [Fact]
    public async Task Append_PrunesEntriesOlderThanRetentionPeriod()
    {
        var store = new AuditLogStore(_tempDir, retentionDays: 15);
        var oldEntry = MakeEntry();
        oldEntry.TimestampUtc = DateTime.UtcNow.AddDays(-16);
        await store.AppendAsync(oldEntry);

        var freshEntry = MakeEntry();
        await store.AppendAsync(freshEntry);

        var all = await store.GetAllAsync();

        Assert.Single(all);
        Assert.Equal(freshEntry.Id, all[0].Id);
    }

    [Fact]
    public async Task Append_KeepsEntriesWithinRetentionPeriod()
    {
        var store = new AuditLogStore(_tempDir, retentionDays: 15);
        var recentEntry = MakeEntry();
        recentEntry.TimestampUtc = DateTime.UtcNow.AddDays(-14);
        await store.AppendAsync(recentEntry);

        await store.AppendAsync(MakeEntry());

        var all = await store.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Id == recentEntry.Id);
    }

    [Fact]
    public async Task Append_WithDefaultRetention_Keeps15Days()
    {
        var store = new AuditLogStore(_tempDir);
        var oldEntry = MakeEntry();
        oldEntry.TimestampUtc = DateTime.UtcNow.AddDays(-16);
        await store.AppendAsync(oldEntry);
        await store.AppendAsync(MakeEntry());

        var all = await store.GetAllAsync();

        Assert.Single(all);
    }
}
