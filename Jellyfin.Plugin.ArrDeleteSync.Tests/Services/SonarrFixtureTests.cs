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

        public HttpResponseMessage Handle(HttpRequestMessage req)
        {
            var url = req.RequestUri!.PathAndQuery;
            if (req.Method == HttpMethod.Put)
            {
                Puts.Add((url, req.Content!.ReadAsStringAsync().Result));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            string? body = url switch
            {
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
    public async Task Fawlty_DeleteS1Only_NoPrompt()
    {
        var (orchestrator, accessor, _, _) = MakeOrchestrator();

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(new[] { Season(accessor, false, 1) }));
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

        Assert.Empty(await orchestrator.GetSeriesImpactAsync(all.Take(11).ToList()));
        Assert.True(Assert.Single(await orchestrator.GetSeriesImpactAsync(all)).Ended);
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
}
