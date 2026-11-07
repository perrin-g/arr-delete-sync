using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class SeriesImpactTests
{
    // Orchestrator decision logic against mocked IArrClient results (our own model types, no
    // Sonarr JSON). Everything that parses real Sonarr responses lives in SonarrFixtureTests.

    // ---- DeleteOrchestrator.GetSeriesImpactAsync ----

    private static readonly Guid SeriesId = Guid.NewGuid();

    private static JellyfinItemInfo Season(Guid id, int season) => new()
    {
        Id = id, Name = $"Season {season}", Granularity = DeleteGranularity.Season,
        SeriesItemId = SeriesId, SeriesName = "FROM", SeriesTvdbId = "401003", SeasonNumber = season, HasPhysicalPath = true
    };

    private static JellyfinItemInfo Episode(Guid id, int season, int episode) => new()
    {
        Id = id, Name = $"E{episode}", Granularity = DeleteGranularity.Episode,
        SeriesItemId = SeriesId, SeriesName = "FROM", SeriesTvdbId = "401003", SeasonNumber = season, EpisodeNumber = episode, HasPhysicalPath = true
    };

    private static (DeleteOrchestrator, Mock<IJellyfinItemAccessor>, Mock<IArrClient>) Make()
    {
        var accessor = new Mock<IJellyfinItemAccessor>();
        var arr = new Mock<IArrClient>();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<bool>())).Returns(arr.Object);
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "401003", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 20 });
        var orchestrator = new DeleteOrchestrator(accessor.Object, factory.Object, new Mock<ISeerrClient>().Object,
            new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);
        return (orchestrator, accessor, arr);
    }

    private static SeriesMonitorState State(bool ended, bool seriesMonitored, params MonitoredEpisode[] episodes) => new()
    {
        Title = "FROM", Ended = ended, SeriesMonitored = seriesMonitored, MonitorsNewSeasons = true, Episodes = episodes
    };

    private static DeleteRequest Req(Guid id, DeleteGranularity g) => new() { JellyfinItemId = id, Granularity = g };

    [Fact]
    public async Task Impact_Empty_WhenOtherMonitoredEpisodesRemain()
    {
        var (orchestrator, accessor, arr) = Make();
        var s4 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s4)).Returns(Season(s4, 4));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, true,
            new MonitoredEpisode(4, 1, true), new MonitoredEpisode(5, 1, true)));

        var impact = await orchestrator.GetSeriesImpactAsync(new[] { Req(s4, DeleteGranularity.Season) });

        Assert.Empty(impact);
    }

    [Fact]
    public async Task Impact_Flags_WhenDeletedSeasonsWereTheOnlyMonitoredEpisodes()
    {
        var (orchestrator, accessor, arr) = Make();
        var s3 = Guid.NewGuid();
        var s4 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s3)).Returns(Season(s3, 3));
        accessor.Setup(a => a.GetItem(s4)).Returns(Season(s4, 4));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, true,
            new MonitoredEpisode(1, 1, false), new MonitoredEpisode(3, 1, true), new MonitoredEpisode(4, 1, true)));

        var impact = await orchestrator.GetSeriesImpactAsync(new[] { Req(s3, DeleteGranularity.Season), Req(s4, DeleteGranularity.Season) });

        var only = Assert.Single(impact);
        Assert.True(only.EmptiesSeries);
        Assert.False(only.WholeSeries);
        Assert.Equal(SeriesId, only.SeriesItemId);
        Assert.Equal("FROM", only.SeriesName);
        Assert.False(only.Ended);
        Assert.Equal(new[] { s3, s4 }.OrderBy(x => x), only.ItemIds.OrderBy(x => x));
    }

    [Fact]
    public async Task Impact_ReportsEnded()
    {
        var (orchestrator, accessor, arr) = Make();
        var s1 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s1)).Returns(Season(s1, 1));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(true, true, new MonitoredEpisode(1, 1, true)));

        var impact = await orchestrator.GetSeriesImpactAsync(new[] { Req(s1, DeleteGranularity.Season) });

        Assert.True(Assert.Single(impact).Ended);
    }

    [Fact]
    public async Task Impact_Episodes_OnlyFlagged_WhenEveryRemainingMonitoredEpisodeIsSelected()
    {
        var (orchestrator, accessor, arr) = Make();
        var e1 = Guid.NewGuid();
        var e2 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(e1)).Returns(Episode(e1, 4, 1));
        accessor.Setup(a => a.GetItem(e2)).Returns(Episode(e2, 4, 2));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, true,
            new MonitoredEpisode(4, 1, true), new MonitoredEpisode(4, 2, true), new MonitoredEpisode(4, 3, true)));

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Req(e1, DeleteGranularity.Episode) }));

        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, true,
            new MonitoredEpisode(4, 1, true), new MonitoredEpisode(4, 2, true), new MonitoredEpisode(4, 3, false)));

        var impact = await orchestrator.GetSeriesImpactAsync(new[] { Req(e1, DeleteGranularity.Episode), Req(e2, DeleteGranularity.Episode) });
        Assert.Single(impact);
    }

    [Fact]
    public async Task Impact_Flags_WhenSeriesItselfIsUnmonitored()
    {
        var (orchestrator, accessor, arr) = Make();
        var s4 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s4)).Returns(Season(s4, 4));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, false,
            new MonitoredEpisode(4, 1, true), new MonitoredEpisode(5, 1, true)));

        Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Req(s4, DeleteGranularity.Season) }));
    }

    [Fact]
    public async Task Impact_Empty_WhenSonarrLookupFails()
    {
        var (orchestrator, accessor, arr) = Make();
        var s4 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s4)).Returns(Season(s4, 4));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync((SeriesMonitorState?)null);

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Req(s4, DeleteGranularity.Season) }));
    }

    [Fact]
    public async Task Impact_Empty_WhenSeriesNotTrackedInSonarr()
    {
        var (orchestrator, accessor, arr) = Make();
        var s4 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s4)).Returns(Season(s4, 4));
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "401003", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Req(s4, DeleteGranularity.Season) }));
        arr.Verify(a => a.GetSeriesMonitorStateAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Impact_IgnoresMovieRequests()
    {
        var (orchestrator, _, arr) = Make();

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Req(Guid.NewGuid(), DeleteGranularity.Movie) }));
        arr.Verify(a => a.GetSeriesMonitorStateAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Impact_Continuing_NotEmptied_IsLeftOut_ButEnded_NotEmptied_IsInfoOnly()
    {
        var (orchestrator, accessor, arr) = Make();
        var s4 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s4)).Returns(Season(s4, 4));
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, true, new MonitoredEpisode(4, 1, true), new MonitoredEpisode(5, 1, true)));
        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Req(s4, DeleteGranularity.Season) }));

        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(true, true, new MonitoredEpisode(4, 1, true), new MonitoredEpisode(5, 1, true)));
        var info = Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Req(s4, DeleteGranularity.Season) }));
        Assert.True(info.Ended);
        Assert.False(info.EmptiesSeries);
    }

    [Fact]
    public async Task Impact_LooksUpEachSeriesOnce_ForManySelectedItems()
    {
        var (orchestrator, accessor, arr) = Make();
        var ids = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            accessor.Setup(a => a.GetItem(ids[i])).Returns(Season(ids[i], i + 1));
        }

        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(State(false, true, new MonitoredEpisode(1, 1, true)));

        await orchestrator.GetSeriesImpactAsync(ids.Select(id => Req(id, DeleteGranularity.Season)).ToList());

        arr.Verify(a => a.GetSeriesMonitorStateAsync(20), Times.Once);
    }

    [Fact]
    public async Task Impact_ReportsWhetherSeriesWillPickUpNewSeasons()
    {
        var (orchestrator, accessor, arr) = Make();
        var s1 = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(s1)).Returns(Season(s1, 1));
        var state = State(false, true, new MonitoredEpisode(1, 1, true));
        state.MonitorsNewSeasons = false;
        arr.Setup(a => a.GetSeriesMonitorStateAsync(20)).ReturnsAsync(state);

        Assert.False(Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Req(s1, DeleteGranularity.Season) })).FollowsNewSeasons);

        state.MonitorsNewSeasons = true;
        Assert.True(Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Req(s1, DeleteGranularity.Season) })).FollowsNewSeasons);

        state.SeriesMonitored = false; // an unmonitored series won't pick anything up either
        Assert.False(Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Req(s1, DeleteGranularity.Season) })).FollowsNewSeasons);
    }

    // ---- keep following: switch Sonarr to monitor the series + new seasons ----

    [Fact]
    public async Task KeepFollowing_ResolvesSeriesAndSwitchesSonarr_AndAudits()
    {
        var accessor = new Mock<IJellyfinItemAccessor>();
        var arr = new Mock<IArrClient>();
        var audit = new Mock<IAuditLogStore>();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(true)).Returns(arr.Object);
        var seriesItem = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(seriesItem)).Returns(new JellyfinItemInfo
        {
            Id = seriesItem, Name = "FROM", Granularity = DeleteGranularity.Series, TvdbId = "401003"
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "401003", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 20 });
        arr.Setup(a => a.SetFollowNewSeasonsAsync(20)).ReturnsAsync(true);
        var orchestrator = new DeleteOrchestrator(accessor.Object, factory.Object, new Mock<ISeerrClient>().Object,
            new Mock<IRetryQueueStore>().Object, audit.Object, new Mock<ICircuitBreaker>().Object);

        Assert.True(await orchestrator.KeepFollowingAsync(seriesItem));

        arr.Verify(a => a.SetFollowNewSeasonsAsync(20), Times.Once);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "FollowNewSeasons" && e.Outcome == "Success" && e.ItemDisplayName == "FROM")), Times.Once);
    }

    [Fact]
    public async Task KeepFollowing_ReturnsFalse_WhenSeriesNotTracked()
    {
        var accessor = new Mock<IJellyfinItemAccessor>();
        var arr = new Mock<IArrClient>();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(true)).Returns(arr.Object);
        var seriesItem = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(seriesItem)).Returns(new JellyfinItemInfo
        {
            Id = seriesItem, Name = "FROM", Granularity = DeleteGranularity.Series, TvdbId = "401003"
        });
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "401003", true))
            .ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked });
        var orchestrator = new DeleteOrchestrator(accessor.Object, factory.Object, new Mock<ISeerrClient>().Object,
            new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        Assert.False(await orchestrator.KeepFollowingAsync(seriesItem));
        arr.Verify(a => a.SetFollowNewSeasonsAsync(It.IsAny<int>()), Times.Never);
    }
}
