using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

// Everything Sonarr-facing here runs the REAL ArrClient against REAL Sonarr 4.0.20 responses
// (Fixtures/sonarr-4.0.20, see its README). No hand-written Sonarr JSON: a hand-written fake once
// invented a field Sonarr doesn't return, so tests passed while every live episode delete failed.
public class SonarrFixtureTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sonarr-4.0.20", name));

    // Minimal fake Sonarr: serves the captured responses by URL and records any PUT.
    private sealed class FakeSonarr
    {
        public readonly List<(string Url, string Body)> Puts = new();
        public readonly List<string> Deletes = new();

        public HttpResponseMessage Handle(HttpRequestMessage req)
        {
            var url = req.RequestUri!.PathAndQuery;
            if (req.Method == HttpMethod.Delete)
            {
                Deletes.Add(url);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (req.Method == HttpMethod.Put)
            {
                Puts.Add((url, req.Content!.ReadAsStringAsync().Result));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            string? body = url switch
            {
                "/api/v3/series" => Fixture("series-list.json"),
                "/api/v3/series?tvdbId=401003" => Fixture("lookup-from.json"),
                "/api/v3/series?tvdbId=75932" => Fixture("lookup-fawlty.json"),
                "/api/v3/series/20" => Fixture("series-from.json"),
                "/api/v3/series/8" => Fixture("series-fawlty.json"),
                "/api/v3/episode?seriesId=20" => Fixture("episodes-from.json"),
                "/api/v3/episode?seriesId=8" => Fixture("episodes-fawlty.json"),
                _ => null
            };

            return body == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private static (ArrClient Client, FakeSonarr Sonarr) MakeClient()
    {
        var sonarr = new FakeSonarr();
        return (new ArrClient(new HttpClient(new FakeHttpMessageHandler(sonarr.Handle)), "http://sonarr:8989", "fakekey"), sonarr);
    }

    // ---- ArrClient against real responses ----

    [Fact]
    public async Task MonitorState_From_Continuing_OnlyS5PlaceholderMonitored()
    {
        var (client, _) = MakeClient();

        var state = await client.GetSeriesMonitorStateAsync(20);

        Assert.NotNull(state);
        Assert.Equal("FROM", state!.Title);
        Assert.False(state.Ended);
        Assert.True(state.SeriesMonitored);
        Assert.True(state.MonitorsNewSeasons);
        Assert.Equal(41, state.Episodes.Count);
        var monitored = Assert.Single(state.Episodes, e => e.Monitored);
        Assert.Equal(5, monitored.SeasonNumber);
    }

    [Fact]
    public async Task MonitorState_Fawlty_Ended()
    {
        var (client, _) = MakeClient();

        var state = await client.GetSeriesMonitorStateAsync(8);

        Assert.True(state!.Ended);
        Assert.Equal(12, state.Episodes.Count(e => e.Monitored));
    }

    [Fact]
    public async Task MonitorState_Null_WhenSeriesMissing()
    {
        var (client, _) = MakeClient();

        Assert.Null(await client.GetSeriesMonitorStateAsync(999));
    }

    [Fact]
    public async Task Coverage_Fawlty_SingleEpisodeFile_IsOne()
    {
        var (client, _) = MakeClient();

        Assert.Equal(1, await client.GetEpisodeFileCoverageCountAsync(8, 1, 1));
    }

    [Fact]
    public async Task Coverage_EpisodeWithoutFile_IsNegative()
    {
        var (client, _) = MakeClient();

        Assert.Equal(-1, await client.GetEpisodeFileCoverageCountAsync(8, 0, 1)); // unmonitored special, no file
    }

    [Fact]
    public async Task SetFollowNewSeasons_PutsRealSeriesBack_ChangingOnlyTheTwoFields()
    {
        var (client, sonarr) = MakeClient();

        Assert.True(await client.SetFollowNewSeasonsAsync(20));

        var put = Assert.Single(sonarr.Puts);
        Assert.Equal("/api/v3/series/20", put.Url);
        var sent = JsonNode.Parse(put.Body)!.AsObject();
        var expected = JsonNode.Parse(Fixture("series-from.json"))!.AsObject();
        expected["monitored"] = true;
        expected["monitorNewItems"] = "all";
        Assert.True(JsonNode.DeepEquals(expected, sent), "PUT body must be the real series resource with only monitored/monitorNewItems changed");
    }

    // ---- full impact check: real ArrClient + real responses ----

    private static readonly Guid FromSeriesItem = Guid.NewGuid();
    private static readonly Guid FawltySeriesItem = Guid.NewGuid();

    private static (DeleteOrchestrator, Mock<IJellyfinItemAccessor>, FakeSonarr, Mock<IAuditLogStore>) MakeOrchestrator()
    {
        var (client, sonarr) = MakeClient();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(true)).Returns(client);
        var accessor = new Mock<IJellyfinItemAccessor>();
        var audit = new Mock<IAuditLogStore>();
        accessor.Setup(a => a.GetItem(FromSeriesItem)).Returns(new JellyfinItemInfo { Id = FromSeriesItem, Name = "FROM", Granularity = DeleteGranularity.Series, TvdbId = "401003" });
        var orchestrator = new DeleteOrchestrator(accessor.Object, factory.Object, new Mock<ISeerrClient>().Object,
            new Mock<IRetryQueueStore>().Object, audit.Object, new Mock<ICircuitBreaker>().Object);
        return (orchestrator, accessor, sonarr, audit);
    }

    private static DeleteRequest Season(Mock<IJellyfinItemAccessor> accessor, bool from, int season)
    {
        var id = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(id)).Returns(new JellyfinItemInfo
        {
            Id = id, Name = $"Season {season}", Granularity = DeleteGranularity.Season, HasPhysicalPath = true,
            SeriesItemId = from ? FromSeriesItem : FawltySeriesItem, SeriesName = from ? "FROM" : "Fawlty Towers",
            SeriesTvdbId = from ? "401003" : "75932", SeasonNumber = season
        });
        return new DeleteRequest { JellyfinItemId = id, Granularity = DeleteGranularity.Season };
    }

    private static DeleteRequest Episode(Mock<IJellyfinItemAccessor> accessor, int season, int episode)
    {
        var id = Guid.NewGuid();
        accessor.Setup(a => a.GetItem(id)).Returns(new JellyfinItemInfo
        {
            Id = id, Name = $"E{episode}", Granularity = DeleteGranularity.Episode, HasPhysicalPath = true,
            SeriesItemId = FawltySeriesItem, SeriesName = "Fawlty Towers", SeriesTvdbId = "75932",
            SeasonNumber = season, EpisodeNumber = episode
        });
        return new DeleteRequest { JellyfinItemId = id, Granularity = DeleteGranularity.Episode };
    }

    [Fact]
    public async Task From_DeleteS4_NoPrompt_BecauseS5PlaceholderStillMonitored()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Season(accessor, true, 4) }));
    }

    [Fact]
    public async Task From_DeleteS5_Prompts_ContinuingAndAlreadyFollowingNewSeasons()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();

        var impact = Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Season(accessor, true, 5) }));

        Assert.Equal(FromSeriesItem, impact.SeriesItemId);
        Assert.Equal("FROM", impact.SeriesName);
        Assert.False(impact.Ended);
        Assert.True(impact.FollowsNewSeasons);
    }

    [Fact]
    public async Task Fawlty_DeleteS1Only_EndedInfoOnly_NoTickbox()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();

        var impact = Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Season(accessor, false, 1) }));

        Assert.True(impact.Ended);
        Assert.False(impact.EmptiesSeries);
        Assert.False(impact.WholeSeries);
    }

    [Fact]
    public async Task WholeSeries_Fawlty_EmptiesAndEnded()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();
        accessor.Setup(a => a.GetItem(FawltySeriesItem)).Returns(new JellyfinItemInfo { Id = FawltySeriesItem, Name = "Fawlty Towers", Granularity = DeleteGranularity.Series, TvdbId = "75932" });

        var impact = Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { new DeleteRequest { JellyfinItemId = FawltySeriesItem, Granularity = DeleteGranularity.Series } }));

        Assert.True(impact.WholeSeries);
        Assert.True(impact.EmptiesSeries);
        Assert.True(impact.Ended);
        Assert.Equal(FawltySeriesItem, impact.SeriesItemId);
        Assert.Equal("Fawlty Towers", impact.SeriesName);
        Assert.Equal(new[] { FawltySeriesItem }, impact.ItemIds);
    }

    [Fact]
    public async Task WholeSeries_From_EmptiesAndContinuing()
    {
        var (orchestrator, _, _, _) = MakeOrchestrator();

        var impact = Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { new DeleteRequest { JellyfinItemId = FromSeriesItem, Granularity = DeleteGranularity.Series } }));

        Assert.True(impact.WholeSeries);
        Assert.True(impact.EmptiesSeries);
        Assert.False(impact.Ended);
        Assert.True(impact.FollowsNewSeasons);
    }

    [Fact]
    public async Task DeleteAllEpisodesKeepSeries_Fawlty_DeletesEveryFile_UnmonitorsAllIncludingSpecials_FollowsNewSeasons()
    {
        var (client, sonarr) = MakeClient();

        Assert.True(await client.DeleteAllEpisodeFilesKeepSeriesAsync(8));

        // every real episode file id in the captured episode list, each deleted exactly once
        var expectedFiles = Enumerable.Range(107, 12).Select(id => $"/api/v3/episodefile/{id}").ToList();
        Assert.Equal(expectedFiles.OrderBy(x => x), sonarr.Deletes.OrderBy(x => x));

        var monitorPut = Assert.Single(sonarr.Puts, p => p.Url == "/api/v3/episode/monitor");
        var body = JsonNode.Parse(monitorPut.Body)!;
        Assert.False(body["monitored"]!.GetValue<bool>());
        var realEpisodeIds = JsonNode.Parse(Fixture("episodes-fawlty.json"))!.AsArray().Select(e => e!["id"]!.GetValue<int>()).OrderBy(x => x);
        Assert.Equal(realEpisodeIds, body["episodeIds"]!.AsArray().Select(x => x!.GetValue<int>()).OrderBy(x => x)); // all 42, specials included

        var seriesPut = JsonNode.Parse(Assert.Single(sonarr.Puts, p => p.Url == "/api/v3/series/8").Body)!;
        Assert.True(seriesPut["monitored"]!.GetValue<bool>());
        Assert.Equal("all", seriesPut["monitorNewItems"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteAllEpisodesKeepSeries_NeverDeletesTheSeries()
    {
        var (client, sonarr) = MakeClient();

        await client.DeleteAllEpisodeFilesKeepSeriesAsync(8);

        Assert.DoesNotContain(sonarr.Deletes, d => d.StartsWith("/api/v3/series", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_AllEpisodesKeepSeries_From_RealClient_SkipsSeerr_RemovesJellyfinCatalogEntry()
    {
        var (client, sonarr) = MakeClient();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<bool>())).Returns(client);
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(FromSeriesItem)).Returns(new JellyfinItemInfo { Id = FromSeriesItem, Name = "FROM", Granularity = DeleteGranularity.Series, TvdbId = "401003", TmdbId = "124364", HasPhysicalPath = true });
        accessor.Setup(a => a.DeleteItem(FromSeriesItem, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny)).Returns(true);
        var seerr = new Mock<ISeerrClient>();
        seerr.Setup(s => s.FindByTmdbIdAsync(124364, true)).ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 9 });
        var orchestrator = new DeleteOrchestrator(accessor.Object, factory.Object, seerr.Object,
            new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = FromSeriesItem, Granularity = DeleteGranularity.AllEpisodesKeepSeries });

        Assert.Null(outcome.BlockedReason);
        Assert.True(outcome.ArrDeleted);
        Assert.True(outcome.JellyfinCleanedUp);
        seerr.Verify(s => s.UpdateAvailabilityAsync(It.IsAny<int>()), Times.Never);
        Assert.DoesNotContain(sonarr.Deletes, d => d.StartsWith("/api/v3/series", StringComparison.Ordinal));
        Assert.Contains(sonarr.Puts, p => p.Url == "/api/v3/series/20");
    }

    [Fact]
    public async Task Fawlty_DeleteBothSeasons_Prompts_AsEnded()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();

        var impact = Assert.Single(await orchestrator.GetSeriesImpactAsync(new[] { Season(accessor, false, 1), Season(accessor, false, 2) }));

        Assert.True(impact.Ended);
        Assert.Equal(2, impact.ItemIds.Count);
    }

    [Fact]
    public async Task Fawlty_EpisodeDeletes_PromptOnlyWhenEveryMonitoredEpisodeSelected()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();
        var all = Enumerable.Range(1, 6).SelectMany(e => new[] { Episode(accessor, 1, e), Episode(accessor, 2, e) }).ToList();

        // Ended series: 11 of 12 is info only (no tickbox); all 12 empties it (tickbox).
        Assert.False(Assert.Single(await orchestrator.GetSeriesImpactAsync(all.Take(11).ToList())).EmptiesSeries);
        var emptied = Assert.Single(await orchestrator.GetSeriesImpactAsync(all));
        Assert.True(emptied.EmptiesSeries);
        Assert.True(emptied.Ended);
    }

    [Fact]
    public async Task KeepFollowing_From_PutsMonitoredAndAllNewSeasons_AndAudits()
    {
        var (orchestrator, _, sonarr, audit) = MakeOrchestrator();

        Assert.True(await orchestrator.KeepFollowingAsync(FromSeriesItem));

        var sent = JsonNode.Parse(Assert.Single(sonarr.Puts).Body)!;
        Assert.True(sent["monitored"]!.GetValue<bool>());
        Assert.Equal("all", sent["monitorNewItems"]!.GetValue<string>());
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "FollowNewSeasons" && e.Outcome == "Success")), Times.Once);
    }

    // ---- followed shows with no episodes ----

    [Fact]
    public async Task GetAllSeries_ParsesRealList()
    {
        var (client, _) = MakeClient();

        var all = await client.GetAllSeriesAsync();

        Assert.Equal(4, all!.Count);
        var lioness = Assert.Single(all, s => s.Title == "Lioness");
        Assert.Equal(32, lioness.ArrSeriesId);
        Assert.True(lioness.Monitored);
        Assert.Equal(0, lioness.EpisodeFileCount);
        Assert.Equal("388589", lioness.TvdbId);
        Assert.Equal(113962, lioness.TmdbId);
        Assert.False(lioness.Ended);
        var fawlty = Assert.Single(all, s => s.Title == "Fawlty Towers");
        Assert.True(fawlty.Ended);
        Assert.True(fawlty.EpisodeFileCount > 0);
    }

    [Fact]
    public async Task FollowedEmpty_ListsMonitoredZeroFileSeries_NotInJellyfin()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();
        accessor.Setup(a => a.SeriesExistsWithTvdbId(It.IsAny<string>())).Returns(false);
        var rickTvdb = JsonNode.Parse(Fixture("series-list.json"))!.AsArray().First(s => s!["title"]!.GetValue<string>() == "Rick and Morty")!["tvdbId"]!.GetValue<int>().ToString();
        accessor.Setup(a => a.SeriesExistsWithTvdbId(rickTvdb)).Returns(true); // still has a Jellyfin entry

        var followed = await orchestrator.GetFollowedEmptySeriesAsync();

        Assert.Equal(new[] { "FROM", "Lioness" }, followed.Select(f => f.Title).OrderBy(t => t));
    }

    [Fact]
    public async Task StopFollowing_EmptySeries_DeletesFromSonarr_ResetsSeerr_Audits()
    {
        var (client, sonarr) = MakeClient();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(true)).Returns(client);
        var seerr = new Mock<ISeerrClient>();
        var tmdb = JsonNode.Parse(Fixture("series-from.json"))!["tmdbId"]!.GetValue<int>();
        seerr.Setup(s => s.FindByTmdbIdAsync(tmdb, true)).ReturnsAsync(new SeerrLookupResult { State = ArrTrackingState.Tracked, MediaId = 9 });
        seerr.Setup(s => s.UpdateAvailabilityAsync(9)).ReturnsAsync(true);
        var audit = new Mock<IAuditLogStore>();
        var orchestrator = new DeleteOrchestrator(new Mock<IJellyfinItemAccessor>().Object, factory.Object, seerr.Object,
            new Mock<IRetryQueueStore>().Object, audit.Object, new Mock<ICircuitBreaker>().Object);

        var outcome = await orchestrator.StopFollowingAsync(20);

        Assert.True(outcome.Ok);
        Assert.Contains(sonarr.Deletes, d => d.StartsWith("/api/v3/series/20", StringComparison.Ordinal));
        seerr.Verify(s => s.UpdateAvailabilityAsync(9), Times.Once);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "StopFollowing" && e.Outcome == "Success" && e.ItemDisplayName == "FROM")), Times.Once);
    }

    [Fact]
    public async Task StopFollowing_Refuses_WhenSeriesStillHasFiles()
    {
        var (client, sonarr) = MakeClient();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(true)).Returns(client);
        var orchestrator = new DeleteOrchestrator(new Mock<IJellyfinItemAccessor>().Object, factory.Object, new Mock<ISeerrClient>().Object,
            new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, new Mock<ICircuitBreaker>().Object);

        var outcome = await orchestrator.StopFollowingAsync(8); // Fawlty Towers: has files

        Assert.False(outcome.Ok);
        Assert.Empty(sonarr.Deletes);
    }

    [Fact]
    public async Task StopFollowing_Refuses_WhenCircuitBreakerTripped()
    {
        var (client, sonarr) = MakeClient();
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(true)).Returns(client);
        var breaker = new Mock<ICircuitBreaker>();
        breaker.Setup(b => b.IsTripped).Returns(true);
        var orchestrator = new DeleteOrchestrator(new Mock<IJellyfinItemAccessor>().Object, factory.Object, new Mock<ISeerrClient>().Object,
            new Mock<IRetryQueueStore>().Object, new Mock<IAuditLogStore>().Object, breaker.Object);

        Assert.False((await orchestrator.StopFollowingAsync(20)).Ok);
        Assert.Empty(sonarr.Deletes);
    }
}
