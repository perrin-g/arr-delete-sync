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
        var (accessor, arr, seerr, queue, audit, breaker) = MakeMocks();
        breaker.Setup(b => b.IsTripped).Returns(true);
        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie });

        Assert.False(outcome.ArrDeleted);
        Assert.NotNull(outcome.BlockedReason);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "Blocked")), Times.Once);
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
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e => e.ArrDeleteStatus == DeleteStepStatus.Failed)), Times.Once);
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
            e.JellyfinCleanupStatus == DeleteStepStatus.Failed)), Times.Once);
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
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e =>
            e.ArrDeleteStatus == DeleteStepStatus.Succeeded &&
            e.JellyfinCleanupStatus == DeleteStepStatus.Succeeded &&
            e.SeerrUpdateStatus == DeleteStepStatus.Failed)), Times.Once);
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
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e => e.ArrDeleteStatus == DeleteStepStatus.Pending)), Times.Once);
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
        arr.Setup(a => a.DeleteAsync(7, true)).ReturnsAsync(true);

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arr.Object), seerr.Object, queue.Object, audit.Object, breaker.Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Episode });

        Assert.True(outcome.ArrDeleted);
    }
}

public delegate void DeleteItemCallback(Guid id, out bool structural, out string? error);
