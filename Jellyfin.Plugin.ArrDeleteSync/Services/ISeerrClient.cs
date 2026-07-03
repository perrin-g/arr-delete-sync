using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface ISeerrClient
{
    Task<SeerrLookupResult> FindByTmdbIdAsync(int tmdbId, bool isTv);
    Task<SeerrLookupResult?> SearchByTitleAsync(string title, int? year, bool isTv);
    Task<bool> VerifyTvdbIdAsync(int tmdbTvId, int expectedTvdbId);
    Task<bool> UpdateAvailabilityAsync(int seerrMediaId);
}
