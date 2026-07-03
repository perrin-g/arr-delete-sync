using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class DeleteOrchestratorRetryTests
{
    private static (Mock<IJellyfinItemAccessor>, Mock<IArrClient>, Mock<ISeerrClient>, Mock<IRetryQueueStore>, Mock<IAuditLogStore>, Mock<ICircuitBreaker>) MakeMocks()
        => (new(), new(), new(), new(), new(), new());

    // These tests exercise retry logic against "an arr client" and don't care about
    // Radarr-vs-Sonarr routing, so the stub factory returns the same mocked client regardless of
    // isSeries — see DeleteOrchestratorResolveTests for the dedicated routing test.
    private static IArrClientFactory MakeArrClientFactory(IArrClient arrClient)
    {
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<bool>())).Returns(arrClient);
        return factory.Object;
    }

    [Fact]
    public async Task ProcessRetry_ArrNeverAttempted_ReRunsFullExecute_UsingStillExistingItem()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo { Id = itemId, Name = "Movie", Granularity = DeleteGranularity.Movie, TmdbId = "603" });
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false)).ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false)).ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);
        var entry = new RetryQueueEntry { Id = Guid.NewGuid(), JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie, ArrDeleteStatus = DeleteStepStatus.Pending, NextRetryAtUtc = DateTime.UtcNow };

        var resolved = await orchestrator.ProcessRetryEntryAsync(entry);

        Assert.True(resolved);
        arr.Verify(a => a.DeleteAsync(41, false), Times.Once);
    }

    [Fact]
    public async Task ProcessRetry_ArrAlreadySucceeded_JellyfinCleanupPending_ReResolvesFromSnapshottedProviderId_NotFromJellyfinItem()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie,
            ProviderIdType = "tmdbId", ProviderIdValue = "603",
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Failed,
            SeerrUpdateStatus = DeleteStepStatus.Succeeded, NextRetryAtUtc = DateTime.UtcNow
        };

        var resolved = await orchestrator.ProcessRetryEntryAsync(entry);

        Assert.True(resolved);
        accessor.Verify(a => a.GetItem(It.IsAny<Guid>()), Times.Never);
        arr.Verify(a => a.FindByProviderIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRetry_ArrLookupConfirmsAlreadyGone_CountsAsSuccess()
    {
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false)).ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        accessor.Setup(a => a.DeleteItem(It.IsAny<Guid>(), out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie,
            ProviderIdType = "tmdbId", ProviderIdValue = "603",
            ArrDeleteStatus = DeleteStepStatus.Failed, JellyfinCleanupStatus = DeleteStepStatus.Pending,
            SeerrUpdateStatus = DeleteStepStatus.Succeeded, NextRetryAtUtc = DateTime.UtcNow
        };

        var resolved = await orchestrator.ProcessRetryEntryAsync(entry);

        Assert.True(resolved);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRetry_ArrStillFailing_DoesNotAttemptJellyfinOrSeerr_PreservesOrdering()
    {
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false)).ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie,
            ProviderIdType = "tmdbId", ProviderIdValue = "603",
            ArrDeleteStatus = DeleteStepStatus.Failed, JellyfinCleanupStatus = DeleteStepStatus.Pending,
            SeerrUpdateStatus = DeleteStepStatus.Pending, NextRetryAtUtc = DateTime.UtcNow
        };

        var resolved = await orchestrator.ProcessRetryEntryAsync(entry);

        Assert.False(resolved);
        accessor.Verify(a => a.DeleteItem(It.IsAny<Guid>(), out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny), Times.Never);
        seerr.Verify(s => s.FindByTmdbIdAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRetry_PendingBranchPartialFailure_PreservesGranularStateForCaller()
    {
        // Regression test for a real bug: when the Pending branch's inner ExecuteDeleteAsync
        // call fails partway, it writes its own accurate RetryQueueEntry — but the ORIGINAL
        // entry object (still showing every step Pending) is what the caller (RetryQueueTask)
        // re-upserts afterward unless this method copies the fresher state back onto it first.
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo { Id = itemId, Name = "Movie", Granularity = DeleteGranularity.Movie, TmdbId = "603" });
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false)).ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false)).ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = "jellyfin cleanup failed"; }))
            .Returns(false);

        // Simulate ExecuteDeleteAsync's own write: a fresh entry reflecting arr succeeded but
        // Jellyfin cleanup failed.
        queue.Setup(q => q.FindByItemIdAsync(itemId)).ReturnsAsync(new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie,
            ProviderIdType = "tmdbId", ProviderIdValue = "603",
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Failed,
            NextRetryAtUtc = DateTime.UtcNow
        });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);
        var entry = new RetryQueueEntry { Id = Guid.NewGuid(), JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie, ArrDeleteStatus = DeleteStepStatus.Pending, NextRetryAtUtc = DateTime.UtcNow };

        var resolved = await orchestrator.ProcessRetryEntryAsync(entry);

        Assert.False(resolved);
        // The original entry object must now reflect the fresh state, not remain "everything
        // Pending" — otherwise the caller's re-upsert would clobber the accurate write.
        Assert.Equal(DeleteStepStatus.Succeeded, entry.ArrDeleteStatus);
        Assert.Equal(DeleteStepStatus.Failed, entry.JellyfinCleanupStatus);
    }

    [Fact]
    public async Task ProcessRetry_GeneralBranch_RecordsCircuitBreakerOutcome()
    {
        // Regression test: the general (non-Pending) branch calls arr/Jellyfin/Seerr directly
        // and must record its own circuit-breaker outcome — it doesn't get this for free from
        // ExecuteDeleteAsync the way the Pending branch does.
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false)).ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie,
            ProviderIdType = "tmdbId", ProviderIdValue = "603",
            ArrDeleteStatus = DeleteStepStatus.Failed, JellyfinCleanupStatus = DeleteStepStatus.Pending,
            SeerrUpdateStatus = DeleteStepStatus.Pending, NextRetryAtUtc = DateTime.UtcNow
        };

        await orchestrator.ProcessRetryEntryAsync(entry);

        breaker.Verify(b => b.RecordFailure(), Times.Once);
        breaker.Verify(b => b.RecordSuccess(), Times.Never);
    }
}
