using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IArrClient
{
    Task<ArrLookupResult> FindByProviderIdAsync(string providerIdType, string providerIdValue, bool isSeries);
    Task<bool> DeleteAsync(int arrInternalId, bool isSeries);
    Task<int> GetEpisodeFileCoverageCountAsync(int seriesInternalId, int seasonNumber, int episodeNumber);
    Task<bool> DeleteSeasonFilesAsync(int seriesInternalId, int seasonNumber);
    Task<bool> DeleteEpisodeFilesAsync(int seriesInternalId, int seasonNumber, int episodeNumber);
}
