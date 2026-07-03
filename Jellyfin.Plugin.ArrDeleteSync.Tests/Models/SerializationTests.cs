using System;
using System.Text.Json;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Models;

public class SerializationTests
{
    [Fact]
    public void RetryQueueEntry_RoundTrips_ThroughJson()
    {
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(),
            JellyfinItemId = Guid.NewGuid(),
            Granularity = DeleteGranularity.Movie,
            ProviderIdType = "Tmdb",
            ProviderIdValue = "603",
            ArrDeleteStatus = DeleteStepStatus.Failed,
            JellyfinCleanupStatus = DeleteStepStatus.Pending,
            SeerrUpdateStatus = DeleteStepStatus.Pending,
            AttemptCount = 2,
            LastError = "timeout",
            NextRetryAtUtc = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
            IsStructuralFailure = false
        };

        var json = JsonSerializer.Serialize(entry);
        var roundTripped = JsonSerializer.Deserialize<RetryQueueEntry>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(entry.Id, roundTripped!.Id);
        Assert.Equal(entry.Granularity, roundTripped.Granularity);
        Assert.Equal(entry.ArrDeleteStatus, roundTripped.ArrDeleteStatus);
        Assert.Equal(entry.NextRetryAtUtc, roundTripped.NextRetryAtUtc);
    }

    [Fact]
    public void AuditLogEntry_RoundTrips_ThroughJson()
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc),
            JellyfinItemId = Guid.NewGuid(),
            ItemDisplayName = "12 Angry Men",
            Granularity = DeleteGranularity.Movie,
            Action = "SyncedDelete",
            Outcome = "Success"
        };

        var json = JsonSerializer.Serialize(entry);
        var roundTripped = JsonSerializer.Deserialize<AuditLogEntry>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(entry.ItemDisplayName, roundTripped!.ItemDisplayName);
        Assert.Equal(entry.Action, roundTripped.Action);
    }
}
