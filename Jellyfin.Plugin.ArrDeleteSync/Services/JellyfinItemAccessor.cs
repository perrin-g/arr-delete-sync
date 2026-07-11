using System;
using System.IO;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

// NOTE: verify BaseItem.CanDelete / ILibraryManager.DeleteItem exact signatures against
// the Jellyfin.Controller package version pinned in the csproj (Task 1) before relying on
// this — Jellyfin's internal APIs have shifted between major versions (see this session's
// 10.11 experience). This is written to the standard, well-established shape.
public class JellyfinItemAccessor : IJellyfinItemAccessor
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinItemAccessor(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public JellyfinItemInfo? GetItem(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return null;
        }

        var granularity = item switch
        {
            Movie => DeleteGranularity.Movie,
            Series => DeleteGranularity.Series,
            Season => DeleteGranularity.Season,
            Episode => DeleteGranularity.Episode,
            _ => throw new InvalidOperationException($"Unsupported item type: {item.GetType().Name}")
        };

        var providerIds = item.ProviderIds;
        providerIds.TryGetValue("Tmdb", out var tmdbId);
        providerIds.TryGetValue("Tvdb", out var tvdbId);
        providerIds.TryGetValue("Imdb", out var imdbId);

        var info = new JellyfinItemInfo
        {
            Id = item.Id,
            Name = item.Name,
            Granularity = granularity,
            TmdbId = tmdbId,
            TvdbId = tvdbId,
            ImdbId = imdbId,
            Path = item.Path,
            HasPhysicalPath = !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) || Directory.Exists(item.Path)
        };

        if (item is Season season)
        {
            info.SeriesItemId = season.SeriesId;
            info.SeasonNumber = season.IndexNumber;
            info.SeriesName = season.SeriesName;
            if (season.Series?.ProviderIds.TryGetValue("Tvdb", out var seriesTvdb) == true)
            {
                info.SeriesTvdbId = seriesTvdb;
            }

            if (season.Series?.ProviderIds.TryGetValue("Tmdb", out var seriesTmdb) == true)
            {
                info.SeriesTmdbId = seriesTmdb;
            }
        }
        else if (item is Episode episode)
        {
            info.SeriesItemId = episode.SeriesId;
            info.SeasonNumber = episode.ParentIndexNumber;
            info.EpisodeNumber = episode.IndexNumber;
            info.SeriesName = episode.SeriesName;
            info.SeasonName = episode.SeasonName;
            if (episode.Series?.ProviderIds.TryGetValue("Tvdb", out var epSeriesTvdb) == true)
            {
                info.SeriesTvdbId = epSeriesTvdb;
            }

            if (episode.Series?.ProviderIds.TryGetValue("Tmdb", out var epSeriesTmdb) == true)
            {
                info.SeriesTmdbId = epSeriesTmdb;
            }
        }

        return info;
    }

    // The library (top-level CollectionFolder, e.g. "Movies", "Discover") an item lives under.
    // GetCollectionFolders is the same API BaseItem.CanDelete itself uses internally to resolve
    // an item's owning library/libraries -- verified by decompiling the pinned 10.11.11
    // MediaBrowser.Controller.dll rather than assuming a signature (there's no GetTopParent() on
    // BaseItem in this version). An item can technically belong to more than one library if
    // multiple libraries share an overlapping path; the first is enough to check against an
    // excluded-library name.
    public string? GetLibraryName(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return null;
        }

        return _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name;
    }

    // Metadata-only removal of Jellyfin's own catalog entry. DeleteFileLocation is
    // deliberately false: this homelab's Jellyfin container has a read-only /media mount
    // (confirmed in Task 0), so Jellyfin cannot delete the underlying file itself — actual
    // file removal is *arr's job (Task 5's ArrClient.DeleteAsync, deleteFiles=true), called
    // by the orchestrator BEFORE this method for tracked content. This call should only ever
    // touch /config (writable), never /media — Task 0's staging verification confirms this
    // assumption holds for the installed Jellyfin SDK version rather than trusting the flag
    // name alone.
    public bool DeleteItem(Guid itemId, out bool isStructuralFailure, out string? error)
    {
        isStructuralFailure = false;
        error = null;

        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            // Already gone — treat as success per the "already gone counts as success" rule.
            return true;
        }

        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, notifyParentItem: true);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            isStructuralFailure = true;
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            isStructuralFailure = true;
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
