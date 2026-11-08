using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface ISeerrClient
{
    // False when no Seerr URL is set: Seerr sync is switched off, not failing.
    bool IsConfigured { get; }
    Task<SeerrLookupResult> FindByTmdbIdAsync(int tmdbId, bool isTv);
    Task<SeerrLookupResult?> SearchByTitleAsync(string title, int? year, bool isTv);
    Task<bool> VerifyTvdbIdAsync(int tmdbTvId, int expectedTvdbId);
    Task<bool> UpdateAvailabilityAsync(int seerrMediaId);
    Task<bool> MarkPartiallyAvailableAsync(int seerrMediaId);
}
