using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrDeleteSync.Models;

public class CircuitBreakerState
{
    public bool IsTripped { get; set; }
    public int ConsecutiveFailures { get; set; }
    public List<DateTime> RecentFailureTimestampsUtc { get; set; } = new();
}
