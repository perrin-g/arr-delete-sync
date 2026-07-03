// IPluginServiceRegistrator's exact interface shape was verified by reflecting on the installed
// Jellyfin.Controller 10.11.11 package (MediaBrowser.Controller.dll) rather than assuming the
// brief's reference signature was correct as written — it was:
//   void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
// which matches the brief's reference code exactly, so no signature adjustment was needed here.
using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ArrDeleteSync;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IJellyfinItemAccessor, JellyfinItemAccessor>();
        serviceCollection.AddSingleton<ICircuitBreaker>(_ =>
        {
            var config = Plugin.Instance!.Configuration;
            return new CircuitBreaker(config.CircuitBreakerThreshold, config.CircuitBreakerWindowMinutes);
        });
        serviceCollection.AddSingleton<IRetryQueueStore>(provider =>
        {
            var paths = provider.GetRequiredService<IApplicationPaths>();
            return new RetryQueueStore(Path.Combine(paths.DataPath, "arrdeletesync"));
        });
        serviceCollection.AddSingleton<IAuditLogStore>(provider =>
        {
            var paths = provider.GetRequiredService<IApplicationPaths>();
            return new AuditLogStore(Path.Combine(paths.DataPath, "arrdeletesync"));
        });
        serviceCollection.AddHttpClient();

        // Radarr (movies) and Sonarr (series/season/episode) are two distinct arr instances with
        // separate base URLs/API keys, but DeleteOrchestrator has a single field/constructor
        // parameter and picks the right one per-call via isSeries. IArrClientFactory is the seam:
        // build both concrete IArrClient instances once here (each against its own named
        // HttpClient, matching the single-instance pattern the brief's reference code used for
        // Radarr alone) and hand out the correct one from GetClient(isSeries).
        serviceCollection.AddSingleton<IArrClientFactory>(provider =>
        {
            var config = Plugin.Instance!.Configuration;
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

            var radarrHttpClient = httpClientFactory.CreateClient("ArrDeleteSync-Radarr");
            radarrHttpClient.Timeout = TimeSpan.FromSeconds(15);
            var radarrKey = Plugin.Instance!.KeyProtector.Unprotect(config.RadarrApiKeyEncrypted);
            var radarrClient = new ArrClient(radarrHttpClient, config.RadarrUrl, radarrKey);

            var sonarrHttpClient = httpClientFactory.CreateClient("ArrDeleteSync-Sonarr");
            sonarrHttpClient.Timeout = TimeSpan.FromSeconds(15);
            var sonarrKey = Plugin.Instance!.KeyProtector.Unprotect(config.SonarrApiKeyEncrypted);
            var sonarrClient = new ArrClient(sonarrHttpClient, config.SonarrUrl, sonarrKey);

            return new ArrClientFactory(radarrClient, sonarrClient);
        });
        serviceCollection.AddSingleton<ISeerrClient>(provider =>
        {
            var config = Plugin.Instance!.Configuration;
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ArrDeleteSync-Seerr");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            var key = Plugin.Instance!.KeyProtector.Unprotect(config.SeerrApiKeyEncrypted);
            return new SeerrClient(httpClient, config.SeerrUrl, key);
        });
        serviceCollection.AddSingleton<IDeleteOrchestrator, DeleteOrchestrator>();
        // Registering as IScheduledTask is what makes Jellyfin's TaskManager discover and run
        // this on its own schedule — without this line, RetryQueueTask exists as a compilable
        // class but is never actually invoked by the real Jellyfin host.
        serviceCollection.AddSingleton<IScheduledTask, ScheduledTasks.RetryQueueTask>();
    }

    // Small private implementation of the seam — deliberately not exposed outside this file
    // since production code only ever needs to construct it once, here, with both concrete
    // clients already resolved. Tests use their own Moq-based IArrClientFactory stub instead.
    private sealed class ArrClientFactory : IArrClientFactory
    {
        private readonly IArrClient _radarrClient;
        private readonly IArrClient _sonarrClient;

        public ArrClientFactory(IArrClient radarrClient, IArrClient sonarrClient)
        {
            _radarrClient = radarrClient;
            _sonarrClient = sonarrClient;
        }

        public IArrClient GetClient(bool isSeries) => isSeries ? _sonarrClient : _radarrClient;
    }
}
