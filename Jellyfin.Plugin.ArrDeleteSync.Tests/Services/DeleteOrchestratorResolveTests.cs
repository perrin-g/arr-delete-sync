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

    // Every existing test here only cares about resolve/execute logic against "an arr client",
    // not about Radarr-vs-Sonarr routing — so this stub returns the same mocked client for both
    // isSeries values, preserving prior behavior under DeleteOrchestrator's new constructor
    // shape. The dedicated routing test below sets up two distinct clients instead.
    private static IArrClientFactory MakeArrClientFactory(IArrClient arrClient)
    {
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<bool>())).Returns(arrClient);
        return factory.Object;
    }

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

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arrClient.Object), seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

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

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arrClient.Object), seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

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

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arrClient.Object), seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

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

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(new Mock<IArrClient>().Object), new Mock<ISeerrClient>().Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Movie);

        Assert.False(result.HasUsableProviderId);
    }

    [Fact]
    public async Task Resolve_SeriesWithTmdbId_ResolvesSeerrMediaId_UsingTvLookup()
    {
        // Regression test: ResolveAsync used to only query Seerr for movies (isSeries guard on
        // the tmdbId lookup), so any series/season/episode item that already carries a TmdbId
        // (the common case — arr's own metadata usually includes it) never got a SeerrMediaId,
        // ExecuteDeleteAsync's seerrUpdated defaulted to true with no Seerr call ever made, and
        // the delete was logged as a full "Success" while Seerr silently kept showing the title
        // as available. Reproduced live against a real Seerr instance before this fix.
        var itemId = Guid.NewGuid();
        var seriesInfo = new JellyfinItemInfo
        {
            Id = itemId,
            Name = "Little Britain USA",
            Granularity = DeleteGranularity.Series,
            TmdbId = "14880",
            TvdbId = "83232"
        };
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(seriesInfo);

        var arrClient = new Mock<IArrClient>();
        arrClient.Setup(a => a.FindByProviderIdAsync("tvdbId", "83232", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 10 });

        var seerrClient = new Mock<ISeerrClient>();
        seerrClient.Setup(s => s.FindByTmdbIdAsync(14880, true))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 14 });

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arrClient.Object), seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Series);

        seerrClient.Verify(s => s.FindByTmdbIdAsync(14880, true), Times.Once);
        Assert.Equal(14, result.SeerrMediaId);
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

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arrClient.Object), seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

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

        var orchestrator = new DeleteOrchestrator(accessor.Object, MakeArrClientFactory(arrClient.Object), seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var result = await orchestrator.ResolveAsync(itemId, DeleteGranularity.Series);

        Assert.Null(result.SeerrMediaId);
    }

    [Fact]
    public async Task Resolve_RoutesToCorrectArrClient_MovieUsesRadarrClient_SeriesUsesSonarrClient()
    {
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(movieId)).Returns(MakeMovie(movieId));
        accessor.Setup(a => a.GetItem(seriesId)).Returns(new JellyfinItemInfo
        {
            Id = seriesId,
            Name = "Test Series",
            Granularity = DeleteGranularity.Series,
            TvdbId = "371572"
        });

        // Two DISTINCT mocks — this is the crux of the test: if DeleteOrchestrator ever calls
        // the wrong one (e.g. always the "Radarr" client, regardless of isSeries), the series
        // resolve below would come back empty/wrong instead of hitting the Sonarr mock's setup.
        var radarrClient = new Mock<IArrClient>();
        radarrClient.Setup(a => a.FindByProviderIdAsync("tmdbId", "603", false))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 41, Title = "Test Movie" });

        var sonarrClient = new Mock<IArrClient>();
        sonarrClient.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7, Title = "Test Series" });

        var arrClientFactory = new Mock<IArrClientFactory>();
        arrClientFactory.Setup(f => f.GetClient(false)).Returns(radarrClient.Object);
        arrClientFactory.Setup(f => f.GetClient(true)).Returns(sonarrClient.Object);

        var seerrClient = new Mock<ISeerrClient>();
        seerrClient.Setup(s => s.FindByTmdbIdAsync(603, false))
            .ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });

        var orchestrator = new DeleteOrchestrator(accessor.Object, arrClientFactory.Object, seerrClient.Object, new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var movieResult = await orchestrator.ResolveAsync(movieId, DeleteGranularity.Movie);
        var seriesResult = await orchestrator.ResolveAsync(seriesId, DeleteGranularity.Series);

        Assert.Equal(41, movieResult.ArrInternalId);
        Assert.Equal(7, seriesResult.ArrInternalId);

        radarrClient.Verify(a => a.FindByProviderIdAsync("tmdbId", "603", false), Times.Once);
        radarrClient.Verify(a => a.FindByProviderIdAsync(It.IsAny<string>(), It.IsAny<string>(), true), Times.Never);
        sonarrClient.Verify(a => a.FindByProviderIdAsync("tvdbId", "371572", true), Times.Once);
        sonarrClient.Verify(a => a.FindByProviderIdAsync(It.IsAny<string>(), It.IsAny<string>(), false), Times.Never);
    }
}
