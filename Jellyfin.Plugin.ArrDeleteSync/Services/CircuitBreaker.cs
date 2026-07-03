using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public class CircuitBreaker : ICircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly object _lock = new();
    private CircuitBreakerState _state = new();

    public CircuitBreaker(int threshold, int windowMinutes)
    {
        _threshold = threshold;
        _window = TimeSpan.FromMinutes(windowMinutes);
    }

    public bool IsTripped
    {
        get { lock (_lock) { return _state.IsTripped; } }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _state.ConsecutiveFailures++;
            _state.RecentFailureTimestampsUtc.Add(now);
            _state.RecentFailureTimestampsUtc.RemoveAll(t => now - t > _window);

            if (_state.ConsecutiveFailures >= _threshold ||
                _state.RecentFailureTimestampsUtc.Count >= _threshold)
            {
                _state.IsTripped = true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _state.ConsecutiveFailures = 0;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = new CircuitBreakerState();
        }
    }

    public CircuitBreakerState GetState()
    {
        lock (_lock)
        {
            return new CircuitBreakerState
            {
                IsTripped = _state.IsTripped,
                ConsecutiveFailures = _state.ConsecutiveFailures,
                RecentFailureTimestampsUtc = _state.RecentFailureTimestampsUtc.ToList()
            };
        }
    }

    public void LoadState(CircuitBreakerState state)
    {
        lock (_lock)
        {
            _state = new CircuitBreakerState
            {
                IsTripped = state.IsTripped,
                ConsecutiveFailures = state.ConsecutiveFailures,
                RecentFailureTimestampsUtc = state.RecentFailureTimestampsUtc?.ToList() ?? new List<DateTime>()
            };
        }
    }
}
