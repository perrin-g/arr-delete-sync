using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class DeleteOrchestratorResolveTests
{
    private static JellyfinItemInfo MakeMovie(Guid id, string? tmdbId = "603", string? imdbId = null) => new()
    {
        Id = id,
        Name = "Test Movie",
        Granularity = DeleteGranularity.Movie,
        TmdbId = tmdbId,
        ImdbId = imdbId
    };

    [Fact]
    public async Task Resolve_Movie_ReturnsTracked_WhenArrHasIt()
    {
        var itemId = Guid.NewGuid();
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));

        var arrClient = new Mock<IArrClient>();
        arrClient.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41, Title = "Test Movie", Year = 2020 });

        var seerrClient = new Mock<ISeerrClient>();
        seerrClient.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 5 });

        var orchestrator = new DeleteOrchestrator(accessor.Object, arrClient.Object, seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Movie);

        Assert.Equal(ArrTrackingState.Tracked, result.State);
        Assert.Equal(41, result.ArrInternalId);
        Assert.Equal(5, result.SeerrMediaId);
        Assert.True(result.HasUsableProviderId);
    }

    [Fact]
    public async Task Resolve_Movie_ReturnsConfirmedNotTracked_WhenArrLacksIt()
    {
        var itemId = Guid.NewGuid();
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));

        var arrClient = new Mock<IArrClient>();
        arrClient.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });

        var seerrClient = new Mock<ISeerrClient>();
        seerrClient.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });

        var orchestrator = new DeleteOrchestrator(accessor.Object, arrClient.Object, seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Movie);

        Assert.Equal(ArrTrackingState.ConfirmedNotTracked, result.State);
    }

    [Fact]
    public async Task Resolve_Movie_ReturnsIndeterminate_WhenArrCallErrors_NeverConfirmedNotTracked()
    {
        var itemId = Guid.NewGuid();
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId));

        var arrClient = new Mock<IArrClient>();
        arrClient.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Indeterminate });

        var seerrClient = new Mock<ISeerrClient>();

        var orchestrator = new DeleteOrchestrator(accessor.Object, arrClient.Object, seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Movie);

        Assert.Equal(ArrTrackingState.Indeterminate, result.State);
        Assert.NotEqual(ArrTrackingState.ConfirmedNotTracked, result.State);
    }

    [Fact]
    public async Task Resolve_ItemWithOnlyImdbId_HasUsableProviderIdIsFalse()
    {
        var itemId = Guid.NewGuid();
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(MakeMovie(itemId, tmdbId: null, imdbId: "tt0080455"));

        var orchestrator = new DeleteOrchestrator(accessor.Object, new Mock<IArrClient>().Object, new Mock<ISeerrClient>().Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Movie);

        Assert.False(result.HasUsableProviderId);
    }

    [Fact]
    public async Task Resolve_SeriesWithNoTmdbId_FallsBackToSeerrSearch_AndVerifiesTvdbId()
    {
        var itemId = Guid.NewGuid();
        var seriesInfo = new JellyfinItemInfo
        {
            Id = itemId,
            Name = "House of the Dragon",
            Granularity = DeleteGranularity.Series,
            TvdbId = "371572"
        };
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(seriesInfo);

        var arrClient = new Mock<IArrClient>();
        arrClient.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });

        var seerrClient = new Mock<ISeerrClient>();
        seerrClient.Setup(s => s.SearchByTitleAsync("House of the Dragon", null, true))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, TmdbId = 94997 });
        seerrClient.Setup(s => s.VerifyTvdbIdAsync(94997, 371572)).ReturnsAsync(true);
        seerrClient.Setup(s => s.FindByTmdbIdAsync(94997, true))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 20 });

        var orchestrator = new DeleteOrchestrator(accessor.Object, arrClient.Object, seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Series);

        Assert.True(result.SeerrMatchFromFallback);
        Assert.Equal(20, result.SeerrMediaId);
    }

    [Fact]
    public async Task Resolve_SeriesWithNoTmdbId_SkipsSeerr_WhenFallbackVerificationFails()
    {
        var itemId = Guid.NewGuid();
        var seriesInfo = new JellyfinItemInfo
        {
            Id = itemId,
            Name = "Some Ambiguous Title",
            Granularity = DeleteGranularity.Series,
            TvdbId = "111"
        };
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(seriesInfo);

        var arrClient = new Mock<IArrClient>();
        arrClient.Setup(a => a.FindByProviderIdAsync("tvdbId", "111", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 9 });

        var seerrClient = new Mock<ISeerrClient>();
        seerrClient.Setup(s => s.SearchByTitleAsync("Some Ambiguous Title", null, true))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, TmdbId = 555 });
        seerrClient.Setup(s => s.VerifyTvdbIdAsync(555, 111)).ReturnsAsync(false);

        var orchestrator = new DeleteOrchestrator(accessor.Object, arrClient.Object, seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Series);

        Assert.Null(result.SeerrMediaId);
    }
}
