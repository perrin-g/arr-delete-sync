using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

public class CircuitBreakerTests
{
    [Fact]
    public void NotTripped_Initially()
    {
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void Trips_AfterThresholdConsecutiveFailures()
    {
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        for (int i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }
        Assert.False(breaker.IsTripped);

        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public void Success_ResetsConsecutiveCount_ButRollingWindowStillAccumulates()
    {
        // The two trip conditions are deliberately independent: a success resets the
        // consecutive-failure streak, but does NOT erase failures already counted toward the
        // rolling window — only elapsed time (pruning) does that. Otherwise a flaky pattern
        // (fail, succeed, fail, succeed, fail...) could never trip via the window condition,
        // which defeats the reason that condition exists.
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();
        // 4 total failures within the window, only 2 consecutive since the success — neither
        // condition has reached the threshold of 5 yet.
        Assert.False(breaker.IsTripped);

        breaker.RecordFailure();
        // 5th failure within the window trips it via the window condition, even though only 3
        // are consecutive since the last success.
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public void ConsecutiveFailures_CanTripIndependently_WhenEarlierFailuresHaveAgedOutOfWindow()
    {
        // The reverse divergence: a long-running consecutive streak whose earliest failures
        // are now outside the rolling window should still trip via the consecutive condition,
        // even though the window count alone wouldn't reach the threshold. Constructed via
        // LoadState (a legitimate public entry point) with pre-aged timestamps rather than a
        // clock abstraction, which this task doesn't otherwise need.
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        var old = DateTime.UtcNow.AddMinutes(-20);
        breaker.LoadState(new CircuitBreakerState
        {
            IsTripped = false,
            ConsecutiveFailures = 4,
            RecentFailureTimestampsUtc = new List<DateTime> { old, old, old, old }
        });

        breaker.RecordFailure();

        // The four aged entries fall outside the 15-minute window and get pruned, leaving only
        // this new failure in the window (count 1) — but the consecutive count reaches 5 and
        // trips independently of the window condition.
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public void Reset_ClearsTrippedState()
    {
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        Assert.True(breaker.IsTripped);

        breaker.Reset();
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void State_RoundTrips_ForPersistenceAcrossRestart()
    {
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        for (int i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }
        var state = breaker.GetState();

        var restored = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        restored.LoadState(state);

        Assert.True(restored.IsTripped);
    }
}
