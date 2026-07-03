using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class SeerrClient : ISeerrClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public SeerrClient(HttpClient httpClient, string baseUrl, string apiKey)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<SeerrLookupResult> FindByTmdbIdAsync(int tmdbId, bool isTv)
    {
        var resource = isTv ? "tv" : "movie";
        var url = $"{_baseUrl}/api/v1/{resource}/{tmdbId}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return new SeerrLookupResult { State = ArrTrackingState.Indeterminate };
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("mediaInfo", out var mediaInfo) ||
                mediaInfo.ValueKind == JsonValueKind.Null)
            {
                return new SeerrLookupResult { State = ArrTrackingState.ConfirmedNotTracked };
            }

            return new SeerrLookupResult
            {
                State = ArrTrackingState.Tracked,
                MediaId = mediaInfo.GetProperty("id").GetInt32(),
                TmdbId = tmdbId
            };
        }
        catch (Exception)
        {
            return new SeerrLookupResult { State = ArrTrackingState.Indeterminate };
        }
    }

    public async Task<SeerrLookupResult?> SearchByTitleAsync(string title, int? year, bool isTv)
    {
        var url = $"{_baseUrl}/api/v1/search?query={HttpUtility.UrlEncode(title)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var expectedMediaType = isTv ? "tv" : "movie";
            var dateField = isTv ? "firstAirDate" : "releaseDate";

            foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (result.GetProperty("mediaType").GetString() != expectedMediaType)
                {
                    continue;
                }

                if (year.HasValue && result.TryGetProperty(dateField, out var dateProp))
                {
                    var dateStr = dateProp.GetString();
                    if (dateStr != null && DateTime.TryParse(dateStr, out var parsedDate) &&
                        Math.Abs(parsedDate.Year - year.Value) > 1)
                    {
                        continue;
                    }
                }

                // Deliberately Indeterminate, not Tracked: a title+year match is only a candidate until
                // VerifyTvdbIdAsync confirms it — reusing the existing "don't trust this yet" state (rather
                // than a plain title/year match claiming to be a confirmed hit) means any future caller that
                // naively checks State == Tracked fails safe by construction, instead of relying on every
                // caller remembering to verify first.
                return new SeerrLookupResult
                {
                    State = ArrTrackingState.Indeterminate,
                    TmdbId = result.GetProperty("id").GetInt32()
                };
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> VerifyTvdbIdAsync(int tmdbTvId, int expectedTvdbId)
    {
        var url = $"{_baseUrl}/api/v1/tv/{tmdbTvId}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("externalIds", out var externalIds) ||
                !externalIds.TryGetProperty("tvdbId", out var tvdbIdProp))
            {
                return false;
            }

            return tvdbIdProp.GetInt32() == expectedTvdbId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> UpdateAvailabilityAsync(int seerrMediaId)
    {
        // Per Task 0 Step 1's confirmed finding: Jellyseerr's DELETE /media/{id}
        // resets mediaInfo to null (full reset), not a narrower "unavailable" state.
        // Update this call if a narrower endpoint was found during verification.
        var url = $"{_baseUrl}/api/v1/media/{seerrMediaId}";

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
