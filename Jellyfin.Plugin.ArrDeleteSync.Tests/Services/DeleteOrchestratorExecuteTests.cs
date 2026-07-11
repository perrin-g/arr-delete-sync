using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class DeleteOrchestratorExecuteTests
{
    private static JellyfinItemInfo MakeMovie(Guid id, string? path = "/media/videos/Movies/Test/Test.mkv") => new()
    {
        Id = id,
        Name = "Test Movie",
        Granularity = DeleteGranularity.Movie,
        TmdbId = "603",
        Path = path
    };

    private static (Mock<IJellyfinItemAccessor>, Mock<IArrClient>, Mock<ISeerrClient>, Mock<IRetryQueueStore>, Mock<IAuditLogStore>, Mock<ICircuitBreaker>) MakeMocks()
        => (new(), new(), new(), new(), new(), new());

    // These tests exercise execute/retry logic against "an arr client" and don't care about
    // Radarr-vs-Sonarr routing, so the stub factory returns the same mocked client regardless of
    // isSeries — see DeleteOrchestratorResolveTests for the dedicated routing test.
    private static IArrClientFactory MakeArrClientFactory(IArrClient arrClient)
    {
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<bool>())).Returns(arrClient);
        return factory.Object;
    }

    [Fact]
    public async Task Execute_Rejects_WhenCircuitBreakerTripped()
    {
        // Regression test: the circuit-breaker check used to run before the item was ever looked
        // up, so every breaker-blocked audit entry logged ItemDisplayName as a hardcoded
        // "unknown" instead of the real name -- reproduced live via the Delete Manager UI.
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        breaker.Setup(b => b.IsTripped).Returns(true);
        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.False(outcome.ArrDeleted);
        Assert.NotNull(outcome.BlockedReason);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "Blocked" && e.ItemDisplayName == "Test Movie")), Times.Once);
        arr.Verify(a => a.FindByProviderIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Execute_TrackedContent_ArrDeletesFirst_ThenJellyfinCleansUp_ThenSeerr_InOrder()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        var callOrder = new System.Collections.Generic.List<string>();

        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 5 });

        arr.Setup(a => a.DeleteAsync(41, false)).Callback(() => callOrder.Add("arr")).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) =>
            {
                callOrder.Add("jellyfin");
                structural = false;
                err = null;
            }))
            .Returns(true);
        seerr.Setup(s => s.UpdateAvailabilityAsync(5)).Callback(() => callOrder.Add("seerr")).ReturnsAsync(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.True(outcome.ArrDeleted);
        Assert.True(outcome.JellyfinCleanedUp);
        Assert.True(outcome.SeerrUpdated);
        Assert.False(outcome.RequiresManualFileCleanup);
        Assert.Equal(new[] { "arr", "jellyfin", "seerr" }, callOrder);
    }

    [Fact]
    public async Task Execute_ArrDeleteFails_NeverCallsJellyfinCleanupOrSeerr_QueuesForRetry()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();

        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 5 });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.False(outcome.ArrDeleted);
        Assert.False(outcome.JellyfinCleanedUp);
        accessor.Verify(a => a.DeleteItem(It.IsAny<Guid>(), out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny), Times.Never);
        seerr.Verify(s => s.UpdateAvailabilityAsync(It.IsAny<int>()), Times.Never);
        // Regression test: RetryQueueEntry.LastError was never set on this path -- the Retry
        // Queue UI showed a blank "last error:" for every arr-delete failure, reproduced live.
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e => e.ArrDeleteStatus == DeleteStepStatus.Failed && e.ItemDisplayName == "Test Movie" && e.LastError == "arr delete call failed")), Times.Once);
        breaker.Verify(b => b.RecordFailure(), Times.Once);
    }

    [Fact]
    public async Task Execute_JellyfinCleanupFails_ArrAlreadySucceeded_OnlyCleanupStepQueued()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();

        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 5 });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = true; err = "denied"; }))
            .Returns(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.True(outcome.ArrDeleted);
        Assert.False(outcome.JellyfinCleanedUp);
        seerr.Verify(s => s.UpdateAvailabilityAsync(It.IsAny<int>()), Times.Never);
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e =>
            e.ArrDeleteStatus == DeleteStepStatus.Succeeded &&
            e.JellyfinCleanupStatus == DeleteStepStatus.Failed &&
            e.ItemDisplayName == "Test Movie")), Times.Once);
    }

    [Fact]
    public async Task Execute_SeerrFails_ArrAndJellyfinStillSucceed_OnlySeerrQueued()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();

        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41 });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 5 });
        arr.Setup(a => a.DeleteAsync(41, false)).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);
        seerr.Setup(s => s.UpdateAvailabilityAsync(5)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.True(outcome.ArrDeleted);
        Assert.True(outcome.JellyfinCleanedUp);
        Assert.False(outcome.SeerrUpdated);
        Assert.True(outcome.QueuedForRetry);
        // Regression test: RetryQueueEntry.LastError was never set on this path -- reproduced
        // live, showing a blank "last error:" for every Seerr-sync failure.
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e =>
            e.ArrDeleteStatus == DeleteStepStatus.Succeeded &&
            e.JellyfinCleanupStatus == DeleteStepStatus.Succeeded &&
            e.SeerrUpdateStatus == DeleteStepStatus.Failed &&
            e.ItemDisplayName == "Test Movie" &&
            e.LastError == "Seerr update failed")), Times.Once);
    }

    [Fact]
    public async Task Execute_IndeterminateAtReResolve_QueuesWholeOperation_NothingCalledYet()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();

        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Indeterminate });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.False(outcome.ArrDeleted);
        Assert.True(outcome.QueuedForRetry);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        accessor.Verify(a => a.DeleteItem(It.IsAny<Guid>(), out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny), Times.Never);
        // Regression test: RetryQueueEntry.LastError was never set on this path -- reproduced
        // live via a Series delete against a stopped Sonarr, showing a blank "last error:".
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e => e.ArrDeleteStatus == DeleteStepStatus.Pending && e.ItemDisplayName == "Test Movie" && e.LastError == "Could not verify arr status; queued for retry")), Times.Once);
    }

    [Fact]
    public async Task Execute_ReportsCircuitBreakerTripped_WhenThisFailureIsWhatCrossesTheThreshold()
    {
        // Regression test: a failure that itself trips the breaker used to report itself
        // identically to any other ordinary "Queued for retry" failure -- reproduced live with a
        // Series delete (each needing its own individually-submitted typed confirmation, unlike
        // a bulk movie batch's automatic next-item), so nothing revealed the trip had happened
        // until a separate, later attempt happened to get blocked.
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Indeterminate });
        // First read (top-of-method early-exit check) says not tripped yet; second read (after
        // RecordFailure() runs) says this exact call just tripped it.
        breaker.SetupSequence(b => b.IsTripped).Returns(false).Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.True(outcome.CircuitBreakerTripped);
        breaker.Verify(b => b.RecordFailure(), Times.Once);
    }

    [Fact]
    public async Task Execute_UntrackedContent_SkipsArrEntirely_CleansUpJellyfinOnly_FlagsManualFileCleanup()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();

        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId, path: "/media/videos/Movies/Legacy/Legacy.mkv"));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.False(outcome.ArrDeleted); // nothing to delete via arr — vacuously not "deleted by arr"
        Assert.True(outcome.JellyfinCleanedUp);
        Assert.True(outcome.RequiresManualFileCleanup);
        Assert.Equal("/media/videos/Movies/Legacy/Legacy.mkv", outcome.FilePath);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Execute_NoUsableProviderId_NoForceFlag_Blocks()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo { Id = itemId, Name = "Unidentified", Granularity = DeleteGranularity.Movie });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie, Force = false });

        Assert.False(outcome.ArrDeleted);
        Assert.NotNull(outcome.BlockedReason);
        queue.Verify(q => q.UpsertAsync(It.IsAny<RetryQueueEntry>()), Times.Never);
    }

    [Fact]
    public async Task Execute_NoUsableProviderId_WithForceFlag_CleansUpJellyfinOnly_FlagsManualFileCleanup()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo { Id = itemId, Name = "Unidentified", Granularity = DeleteGranularity.Movie, Path = "/media/videos/Movies/Unknown/Unknown.mkv" });
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie, Force = true });

        Assert.True(outcome.JellyfinCleanedUp);
        Assert.True(outcome.RequiresManualFileCleanup);
        arr.Verify(a => a.FindByProviderIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        // Force-deleted untracked content never calls arr, so it must never record a spurious
        // breaker "success" either — doing so would mask a genuinely broken arr/Seerr integration
        // during a batch of force-deletes (see the arr-delete-first ordering test for the
        // gate this shares).
        breaker.Verify(b => b.RecordSuccess(), Times.Never);
    }

    [Fact]
    public async Task Execute_SeasonDelete_Blocks_WhenNoPhysicalPath_VirtualSeason()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "Season 1", Granularity = DeleteGranularity.Season,
            TvdbId = "371572", HasPhysicalPath = false
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Season });

        Assert.False(outcome.ArrDeleted);
        Assert.Contains("layout", outcome.BlockedReason, StringComparison.OrdinalIgnoreCase);
        accessor.Verify(a => a.DeleteItem(It.IsAny<Guid>(), out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny), Times.Never);
    }

    [Fact]
    public async Task Execute_EpisodeDelete_Blocks_WhenUntracked_NoFileBoundarySafetyPossible()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E01", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.False(outcome.ArrDeleted);
        Assert.Contains("boundary", outcome.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_EpisodeDelete_Blocks_WhenFileCoversMultipleEpisodes()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E01-E02", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        arr.Setup(a => a.GetEpisodeFileCoverageCountAsync(7, 1, 1)).ReturnsAsync(2);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.False(outcome.ArrDeleted);
        Assert.Contains("also contains", outcome.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_EpisodeDelete_Blocks_WhenCoverageCheckFails()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E01", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        arr.Setup(a => a.GetEpisodeFileCoverageCountAsync(7, 1, 1)).ReturnsAsync(-1);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.False(outcome.ArrDeleted);
        Assert.Contains("verify", outcome.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_EpisodeDelete_Blocks_WhenCoverageCountIsZero()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E01", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        arr.Setup(a => a.GetEpisodeFileCoverageCountAsync(7, 1, 1)).ReturnsAsync(0);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.False(outcome.ArrDeleted);
        Assert.Contains("verify", outcome.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_EpisodeDelete_Proceeds_WhenTrackedAndFileCoversOnlyOneEpisode()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E01", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        arr.Setup(a => a.GetEpisodeFileCoverageCountAsync(7, 1, 1)).ReturnsAsync(1);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);
        // Regression test: this used to assert arr.DeleteAsync(7, true) -- deleting the WHOLE
        // series (7 is the series' internal id) for a single-episode delete, pinning the bug
        // this fix resolves as "expected" behavior. It must call the episode-scoped delete.
        arr.Setup(a => a.DeleteEpisodeFilesAsync(7, 1, 1)).ReturnsAsync(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.True(outcome.ArrDeleted);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Execute_SeasonDelete_CallsDeleteSeasonFilesAsync_NotWholeSeriesDelete()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "Season 1", Granularity = DeleteGranularity.Season,
            TvdbId = "371572", SeasonNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        seerr.Setup(s => s.SearchByTitleAsync(It.IsAny<string>(), null, true)).ReturnsAsync((SeerrLookupResult?)null);
        arr.Setup(a => a.DeleteSeasonFilesAsync(7, 1)).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Season });

        Assert.True(outcome.ArrDeleted);
        arr.Verify(a => a.DeleteSeasonFilesAsync(7, 1), Times.Once);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Execute_SeriesDelete_StillCallsWholeSeriesDeleteAsync()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "The Pitt", Granularity = DeleteGranularity.Series,
            TvdbId = "371572", HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        seerr.Setup(s => s.SearchByTitleAsync(It.IsAny<string>(), null, true)).ReturnsAsync((SeerrLookupResult?)null);
        arr.Setup(a => a.DeleteAsync(7, true)).ReturnsAsync(true);
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Series });

        Assert.True(outcome.ArrDeleted);
        arr.Verify(a => a.DeleteAsync(7, true), Times.Once);
        arr.Verify(a => a.DeleteSeasonFilesAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Execute_SeasonDelete_Blocks_WhenSeasonNumberMissing_NeverFallsBackToWholeSeriesDelete()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "Season 1", Granularity = DeleteGranularity.Season,
            TvdbId = "371572", SeasonNumber = null, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Season });

        Assert.False(outcome.ArrDeleted);
        Assert.NotNull(outcome.BlockedReason);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        arr.Verify(a => a.DeleteSeasonFilesAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Execute_EpisodeDelete_Blocks_WhenEpisodeNumberMissing_NeverFallsBackToWholeSeriesDelete()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E??", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = null, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.False(outcome.ArrDeleted);
        Assert.NotNull(outcome.BlockedReason);
        arr.Verify(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        arr.Verify(a => a.DeleteEpisodeFilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Execute_SeasonDeleteFails_QueuesRetryEntry_WithSeasonNumberPersisted()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "Season 1", Granularity = DeleteGranularity.Season,
            TvdbId = "371572", SeasonNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        seerr.Setup(s => s.SearchByTitleAsync(It.IsAny<string>(), null, true)).ReturnsAsync((SeerrLookupResult?)null);
        arr.Setup(a => a.DeleteSeasonFilesAsync(7, 1)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Season });

        Assert.False(outcome.ArrDeleted);
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e => e.SeasonNumber == 1 && e.Granularity == DeleteGranularity.Season)), Times.Once);
    }

    [Fact]
    public async Task Execute_EpisodeDeleteFails_QueuesRetryEntry_WithSeasonAndEpisodeNumberPersisted()
    {
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "S01E01", Granularity = DeleteGranularity.Episode,
            TvdbId = "371572", SeasonNumber = 1, EpisodeNumber = 1, HasPhysicalPath = true
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        arr.Setup(a => a.GetEpisodeFileCoverageCountAsync(7, 1, 1)).ReturnsAsync(1);
        seerr.Setup(s => s.SearchByTitleAsync(It.IsAny<string>(), null, true)).ReturnsAsync((SeerrLookupResult?)null);
        arr.Setup(a => a.DeleteEpisodeFilesAsync(7, 1, 1)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.False(outcome.ArrDeleted);
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1 && e.Granularity == DeleteGranularity.Episode)), Times.Once);
    }

    [Fact]
    public async Task Execute_Blocks_WhenItemBelongsToExcludedLibrary()
    {
        // Security hardening: the Delete Manager UI hides excluded-library items from its list,
        // but that alone doesn't stop a direct POST to /ArrDeleteSync/delete for one of their
        // item ids (stale tab, replayed request, a future UI regression). This is the
        // server-side enforcement that closes that gap.
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        accessor.Setup(a => a.GetLibraryName(itemId)).Returns("Discover");

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object, excludedLibraryNames: new[] { "Discover" });

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        Assert.False(outcome.ArrDeleted);
        Assert.NotNull(outcome.BlockedReason);
        arr.Verify(a => a.FindByProviderIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        seerr.Verify(s => s.FindByTmdbIdAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Execute_DoesNotCheckExcludedLibrary_WhenNoneConfigured()
    {
        // No wasted GetLibraryName lookup on the default/common case where nothing is excluded.
        var itemId = Guid.NewGuid();
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));
        arr.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        seerr.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Movie });

        accessor.Verify(a => a.GetLibraryName(It.IsAny<Guid>()), Times.Never);
    }
}

public delegate void DeleteItemCallback(Guid id, out bool structural, out string? error);
