using Jellyfin.Plugin.ArrDeleteSync.Models;

namespace Jellyfin.Plugin.ArrDeleteSync.Services;

public interface ICircuitBreaker
{
    bool IsTripped { get; }
    void RecordFailure();
    void RecordSuccess();
    void Reset();
    CircuitBreakerState GetState();
    void LoadState(CircuitBreakerState state);
}
