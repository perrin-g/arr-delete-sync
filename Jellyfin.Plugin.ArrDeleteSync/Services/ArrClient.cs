using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class ArrClient : IArrClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public ArrClient(HttpClient httpClient, string baseUrl, string apiKey)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<ArrLookupResult> FindByProviderIdAsync(string providerIdType, string providerIdValue, bool isSeries)
    {
        var resource = isSeries ? "series" : "movie";
        var queryParam = providerIdType.Equals("tvdbId", StringComparison.OrdinalIgnoreCase) ? "tvdbId" : "tmdbId";
        var url = $"{_baseUrl}/api/v3/{resource}?{queryParam}={providerIdValue}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return new ArrLookupResult { State = ArrTrackingState.Indeterminate };
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var results = doc.RootElement;

            if (results.GetArrayLength() == 0)
            {
                return new ArrLookupResult { State = ArrTrackingState.ConfirmedNotTracked };
            }

            var first = results[0];
            return new ArrLookupResult
            {
                State = ArrTrackingState.Tracked,
                InternalId = first.GetProperty("id").GetInt32(),
                Title = first.TryGetProperty("title", out var t) ? t.GetString() : null,
                Year = first.TryGetProperty("year", out var y) ? y.GetInt32() : null
            };
        }
        catch (Exception)
        {
            return new ArrLookupResult { State = ArrTrackingState.Indeterminate };
        }
    }

    public async Task<bool> DeleteAsync(int arrInternalId, bool isSeries)
    {
        var resource = isSeries ? "series" : "movie";
        var url = $"{_baseUrl}/api/v3/{resource}/{arrInternalId}?deleteFiles=true";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<int> GetEpisodeFileCoverageCountAsync(int seriesInternalId, int seasonNumber, int episodeNumber)
    {
        var episodeUrl = $"{_baseUrl}/api/v3/episode?seriesId={seriesInternalId}";

        try
        {
            using var episodeRequest = new HttpRequestMessage(HttpMethod.Get, episodeUrl);
            episodeRequest.Headers.Add("X-Api-Key", _apiKey);
            using var episodeResponse = await _httpClient.SendAsync(episodeRequest);

            if (!episodeResponse.IsSuccessStatusCode)
            {
                return -1; // caller treats negative as indeterminate and blocks conservatively
            }

            var episodeBody = await episodeResponse.Content.ReadAsStringAsync();
            using var episodeDoc = JsonDocument.Parse(episodeBody);

            var episodeFileId = 0;
            var found = false;
            foreach (var episode in episodeDoc.RootElement.EnumerateArray())
            {
                if (episode.TryGetProperty("seasonNumber", out var s) && s.GetInt32() == seasonNumber &&
                    episode.TryGetProperty("episodeNumber", out var e) && e.GetInt32() == episodeNumber)
                {
                    found = true;
                    if (episode.TryGetProperty("episodeFileId", out var fileIdProp))
                    {
                        episodeFileId = fileIdProp.GetInt32();
                    }

                    break;
                }
            }

            if (!found || episodeFileId == 0)
            {
                return -1; // episode not found, or has no file — can't confirm coverage
            }

            var episodeFileUrl = $"{_baseUrl}/api/v3/episodefile?seriesId={seriesInternalId}";
            using var fileRequest = new HttpRequestMessage(HttpMethod.Get, episodeFileUrl);
            fileRequest.Headers.Add("X-Api-Key", _apiKey);
            using var fileResponse = await _httpClient.SendAsync(fileRequest);

            if (!fileResponse.IsSuccessStatusCode)
            {
                return -1;
            }

            var fileBody = await fileResponse.Content.ReadAsStringAsync();
            using var fileDoc = JsonDocument.Parse(fileBody);

            foreach (var file in fileDoc.RootElement.EnumerateArray())
            {
                if (file.TryGetProperty("id", out var idProp) && idProp.GetInt32() == episodeFileId &&
                    file.TryGetProperty("episodeIds", out var episodeIds))
                {
                    return episodeIds.GetArrayLength();
                }
            }

            return -1; // matching file not found in the episodefile response
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
