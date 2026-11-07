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
        if (!int.TryParse(providerIdValue, out var numericProviderId) || numericProviderId < 0)
        {
            // TMDB/TVDB IDs are always non-negative integers. Reject anything else before it
            // reaches the URL so a malformed/poisoned value scraped from media metadata can't
            // inject extra query parameters into the Radarr/Sonarr request.
            return new ArrLookupResult { State = ArrTrackingState.Indeterminate };
        }

        var resource = isSeries ? "series" : "movie";
        var queryParam = providerIdType.Equals("tvdbId", StringComparison.OrdinalIgnoreCase) ? "tvdbId" : "tmdbId";
        var url = $"{_baseUrl}/api/v3/{resource}?{queryParam}={numericProviderId}";

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

    // Counts how many episodes the file backing this episode covers (1 = normal, >1 = multi-
    // episode file). Derived from the episode list itself -- every episode backed by the same
    // file shares its episodeFileId. Sonarr v4's /api/v3/episodefile has no "episodeIds" field
    // (verified on 4.0.20), so it can't be used for this.
    public async Task<int> GetEpisodeFileCoverageCountAsync(int seriesInternalId, int seasonNumber, int episodeNumber)
    {
        var episodes = await FetchEpisodesAsync(seriesInternalId);
        if (episodes == null)
        {
            return -1; // caller treats negative as indeterminate and blocks conservatively
        }

        var match = episodes.Find(e => e.SeasonNumber == seasonNumber && e.EpisodeNumber == episodeNumber);
        if (match.EpisodeFileId == 0)
        {
            return -1; // episode not found, or has no file — can't confirm coverage
        }

        return episodes.Count(e => e.EpisodeFileId == match.EpisodeFileId);
    }

    // Null on any failure -- the caller treats that as "can't tell" and offers no remove-series
    // option, so a Sonarr hiccup can never lead to a whole series being removed by surprise.
    public async Task<SeriesMonitorState?> GetSeriesMonitorStateAsync(int seriesInternalId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v3/series/{seriesInternalId}");
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            var seriesMonitored = root.TryGetProperty("monitored", out var m) && m.ValueKind == JsonValueKind.True;
            var monitorNewItems = root.TryGetProperty("monitorNewItems", out var mni) ? mni.GetString() : null;

            var episodes = await FetchEpisodesAsync(seriesInternalId);
            if (episodes == null)
            {
                return null;
            }

            return new SeriesMonitorState
            {
                Title = title,
                Ended = string.Equals(status, "ended", StringComparison.OrdinalIgnoreCase),
                SeriesMonitored = seriesMonitored,
                MonitorsNewSeasons = string.Equals(monitorNewItems, "all", StringComparison.OrdinalIgnoreCase),
                Episodes = episodes.Select(e => new MonitoredEpisode(e.SeasonNumber, e.EpisodeNumber, e.Monitored)).ToList()
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // "Keep following" after a delete leaves nothing monitored: make sure Sonarr will actually
    // download new seasons -- series monitored + Monitor New Seasons = all. Sonarr's PUT takes the
    // whole series resource, so read it, change just those two fields, and send it back unchanged
    // otherwise (no moveFiles, so the path is never touched).
    public async Task<bool> SetFollowNewSeasonsAsync(int seriesInternalId)
    {
        var url = $"{_baseUrl}/api/v3/series/{seriesInternalId}";
        try
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            getRequest.Headers.Add("X-Api-Key", _apiKey);
            using var getResponse = await _httpClient.SendAsync(getRequest);
            if (!getResponse.IsSuccessStatusCode)
            {
                return false;
            }

            if (System.Text.Json.Nodes.JsonNode.Parse(await getResponse.Content.ReadAsStringAsync()) is not System.Text.Json.Nodes.JsonObject series)
            {
                return false;
            }

            series["monitored"] = true;
            series["monitorNewItems"] = "all";

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(series.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            putRequest.Headers.Add("X-Api-Key", _apiKey);
            using var putResponse = await _httpClient.SendAsync(putRequest);
            return putResponse.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Whole-series delete with the series kept: every episode file goes (specials included), every
    // episode is unmonitored so nothing re-downloads, and the series stays monitored for new
    // seasons. The series record itself is never deleted.
    public async Task<bool> DeleteAllEpisodeFilesKeepSeriesAsync(int seriesInternalId)
    {
        var episodes = await FetchEpisodesAsync(seriesInternalId);
        if (episodes == null)
        {
            return false;
        }

        var allSucceeded = true;
        foreach (var fileId in episodes.Where(e => e.EpisodeFileId != 0).Select(e => e.EpisodeFileId).Distinct())
        {
            if (!await DeleteEpisodeFileByIdAsync(fileId))
            {
                allSucceeded = false;
            }
        }

        if (!allSucceeded)
        {
            return false;
        }

        var episodeIds = episodes.Select(e => e.Id).Where(id => id != 0).ToList();
        return await SetEpisodesMonitoredAsync(episodeIds, monitored: false)
            && await SetFollowNewSeasonsAsync(seriesInternalId);
    }

    public async Task<IReadOnlyList<FollowedSeries>?> GetAllSeriesAsync()
    {
        var body = await GetBodyAsync($"{_baseUrl}/api/v3/series");
        if (body == null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.EnumerateArray().Select(ParseSeriesSummary).ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<FollowedSeries?> GetSeriesSummaryAsync(int seriesInternalId)
    {
        var body = await GetBodyAsync($"{_baseUrl}/api/v3/series/{seriesInternalId}");
        if (body == null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseSeriesSummary(doc.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FollowedSeries ParseSeriesSummary(JsonElement s)
    {
        var fileCount = s.TryGetProperty("statistics", out var stats) && stats.TryGetProperty("episodeFileCount", out var fc) ? fc.GetInt32() : 0;
        return new FollowedSeries
        {
            ArrSeriesId = s.GetProperty("id").GetInt32(),
            Title = s.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
            Ended = s.TryGetProperty("status", out var st) && string.Equals(st.GetString(), "ended", StringComparison.OrdinalIgnoreCase),
            Monitored = s.TryGetProperty("monitored", out var m) && m.ValueKind == JsonValueKind.True,
            EpisodeFileCount = fileCount,
            TvdbId = s.TryGetProperty("tvdbId", out var tv) && tv.ValueKind == JsonValueKind.Number && tv.GetInt32() > 0 ? tv.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            TmdbId = s.TryGetProperty("tmdbId", out var tm) && tm.ValueKind == JsonValueKind.Number && tm.GetInt32() > 0 ? tm.GetInt32() : null
        };
    }

    private async Task<string?> GetBodyAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _apiKey);
            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Deletes every distinct episode file in the given season, without touching the series or
    // any other season -- unlike DeleteAsync (whole series/movie), used only for Movie/Series
    // granularity. Attempts every file even if one delete fails, rather than stopping at the
    // first failure, so a single bad file can't strand the rest of the season undeleted; the
    // caller (DeleteOrchestrator) queues a retry on any false, and re-fetching the episode list
    // fresh on each retry means already-deleted files simply won't reappear (Sonarr clears their
    // episodeFileId once gone), so a retry never re-attempts them.
    public async Task<bool> DeleteSeasonFilesAsync(int seriesInternalId, int seasonNumber)
    {
        var episodes = await FetchEpisodesAsync(seriesInternalId);
        if (episodes == null)
        {
            return false;
        }

        var seasonEpisodes = episodes.Where(e => e.SeasonNumber == seasonNumber).ToList();

        var fileIds = seasonEpisodes
            .Where(e => e.EpisodeFileId != 0)
            .Select(e => e.EpisodeFileId)
            .Distinct() // a combined multi-episode file must only be deleted once
            .ToList();

        var allSucceeded = true;
        foreach (var fileId in fileIds)
        {
            if (!await DeleteEpisodeFileByIdAsync(fileId))
            {
                allSucceeded = false;
            }
        }

        if (!allSucceeded)
        {
            return false;
        }

        // Unmonitor every episode in the season, not just the ones that had a file -- Sonarr
        // leaves deleted content monitored by default, and without this its own automatic
        // search/RSS sync can silently re-download exactly what was just deleted, undoing it.
        // Includes not-yet-aired episodes too, since the whole season is being deleted.
        var episodeIds = seasonEpisodes.Select(e => e.Id).Where(id => id != 0).ToList();
        return await SetEpisodesMonitoredAsync(episodeIds, monitored: false);
    }

    // Deletes the single file backing one episode. The caller already verifies (via
    // GetEpisodeFileCoverageCountAsync) that this file covers only this one episode before
    // calling here -- this re-fetches the episode list a second time rather than threading the
    // already-resolved file id through, matching this codebase's existing precedent of
    // re-resolving rather than caching (see ProcessRetryEntryAsync's doc comment) and costing one
    // extra GET on what is always a human-paced, admin-triggered action.
    public async Task<bool> DeleteEpisodeFilesAsync(int seriesInternalId, int seasonNumber, int episodeNumber)
    {
        var episodes = await FetchEpisodesAsync(seriesInternalId);
        if (episodes == null)
        {
            return false;
        }

        var match = episodes.Find(e => e.SeasonNumber == seasonNumber && e.EpisodeNumber == episodeNumber);
        if (match.EpisodeFileId == 0)
        {
            return false;
        }

        if (!await DeleteEpisodeFileByIdAsync(match.EpisodeFileId))
        {
            return false;
        }

        if (match.Id == 0)
        {
            return true; // no episode id to unmonitor against (shouldn't happen for a matched episode)
        }

        return await SetEpisodesMonitoredAsync(new List<int> { match.Id }, monitored: false);
    }

    private readonly record struct EpisodeRecord(int Id, int SeasonNumber, int EpisodeNumber, int EpisodeFileId, bool Monitored);

    private async Task<List<EpisodeRecord>?> FetchEpisodesAsync(int seriesInternalId)
    {
        var episodeUrl = $"{_baseUrl}/api/v3/episode?seriesId={seriesInternalId}";

        try
        {
            using var episodeRequest = new HttpRequestMessage(HttpMethod.Get, episodeUrl);
            episodeRequest.Headers.Add("X-Api-Key", _apiKey);
            using var episodeResponse = await _httpClient.SendAsync(episodeRequest);

            if (!episodeResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var episodeBody = await episodeResponse.Content.ReadAsStringAsync();
            using var episodeDoc = JsonDocument.Parse(episodeBody);

            var episodes = new List<EpisodeRecord>();
            foreach (var episode in episodeDoc.RootElement.EnumerateArray())
            {
                var id = episode.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                var season = episode.TryGetProperty("seasonNumber", out var s) ? s.GetInt32() : -1;
                var number = episode.TryGetProperty("episodeNumber", out var e) ? e.GetInt32() : -1;
                var fileId = episode.TryGetProperty("episodeFileId", out var fileIdProp) ? fileIdProp.GetInt32() : 0;
                var monitored = episode.TryGetProperty("monitored", out var monitoredProp) && monitoredProp.ValueKind == JsonValueKind.True;
                episodes.Add(new EpisodeRecord(id, season, number, fileId, monitored));
            }

            return episodes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> DeleteEpisodeFileByIdAsync(int episodeFileId)
    {
        var url = $"{_baseUrl}/api/v3/episodefile/{episodeFileId}";

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

    // Confirmed from Sonarr's own source (EpisodeController.SetEpisodesMonitored) that
    // PUT /api/v3/episode/monitor is the bulk endpoint for this, taking {episodeIds, monitored}.
    // No episode ids to unmonitor is a vacuous success, not a call worth making.
    private async Task<bool> SetEpisodesMonitoredAsync(IReadOnlyList<int> episodeIds, bool monitored)
    {
        if (episodeIds.Count == 0)
        {
            return true;
        }

        var url = $"{_baseUrl}/api/v3/episode/monitor";
        var body = JsonSerializer.Serialize(new { episodeIds, monitored });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
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
