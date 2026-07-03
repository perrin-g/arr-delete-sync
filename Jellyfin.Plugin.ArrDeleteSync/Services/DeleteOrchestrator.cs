using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public partial class DeleteOrchestrator : IDeleteOrchestrator
{
    private readonly IJellyfinItemAccessor _itemAccessor;
    private readonly IArrClient _arrClient;
    private readonly ISeerrClient _seerrClient;

    public DeleteOrchestrator(IJellyfinItemAccessor itemAccessor, IArrClient arrClient, ISeerrClient seerrClient)
    {
        _itemAccessor = itemAccessor;
        _arrClient = arrClient;
        _seerrClient = seerrClient;
    }

    public async Task<ResolutionResult> ResolveAsync(Guid jellyfinItemId, DeleteGranularity granularity)
    {
        var item = _itemAccessor.GetItem(jellyfinItemId);
        if (item == null)
        {
            return new ResolutionResult { State = ArrTrackingState.ConfirmedNotTracked, HasUsableProviderId = false };
        }

        var isSeries = granularity is DeleteGranularity.Series or DeleteGranularity.Season or DeleteGranularity.Episode;
        var tvdbId = granularity == DeleteGranularity.Movie ? null : (item.SeriesTvdbId ?? item.TvdbId);
        var tmdbId = item.TmdbId;

        if (isSeries && string.IsNullOrEmpty(tmdbId) && !string.IsNullOrEmpty(tvdbId))
        {
            return await ResolveWithSeerrFallbackAsync(item, tvdbId!);
        }

        if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId))
        {
            return new ResolutionResult { State = ArrTrackingState.ConfirmedNotTracked, HasUsableProviderId = false };
        }

        var providerIdType = isSeries ? "tvdbId" : "tmdbId";
        var providerIdValue = isSeries ? tvdbId! : tmdbId!;

        var arrResult = await _arrClient.FindByProviderIdAsync(providerIdType, providerIdValue, isSeries);

        var result = new ResolutionResult
        {
            State = arrResult.State,
            ArrInternalId = arrResult.InternalId,
            ArrTitle = arrResult.Title,
            ArrYear = arrResult.Year,
            ProviderIdType = providerIdType,
            ProviderIdValue = providerIdValue,
            HasUsableProviderId = true
        };

        if (arrResult.State != ArrTrackingState.Indeterminate && !isSeries && int.TryParse(tmdbId, out var tmdbInt))
        {
            var seerrResult = await _seerrClient.FindByTmdbIdAsync(tmdbInt, isTv: false);
            if (seerrResult.State == ArrTrackingState.Tracked)
            {
                result.SeerrMediaId = seerrResult.MediaId;
            }
        }

        return result;
    }

    private async Task<ResolutionResult> ResolveWithSeerrFallbackAsync(JellyfinItemInfo item, string tvdbId)
    {
        var arrResult = await _arrClient.FindByProviderIdAsync("tvdbId", tvdbId, isSeries: true);

        var result = new ResolutionResult
        {
            State = arrResult.State,
            ArrInternalId = arrResult.InternalId,
            ArrTitle = arrResult.Title,
            ArrYear = arrResult.Year,
            ProviderIdType = "tvdbId",
            ProviderIdValue = tvdbId,
            HasUsableProviderId = true
        };

        if (int.TryParse(tvdbId, out var tvdbInt))
        {
            var seerrMatch = await _seerrClient.SearchByTitleAsync(item.Name, null, isTv: true);
            if (seerrMatch?.TmdbId != null)
            {
                var verified = await _seerrClient.VerifyTvdbIdAsync(seerrMatch.TmdbId.Value, tvdbInt);
                if (verified)
                {
                    var confirmed = await _seerrClient.FindByTmdbIdAsync(seerrMatch.TmdbId.Value, isTv: true);
                    if (confirmed.State == ArrTrackingState.Tracked)
                    {
                        result.SeerrMediaId = confirmed.MediaId;
                        result.SeerrMatchFromFallback = true;
                    }
                }
                // Unverified match: leave SeerrMediaId null — logged as "skipped, no
                // verified TMDB match" by the caller (Task 8), not treated as an error here.
            }
        }

        return result;
    }

    // Intentionally not implemented in Task 7 — Task 8 extends this partial class with the
    // real ExecuteDeleteAsync logic. This stub exists only so DeleteOrchestrator (a concrete
    // class instantiated directly in tests) satisfies IDeleteOrchestrator's full contract.
    public Task<DeleteOutcome> ExecuteDeleteAsync(DeleteRequest request)
    {
        throw new NotImplementedException("ExecuteDeleteAsync is implemented in Task 8.");
    }
}
