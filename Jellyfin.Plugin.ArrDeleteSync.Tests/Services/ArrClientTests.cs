using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public HttpRequestMessage? LastRequest { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(_responder(request));
    }
}

public class ArrClientTests
{
    [Fact]
    public async Task FindByProviderId_ReturnsTracked_WhenArrReturnsAMatch()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":41,\"title\":\"Blazing Saddles\",\"year\":1974}]")
        });
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "fakekey");

        var result = await client.FindByProviderIdAsync("tmdbId", "11072", isSeries: false);

        Assert.Equal(ArrTrackingState.Tracked, result.State);
        Assert.Equal(41, result.InternalId);
        Assert.Equal("Blazing Saddles", result.Title);
    }

    [Fact]
    public async Task FindByProviderId_ReturnsConfirmedNotTracked_WhenArrReturnsEmptyArray()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "fakekey");

        var result = await client.FindByProviderIdAsync("tmdbId", "999999", isSeries: false);

        Assert.Equal(ArrTrackingState.ConfirmedNotTracked, result.State);
    }

    [Fact]
    public async Task FindByProviderId_ReturnsIndeterminate_WhenArrErrors()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "fakekey");

        var result = await client.FindByProviderIdAsync("tmdbId", "603", isSeries: false);

        Assert.Equal(ArrTrackingState.Indeterminate, result.State);
    }

    [Fact]
    public async Task FindByProviderId_ReturnsIndeterminate_OnTimeout()
    {
        var handler = new FakeHttpMessageHandler(req => throw new TaskCanceledException("timeout"));
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "fakekey");

        var result = await client.FindByProviderIdAsync("tmdbId", "603", isSeries: false);

        Assert.Equal(ArrTrackingState.Indeterminate, result.State);
    }

    [Theory]
    [InlineData("603&foo=bar")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task FindByProviderId_ReturnsIndeterminate_WithoutHttpCall_WhenProviderIdIsNotANonNegativeInteger(string maliciousOrMalformedValue)
    {
        var handler = new FakeHttpMessageHandler(req => throw new InvalidOperationException("HTTP call should never have been made for an invalid provider ID."));
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "fakekey");

        var result = await client.FindByProviderIdAsync("tmdbId", maliciousOrMalformedValue, isSeries: false);

        Assert.Equal(ArrTrackingState.Indeterminate, result.State);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ApiKey_IsSentAsHeader_NeverAsQueryString()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "supersecretkey");

        await client.FindByProviderIdAsync("tmdbId", "603", isSeries: false);

        Assert.NotNull(captured);
        Assert.DoesNotContain("supersecretkey", captured!.RequestUri!.ToString());
        Assert.True(captured.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task Delete_CallsDeleteWithDeleteFilesTrue()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new ArrClient(new HttpClient(handler), "http://radarr:7878", "fakekey");

        var success = await client.DeleteAsync(41, isSeries: false);

        Assert.True(success);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("deleteFiles=true", handler.LastRequest.RequestUri!.ToString());
    }

    private static HttpResponseMessage RouteEpisodeAndEpisodeFile(HttpRequestMessage req, string episodeListJson, string episodeFileListJson)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/api/v3/episodefile?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(episodeFileListJson) };
        }

        if (url.Contains("/api/v3/episode?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(episodeListJson) };
        }

        throw new InvalidOperationException($"Unexpected URL requested: {url}");
    }

    [Fact]
    public async Task GetEpisodeFileCoverageCount_ReturnsOne_ForNormalSingleEpisodeFile()
    {
        var handler = new FakeHttpMessageHandler(req => RouteEpisodeAndEpisodeFile(
            req,
            episodeListJson: "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}]",
            episodeFileListJson: "[{\"id\":100,\"episodeIds\":[500]}]"));
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var count = await client.GetEpisodeFileCoverageCountAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetEpisodeFileCoverageCount_ReturnsTwo_ForCombinedMultiEpisodeFile()
    {
        var handler = new FakeHttpMessageHandler(req => RouteEpisodeAndEpisodeFile(
            req,
            episodeListJson: "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}]",
            episodeFileListJson: "[{\"id\":100,\"episodeIds\":[500,501]}]"));
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var count = await client.GetEpisodeFileCoverageCountAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetEpisodeFileCoverageCount_SelectsCorrectFile_WhenSeriesHasMultipleEpisodeFiles()
    {
        // Episode 1x01 maps to episodeFileId 100 (a single-episode file). The episodefile
        // response also contains an unrelated multi-episode file (id 200) for some other
        // episode — the old buggy implementation would have returned that file's count (2)
        // because it just took whichever file was first in the array. The fix must select
        // file 100 specifically and return 1.
        var handler = new FakeHttpMessageHandler(req => RouteEpisodeAndEpisodeFile(
            req,
            episodeListJson: "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}]",
            episodeFileListJson: "[{\"id\":200,\"episodeIds\":[600,601]},{\"id\":100,\"episodeIds\":[500]}]"));
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var count = await client.GetEpisodeFileCoverageCountAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetEpisodeFileCoverageCount_ReturnsNegativeOne_WhenEpisodeNotFoundOrHasNoFile()
    {
        var handler = new FakeHttpMessageHandler(req => RouteEpisodeAndEpisodeFile(
            req,
            episodeListJson: "[{\"seasonNumber\":1,\"episodeNumber\":2,\"episodeFileId\":100}]",
            episodeFileListJson: "[]"));
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var count = await client.GetEpisodeFileCoverageCountAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Equal(-1, count);
    }

    // Regression coverage for the bug where a Season/Episode delete wiped the whole Sonarr
    // series: these exercise the season/episode-scoped file deletion that must be used instead
    // of the whole-series DeleteAsync for these two granularities.

    [Fact]
    public async Task DeleteSeasonFiles_DeletesAllDistinctFilesInSeason_NotOtherSeasons()
    {
        var deletedIds = new List<int>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                deletedIds.Add(int.Parse(req.RequestUri!.Segments.Last()));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}," +
                    "{\"seasonNumber\":1,\"episodeNumber\":2,\"episodeFileId\":101}," +
                    "{\"seasonNumber\":2,\"episodeNumber\":1,\"episodeFileId\":200}]")
            };
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteSeasonFilesAsync(seriesInternalId: 7, seasonNumber: 1);

        Assert.True(success);
        Assert.Equal(new[] { 100, 101 }, deletedIds.OrderBy(x => x));
    }

    [Fact]
    public async Task DeleteSeasonFiles_DeduplicatesSharedMultiEpisodeFile()
    {
        var deletedIds = new List<int>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                deletedIds.Add(int.Parse(req.RequestUri!.Segments.Last()));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}," +
                    "{\"seasonNumber\":1,\"episodeNumber\":2,\"episodeFileId\":100}]")
            };
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteSeasonFilesAsync(seriesInternalId: 7, seasonNumber: 1);

        Assert.True(success);
        Assert.Single(deletedIds);
        Assert.Equal(100, deletedIds[0]);
    }

    [Fact]
    public async Task DeleteSeasonFiles_ReturnsTrue_WhenSeasonHasNoFiles()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"seasonNumber\":2,\"episodeNumber\":1,\"episodeFileId\":200}]")
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteSeasonFilesAsync(seriesInternalId: 7, seasonNumber: 1);

        Assert.True(success);
    }

    [Fact]
    public async Task DeleteSeasonFiles_ReturnsFalse_WhenAnyFileDeleteFails_ButStillAttemptsAll()
    {
        var deletedIds = new List<int>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                var id = int.Parse(req.RequestUri!.Segments.Last());
                deletedIds.Add(id);
                return new HttpResponseMessage(id == 100 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}," +
                    "{\"seasonNumber\":1,\"episodeNumber\":2,\"episodeFileId\":101}]")
            };
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteSeasonFilesAsync(seriesInternalId: 7, seasonNumber: 1);

        Assert.False(success);
        Assert.Equal(new[] { 100, 101 }, deletedIds.OrderBy(x => x));
    }

    [Fact]
    public async Task DeleteSeasonFiles_ReturnsFalse_WhenEpisodeListFetchFails()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteSeasonFilesAsync(seriesInternalId: 7, seasonNumber: 1);

        Assert.False(success);
    }

    [Fact]
    public async Task DeleteEpisodeFiles_DeletesOnlyThatEpisodesFile_UsesCorrectUrlAndMethod()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}," +
                    "{\"seasonNumber\":1,\"episodeNumber\":2,\"episodeFileId\":101}]")
            };
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteEpisodeFilesAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.True(success);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.EndsWith("/api/v3/episodefile/100", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteEpisodeFiles_DoesNotSendRequestBody()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"seasonNumber\":1,\"episodeNumber\":1,\"episodeFileId\":100}]")
            };
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        await client.DeleteEpisodeFilesAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Null(handler.LastRequest!.Content);
    }

    [Fact]
    public async Task DeleteEpisodeFiles_ReturnsFalse_WhenEpisodeNotFoundOrHasNoFile()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"seasonNumber\":1,\"episodeNumber\":2,\"episodeFileId\":100}]")
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var success = await client.DeleteEpisodeFilesAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.False(success);
    }
}
