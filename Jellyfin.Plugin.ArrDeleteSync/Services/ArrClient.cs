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
}
