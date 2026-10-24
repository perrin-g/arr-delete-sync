using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class SeerrClientTests
{
    [Fact]
    public async Task FindByTmdbId_ReturnsTracked_WhenMediaInfoPresent()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"mediaInfo\":{\"id\":11,\"tmdbId\":629542}}")
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var result = await client.FindByTmdbIdAsync(629542, isTv: false);

        Assert.Equal(ArrTrackingState.Tracked, result.State);
        Assert.Equal(11, result.MediaId);
    }

    [Fact]
    public async Task FindByTmdbId_ReturnsConfirmedNotTracked_WhenMediaInfoNull()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"mediaInfo\":null}")
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var result = await client.FindByTmdbIdAsync(1, isTv: false);

        Assert.Equal(ArrTrackingState.ConfirmedNotTracked, result.State);
    }

    [Fact]
    public async Task FindByTmdbId_ReturnsIndeterminate_OnError()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var result = await client.FindByTmdbIdAsync(1, isTv: false);

        Assert.Equal(ArrTrackingState.Indeterminate, result.State);
    }

    [Fact]
    public async Task SearchByTitle_ReturnsBestMatch_ByTitleAndYear()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"results\":[" +
                "{\"id\":419192,\"mediaType\":\"movie\",\"title\":\"McLaren\",\"releaseDate\":\"2016-02-14\"}," +
                "{\"id\":1427255,\"mediaType\":\"movie\",\"title\":\"McLaren Homemovie\",\"releaseDate\":\"1936-02-01\"}" +
                "]}")
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var result = await client.SearchByTitleAsync("McLaren", 2016, isTv: false);

        Assert.NotNull(result);
        Assert.Equal(419192, result!.TmdbId);
    }

    [Fact]
    public async Task SearchByTitle_ReturnsNull_WhenNoResults()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"results\":[]}")
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var result = await client.SearchByTitleAsync("Nonexistent Show", 2016, isTv: true);

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyTvdbId_ReturnsTrue_WhenExternalIdsMatch()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"externalIds\":{\"tvdbId\":371572}}")
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var verified = await client.VerifyTvdbIdAsync(94997, expectedTvdbId: 371572);

        Assert.True(verified);
    }

    [Fact]
    public async Task VerifyTvdbId_ReturnsFalse_WhenExternalIdsDoNotMatch()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"externalIds\":{\"tvdbId\":999999}}")
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var verified = await client.VerifyTvdbIdAsync(94997, expectedTvdbId: 371572);

        Assert.False(verified);
    }

    [Fact]
    public async Task UpdateAvailability_CallsDeleteMediaEndpoint()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "fakekey");

        var success = await client.UpdateAvailabilityAsync(11);

        Assert.True(success);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("/api/v1/media/11", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ApiKey_IsSentAsHeader_NeverAsQueryString()
    {
        // Parity with ArrClientTests' equivalent — this constraint applies to every HTTP call
        // this client makes, checked here via FindByTmdbIdAsync as a representative example.
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"mediaInfo\":null}") };
        });
        var client = new SeerrClient(new HttpClient(handler), "http://seerr:5055", "supersecretkey");

        await client.FindByTmdbIdAsync(1, isTv: false);

        Assert.NotNull(captured);
        Assert.DoesNotContain("supersecretkey", captured!.RequestUri!.ToString());
        Assert.True(captured.Headers.Contains("X-Api-Key"));
    }
}
