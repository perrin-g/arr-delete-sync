using System;
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
    public void Success_ResetsConsecutiveFailureCount()
    {
        var breaker = new CircuitBreaker(threshold: 5, windowMinutes: 15);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.False(breaker.IsTripped);
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
