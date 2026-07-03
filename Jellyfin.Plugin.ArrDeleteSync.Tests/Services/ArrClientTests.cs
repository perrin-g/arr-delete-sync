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

    [Fact]
    public async Task GetEpisodeFileCoverageCount_ReturnsOne_ForNormalSingleEpisodeFile()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":100,\"episodeIds\":[500]}]")
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var count = await client.GetEpisodeFileCoverageCountAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetEpisodeFileCoverageCount_ReturnsTwo_ForCombinedMultiEpisodeFile()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":100,\"episodeIds\":[500,501]}]")
        });
        var client = new ArrClient(new HttpClient(handler), "http://sonarr:8989", "fakekey");

        var count = await client.GetEpisodeFileCoverageCountAsync(seriesInternalId: 7, seasonNumber: 1, episodeNumber: 1);

        Assert.Equal(2, count);
    }
}
