using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class RetryQueueStoreTests : IDisposable
{
    private readonly string _tempDir;

    public RetryQueueStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arrdeletesync-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private RetryQueueEntry MakeEntry(Guid? itemId = null) => new()
    {
        Id = Guid.NewGuid(),
        JellyfinItemId = itemId ?? Guid.NewGuid(),
        Granularity = DeleteGranularity.Movie,
        ProviderIdType = "Tmdb",
        ProviderIdValue = "603",
        NextRetryAtUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task UpsertAndGetAll_RoundTrips()
    {
        var store = new RetryQueueStore(_tempDir);
        var entry = MakeEntry();

        await store.UpsertAsync(entry);
        var all = await store.GetAllAsync();

        Assert.Single(all);
        Assert.Equal(entry.Id, all[0].Id);
    }

    [Fact]
    public async Task FindByItemId_ReturnsMatchingEntry()
    {
        var store = new RetryQueueStore(_tempDir);
        var itemId = Guid.NewGuid();
        await store.UpsertAsync(MakeEntry(itemId));
        await store.UpsertAsync(MakeEntry());

        var found = await store.FindByItemIdAsync(itemId);

        Assert.NotNull(found);
        Assert.Equal(itemId, found!.JellyfinItemId);
    }

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var store = new RetryQueueStore(_tempDir);
        var entry = MakeEntry();
        await store.UpsertAsync(entry);

        await store.RemoveAsync(entry.Id);
        var all = await store.GetAllAsync();

        Assert.Empty(all);
    }

    [Fact]
    public async Task Upsert_SameItemId_ReplacesNotDuplicates()
    {
        var store = new RetryQueueStore(_tempDir);
        var itemId = Guid.NewGuid();
        var first = MakeEntry(itemId);
        await store.UpsertAsync(first);

        var updated = MakeEntry(itemId);
        updated.Id = first.Id;
        updated.AttemptCount = 3;
        await store.UpsertAsync(updated);

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal(3, all[0].AttemptCount);
    }

    [Fact]
    public async Task StaleTempFile_DoesNotLeakIntoReads()
    {
        var store = new RetryQueueStore(_tempDir);
        var entry = MakeEntry();
        await store.UpsertAsync(entry);

        // Simulate a crash mid-write by leaving a stray, invalid temp file behind —
        // the store must never read from a partial temp file, only the committed one.
        var strayTemp = Path.Combine(_tempDir, "retry-queue.json.tmp");
        await File.WriteAllTextAsync(strayTemp, "{not valid json");

        var all = await store.GetAllAsync();

        Assert.Single(all);
        Assert.Equal(entry.Id, all[0].Id);
    }

    [Fact]
    public async Task Upsert_CleansUpTempFileAfterSuccessfulWrite()
    {
        var store = new RetryQueueStore(_tempDir);
        await store.UpsertAsync(MakeEntry());

        var expectedFilePath = Path.Combine(_tempDir, "retry-queue.json");
        var expectedTempPath = expectedFilePath + ".tmp";

        Assert.True(File.Exists(expectedFilePath), "the real file should exist after a successful write");
        Assert.False(File.Exists(expectedTempPath), "the temp file should not survive a successful rename");
    }

    [Fact]
    public async Task ConcurrentUpserts_DoNotLoseEntries()
    {
        var store = new RetryQueueStore(_tempDir);
        var tasks = new Task[20];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = store.UpsertAsync(MakeEntry());
        }
        await Task.WhenAll(tasks);

        var all = await store.GetAllAsync();
        Assert.Equal(20, all.Count);
    }
}
