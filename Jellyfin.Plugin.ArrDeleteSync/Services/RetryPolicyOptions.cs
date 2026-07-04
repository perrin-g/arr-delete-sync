namespace Jellyfin.Plugin.ArrDeleteSync.Services;

// A bare int constructor parameter on RetryQueueTask isn't resolvable by Jellyfin's own DI
// activator (it independently tries to construct IScheduledTask implementations via reflection,
// separate from -- and in addition to -- ServiceRegistrator's own factory registration below;
// confirmed live: "Unable to resolve service for type 'System.Int32'" on startup). Wrapping the
// configured value in a real registered type lets both construction paths resolve it correctly.
public class RetryPolicyOptions
{
    public required int MaxAttempts { get; set; }
}
