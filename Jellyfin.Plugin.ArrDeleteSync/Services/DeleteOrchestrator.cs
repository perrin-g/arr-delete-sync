using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public partial class DeleteOrchestrator : IDeleteOrchestrator
{
    private readonly IJellyfinItemAccessor _itemAccessor;
    private readonly IArrClientFactory _arrClientFactory;
    private readonly ISeerrClient _seerrClient;
    private readonly IRetryQueueStore _retryQueueStore;
    private readonly IAuditLogStore _auditLogStore;
    private readonly ICircuitBreaker _circuitBreaker;

    public DeleteOrchestrator(
        IJellyfinItemAccessor itemAccessor,
        IArrClientFactory arrClientFactory,
        ISeerrClient seerrClient,
        IRetryQueueStore retryQueueStore,
        IAuditLogStore auditLogStore,
        ICircuitBreaker circuitBreaker)
    {
        _itemAccessor = itemAccessor;
        _arrClientFactory = arrClientFactory;
        _seerrClient = seerrClient;
        _retryQueueStore = retryQueueStore;
        _auditLogStore = auditLogStore;
        _circuitBreaker = circuitBreaker;
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

        var arrResult = await _arrClientFactory.GetClient(isSeries).FindByProviderIdAsync(providerIdType, providerIdValue, isSeries);

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

        if (arrResult.State != ArrTrackingState.Indeterminate && int.TryParse(tmdbId, out var tmdbInt))
        {
            var seerrResult = await _seerrClient.FindByTmdbIdAsync(tmdbInt, isTv: isSeries);
            if (seerrResult.State == ArrTrackingState.Tracked)
            {
                result.SeerrMediaId = seerrResult.MediaId;
            }
        }

        return result;
    }

    private async Task<ResolutionResult> ResolveWithSeerrFallbackAsync(JellyfinItemInfo item, string tvdbId)
    {
        var arrResult = await _arrClientFactory.GetClient(true).FindByProviderIdAsync("tvdbId", tvdbId, isSeries: true);

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

    public async Task<DeleteOutcome> ExecuteDeleteAsync(DeleteRequest request)
    {
        if (_circuitBreaker.IsTripped)
        {
            await LogAsync(request.JellyfinItemId, "unknown", request.Granularity, "Blocked", "Failed", "Circuit breaker is tripped", false, null);
            return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, BlockedReason = "Circuit breaker is tripped — repeated failures detected. An admin must manually reset it." };
        }

        var resolution = await ResolveAsync(request.JellyfinItemId, request.Granularity);
        var itemInfo = _itemAccessor.GetItem(request.JellyfinItemId);
        var itemName = itemInfo?.Name ?? "(deleted)";

        if (!resolution.HasUsableProviderId && !request.Force)
        {
            await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "Blocked", "Failed", "No usable provider ID; force flag required", false, null);
            return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, BlockedReason = "This item isn't identified (no usable provider ID). Use force-delete to remove it — arr won't be touched, and the file will remain on disk (see the untracked-content limitation)." };
        }

        if (request.Granularity == DeleteGranularity.Season && itemInfo != null && !itemInfo.HasPhysicalPath)
        {
            await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "Blocked", "Failed", "Virtual season, no physical layout", false, null);
            return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, BlockedReason = "Season-level delete isn't available for this show's layout (no physical per-season folder)." };
        }

        if (request.Granularity == DeleteGranularity.Episode)
        {
            if (resolution.State != ArrTrackingState.Tracked)
            {
                await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "Blocked", "Failed", "Episode-level delete requires arr tracking to verify file boundary", false, null);
                return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, BlockedReason = "Episode-level delete requires arr tracking to verify file boundary; use series/season-level or force-delete for untracked content." };
            }

            if (resolution.ArrInternalId.HasValue && itemInfo?.SeasonNumber != null && itemInfo.EpisodeNumber != null)
            {
                var coverageCount = await _arrClientFactory.GetClient(true).GetEpisodeFileCoverageCountAsync(resolution.ArrInternalId.Value, itemInfo.SeasonNumber.Value, itemInfo.EpisodeNumber.Value);
                if (coverageCount <= 0)
                {
                    await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "Blocked", "Failed", "Could not verify episode file layout", false, null);
                    return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, BlockedReason = "Could not verify this episode's file layout (arr lookup failed) — blocking conservatively. Try again or verify manually before force-deleting." };
                }

                if (coverageCount > 1)
                {
                    await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "Blocked", "Failed", $"File covers {coverageCount} episodes", false, null);
                    return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, BlockedReason = $"This file also contains {coverageCount - 1} other episode(s) — not supported at single-episode granularity." };
                }
            }
        }

        var isUntracked = resolution.State == ArrTrackingState.ConfirmedNotTracked || (!resolution.HasUsableProviderId && request.Force);

        if (resolution.State == ArrTrackingState.Indeterminate)
        {
            var pendingEntry = new RetryQueueEntry
            {
                Id = Guid.NewGuid(),
                JellyfinItemId = request.JellyfinItemId,
                ItemDisplayName = itemName,
                Granularity = request.Granularity,
                ProviderIdType = resolution.ProviderIdType,
                ProviderIdValue = resolution.ProviderIdValue,
                ArrDeleteStatus = DeleteStepStatus.Pending,
                JellyfinCleanupStatus = DeleteStepStatus.Pending,
                SeerrUpdateStatus = DeleteStepStatus.Pending,
                NextRetryAtUtc = DateTime.UtcNow.AddMinutes(5)
            };
            await _retryQueueStore.UpsertAsync(pendingEntry);
            _circuitBreaker.RecordFailure();
            await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "SyncedDelete", "Partial", "Could not verify arr status; queued for retry", false, null);
            return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, QueuedForRetry = true };
        }

        // Tracked content: *arr owns the actual file + record deletion (deleteFiles=true) — the
        // only component in this stack with write access to media. This must happen before any
        // Jellyfin action, so a failure here leaves nothing else touched.
        bool arrDeleted = false; // untracked content: nothing to delete via arr, so vacuously not "deleted by arr"
        if (!isUntracked && resolution.State == ArrTrackingState.Tracked && resolution.ArrInternalId.HasValue)
        {
            var isSeries = request.Granularity is DeleteGranularity.Series or DeleteGranularity.Season or DeleteGranularity.Episode;
            arrDeleted = await _arrClientFactory.GetClient(isSeries).DeleteAsync(resolution.ArrInternalId.Value, isSeries);

            if (!arrDeleted)
            {
                var entry = new RetryQueueEntry
                {
                    Id = Guid.NewGuid(),
                    JellyfinItemId = request.JellyfinItemId,
                    ItemDisplayName = itemName,
                    Granularity = request.Granularity,
                    ProviderIdType = resolution.ProviderIdType,
                    ProviderIdValue = resolution.ProviderIdValue,
                    ArrDeleteStatus = DeleteStepStatus.Failed,
                    NextRetryAtUtc = DateTime.UtcNow.AddMinutes(5)
                };
                await _retryQueueStore.UpsertAsync(entry);
                _circuitBreaker.RecordFailure();
                await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "SyncedDelete", "Partial", "arr delete call failed", false, null);
                return new DeleteOutcome { ArrDeleted = false, JellyfinCleanedUp = false, SeerrUpdated = false, QueuedForRetry = true };
            }
        }

        // Only record a breaker success when an arr call was actually made (same gating
        // condition as the arr-delete block above) — untracked/force-deleted content never calls
        // arr at all, so recording a "success" for it would spuriously reset the
        // consecutive-failure counter and could mask a genuinely broken arr/Seerr integration
        // during a batch of force-deletes.
        if (!isUntracked && resolution.State == ArrTrackingState.Tracked && resolution.ArrInternalId.HasValue)
        {
            _circuitBreaker.RecordSuccess();
        }

        // Jellyfin catalog cleanup — metadata-only (DeleteFileLocation=false inside the accessor),
        // runs for both tracked (after arr already removed the file) and untracked content.
        var cleanedUp = _itemAccessor.DeleteItem(request.JellyfinItemId, out var isStructural, out var cleanupError);

        var requiresManualFileCleanup = isUntracked;
        var filePath = requiresManualFileCleanup ? itemInfo?.Path : null;

        if (!cleanedUp)
        {
            var entry = new RetryQueueEntry
            {
                Id = Guid.NewGuid(),
                JellyfinItemId = request.JellyfinItemId,
                ItemDisplayName = itemName,
                Granularity = request.Granularity,
                ProviderIdType = resolution.ProviderIdType,
                ProviderIdValue = resolution.ProviderIdValue,
                ArrDeleteStatus = DeleteStepStatus.Succeeded,
                JellyfinCleanupStatus = DeleteStepStatus.Failed,
                IsStructuralFailure = isStructural,
                LastError = SecretScrubber.Scrub(cleanupError, GetKnownSecrets()),
                NextRetryAtUtc = isStructural ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(5),
                RequiresManualFileCleanup = requiresManualFileCleanup,
                FilePath = filePath
            };
            await _retryQueueStore.UpsertAsync(entry);
            _circuitBreaker.RecordFailure();
            await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "SyncedDelete", isStructural ? "Failed" : "Partial", entry.LastError, requiresManualFileCleanup, filePath);
            return new DeleteOutcome { ArrDeleted = arrDeleted, JellyfinCleanedUp = false, SeerrUpdated = false, QueuedForRetry = !isStructural, RequiresManualFileCleanup = requiresManualFileCleanup, FilePath = filePath };
        }

        bool seerrUpdated = true;
        if (resolution.SeerrMediaId.HasValue)
        {
            seerrUpdated = await _seerrClient.UpdateAvailabilityAsync(resolution.SeerrMediaId.Value);
        }

        if (!seerrUpdated)
        {
            var entry = new RetryQueueEntry
            {
                Id = Guid.NewGuid(),
                JellyfinItemId = request.JellyfinItemId,
                ItemDisplayName = itemName,
                Granularity = request.Granularity,
                ProviderIdType = resolution.ProviderIdType,
                ProviderIdValue = resolution.ProviderIdValue,
                ArrDeleteStatus = DeleteStepStatus.Succeeded,
                JellyfinCleanupStatus = DeleteStepStatus.Succeeded,
                SeerrUpdateStatus = DeleteStepStatus.Failed,
                NextRetryAtUtc = DateTime.UtcNow.AddMinutes(5)
            };
            await _retryQueueStore.UpsertAsync(entry);
            _circuitBreaker.RecordFailure();
            await LogAsync(request.JellyfinItemId, itemName, request.Granularity, "SyncedDelete", "Partial", "Seerr update failed", requiresManualFileCleanup, filePath);
            return new DeleteOutcome { ArrDeleted = arrDeleted, JellyfinCleanedUp = true, SeerrUpdated = false, QueuedForRetry = true, RequiresManualFileCleanup = requiresManualFileCleanup, FilePath = filePath };
        }

        await LogAsync(request.JellyfinItemId, itemName, request.Granularity, request.Force ? "Forced" : (isUntracked ? "JellyfinCatalogOnly" : "SyncedDelete"), "Success", null, requiresManualFileCleanup, filePath);
        return new DeleteOutcome { ArrDeleted = arrDeleted, JellyfinCleanedUp = true, SeerrUpdated = seerrUpdated, RequiresManualFileCleanup = requiresManualFileCleanup, FilePath = filePath };
    }

    // Ordering (arr → Jellyfin cleanup → Seerr) is preserved on retry, not just on first attempt:
    // each step only runs once the step before it is confirmed succeeded, exactly like
    // ExecuteDeleteAsync itself.
    public async Task<bool> ProcessRetryEntryAsync(RetryQueueEntry entry)
    {
        if (entry.ArrDeleteStatus == DeleteStepStatus.Pending)
        {
            // Nothing was attempted at all (this entry came from an indeterminate result before
            // confirmation completed) — the Jellyfin item is untouched, safe to re-run everything.
            // ExecuteDeleteAsync records its own circuit-breaker outcomes internally, so this
            // branch doesn't need to (unlike the general branch below, which calls arr/Jellyfin/
            // Seerr directly and must record for itself — see the end of this method).
            var outcome = await ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = entry.JellyfinItemId, Granularity = entry.Granularity });
            // ArrDeleted is correctly false for untracked/force-delete content (Task 8's
            // RequiresManualFileCleanup flag identifies this) — the arr step was never applicable
            // there, not a failure, so it shouldn't gate whether this retry counts as resolved.
            var arrStepOk = outcome.ArrDeleted || outcome.RequiresManualFileCleanup;
            var resolved = arrStepOk && outcome.JellyfinCleanedUp && outcome.SeerrUpdated;

            if (!resolved)
            {
                // ExecuteDeleteAsync already wrote its OWN fresh, granular RetryQueueEntry (new Id,
                // accurate per-step status) if anything failed — RetryQueueStore dedupes by
                // JellyfinItemId, so that write already landed correctly. But the caller
                // (RetryQueueTask) still holds this ORIGINAL entry object, still showing every
                // step as "Pending", and will re-upsert IT with a bumped attempt count on our
                // return — which would clobber the more accurate entry ExecuteDeleteAsync just
                // wrote. Pull the fresh state back onto this entry object so that re-upsert
                // preserves it instead of regressing it back to "everything Pending" (which would
                // otherwise cause every subsequent retry to redo the whole flow from scratch, and
                // — worse — cause an already-correctly-deleted item to be misclassified as
                // "untracked, needs manual cleanup" on the next full re-resolve).
                var freshEntry = await _retryQueueStore.FindByItemIdAsync(entry.JellyfinItemId);
                if (freshEntry != null)
                {
                    entry.ItemDisplayName = freshEntry.ItemDisplayName;
                    entry.ArrDeleteStatus = freshEntry.ArrDeleteStatus;
                    entry.JellyfinCleanupStatus = freshEntry.JellyfinCleanupStatus;
                    entry.SeerrUpdateStatus = freshEntry.SeerrUpdateStatus;
                    entry.ProviderIdType = freshEntry.ProviderIdType;
                    entry.ProviderIdValue = freshEntry.ProviderIdValue;
                    entry.IsStructuralFailure = freshEntry.IsStructuralFailure;
                    entry.RequiresManualFileCleanup = freshEntry.RequiresManualFileCleanup;
                    entry.FilePath = freshEntry.FilePath;
                }
            }

            return resolved;
        }

        var isSeries = entry.Granularity is DeleteGranularity.Series or DeleteGranularity.Season or DeleteGranularity.Episode;

        var arrOk = entry.ArrDeleteStatus == DeleteStepStatus.Succeeded;
        if (!arrOk)
        {
            if (!string.IsNullOrEmpty(entry.ProviderIdType) && !string.IsNullOrEmpty(entry.ProviderIdValue))
            {
                // arr's delete call failed previously — re-resolve using the snapshotted provider
                // ID (never the Jellyfin item; this plugin never touches it until arr succeeds).
                var lookup = await _arrClientFactory.GetClient(isSeries).FindByProviderIdAsync(entry.ProviderIdType, entry.ProviderIdValue, isSeries);
                if (lookup.State == ArrTrackingState.ConfirmedNotTracked)
                {
                    arrOk = true; // already gone — counts as success
                }
                else if (lookup.State == ArrTrackingState.Tracked && lookup.InternalId.HasValue)
                {
                    arrOk = await _arrClientFactory.GetClient(isSeries).DeleteAsync(lookup.InternalId.Value, isSeries);
                }
            }
            else
            {
                // No provider ID at all — an untracked/force-delete entry; arr never had
                // anything to do here, so this step is vacuously fine.
                arrOk = true;
            }
        }

        var jellyfinOk = entry.JellyfinCleanupStatus == DeleteStepStatus.Succeeded;
        if (arrOk && !jellyfinOk)
        {
            jellyfinOk = _itemAccessor.DeleteItem(entry.JellyfinItemId, out _, out _);
        }

        var seerrOk = entry.SeerrUpdateStatus == DeleteStepStatus.Succeeded;
        if (arrOk && jellyfinOk && !seerrOk)
        {
            if (entry.ProviderIdType == "tmdbId" && int.TryParse(entry.ProviderIdValue, out var tmdbInt))
            {
                var seerrLookup = await _seerrClient.FindByTmdbIdAsync(tmdbInt, isSeries);
                if (seerrLookup.State == ArrTrackingState.ConfirmedNotTracked)
                {
                    seerrOk = true;
                }
                else if (seerrLookup.State == ArrTrackingState.Tracked && seerrLookup.MediaId.HasValue)
                {
                    seerrOk = await _seerrClient.UpdateAvailabilityAsync(seerrLookup.MediaId.Value);
                }
            }
            else
            {
                // TVDB-only fallback case (already best-effort skip-logged) or no Seerr record
                // at all — nothing further to retry.
                seerrOk = true;
            }
        }

        entry.ArrDeleteStatus = arrOk ? DeleteStepStatus.Succeeded : DeleteStepStatus.Failed;
        entry.JellyfinCleanupStatus = jellyfinOk ? DeleteStepStatus.Succeeded : DeleteStepStatus.Failed;
        entry.SeerrUpdateStatus = seerrOk ? DeleteStepStatus.Succeeded : DeleteStepStatus.Failed;

        var fullyResolved = arrOk && jellyfinOk && seerrOk;

        // This branch calls arr/Jellyfin/Seerr directly (unlike the Pending branch, which
        // delegates to ExecuteDeleteAsync and gets breaker recording for free) — without this,
        // a dependency that only ever fails during retries (never during a first attempt) could
        // never trip the breaker at all.
        if (fullyResolved)
        {
            _circuitBreaker.RecordSuccess();
        }
        else
        {
            _circuitBreaker.RecordFailure();
        }

        return fullyResolved;
    }

    private async Task LogAsync(Guid itemId, string itemName, DeleteGranularity granularity, string action, string outcome, string? error, bool requiresManualFileCleanup, string? filePath)
    {
        await _auditLogStore.AppendAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTime.UtcNow,
            JellyfinItemId = itemId,
            ItemDisplayName = itemName,
            Granularity = granularity,
            Action = action,
            Outcome = outcome,
            ErrorDetail = error,
            RequiresManualFileCleanup = requiresManualFileCleanup,
            FilePath = filePath
        });
    }

    // Task 5/6's ArrClient/SeerrClient never surface raw exception text up to the orchestrator
    // today (they collapse failures to an enum/bool), and API keys are header-only, never
    // embedded in a URL a caught exception could echo back (verified by Task 5's
    // ApiKey_IsSentAsHeader_NeverAsQueryString test) — so this is defense-in-depth for a future
    // change to ArrClient/SeerrClient that surfaces raw HTTP exception text here. Resolved via
    // the static Plugin.Instance singleton (the same mechanism ServiceRegistrator already uses
    // to reach configuration/KeyProtector) rather than adding constructor parameters, since
    // DeleteOrchestrator has no other need for plugin configuration. Plugin.Instance is null in
    // unit tests (the real Plugin/BasePlugin is never constructed there), which this tolerates.
    private static string[] GetKnownSecrets()
    {
        var plugin = Plugin.Instance;
        if (plugin?.Configuration == null)
        {
            return Array.Empty<string>();
        }

        var config = plugin.Configuration;
        var keys = new[]
        {
            plugin.KeyProtector.Unprotect(config.RadarrApiKeyEncrypted),
            plugin.KeyProtector.Unprotect(config.SonarrApiKeyEncrypted),
            plugin.KeyProtector.Unprotect(config.SeerrApiKeyEncrypted)
        };

        return Array.FindAll(keys, key => !string.IsNullOrEmpty(key));
    }
}
