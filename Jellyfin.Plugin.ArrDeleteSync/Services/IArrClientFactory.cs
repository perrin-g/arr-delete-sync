namespace Jellyfin.Plugin.ArrDeleteSync.Services;

// Radarr (movies) and Sonarr (series/season/episode) are two separate arr instances with
// separate base URLs/API keys, but share the exact same ArrClient shape (Task 5) since both
// expose the same {resource}/{lookup} REST surface. DeleteOrchestrator needs to reach whichever
// one is correct for a given call's isSeries value without knowing about both instances
// directly — this factory is that seam. See ServiceRegistrator for the production
// implementation (one IArrClient per provider, both built from named IHttpClientFactory
// clients) and the DeleteOrchestrator*Tests files for the test-double implementation (a Moq
// stub that can return either the same mock for both, or two distinct mocks to prove routing).
public interface IArrClientFactory
{
    IArrClient GetClient(bool isSeries);
}
