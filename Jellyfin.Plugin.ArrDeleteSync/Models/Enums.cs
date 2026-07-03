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
    Episode
}

public enum DeleteStepStatus
{
    Pending,
    Succeeded,
    Failed
}
