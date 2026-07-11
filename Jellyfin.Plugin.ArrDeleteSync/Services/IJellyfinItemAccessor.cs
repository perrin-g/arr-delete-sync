using System;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface IJellyfinItemAccessor
{
    JellyfinItemInfo? GetItem(Guid itemId);
    bool DeleteItem(Guid itemId, out bool isStructuralFailure, out string? error);
    string? GetLibraryName(Guid itemId);
}
