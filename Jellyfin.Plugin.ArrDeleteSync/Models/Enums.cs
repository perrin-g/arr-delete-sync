namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public enum ArrTrackingState
{
    Tracked,
    ConfirmedNotTracked,
    Indeterminate
}

public enum DeleteGranularity
{
    Movie,
    Series,
    Season,
    Episode,
    // Whole series selected but "remove whole series" left unticked: delete every episode file
    // (specials included) and unmonitor them all, keep the series monitored for new seasons,
    // never touch Seerr. Appended last so stored numeric values of the others don't shift.
    AllEpisodesKeepSeries
}

public enum DeleteStepStatus
{
    Pending,
    Succeeded,
    Failed
}
