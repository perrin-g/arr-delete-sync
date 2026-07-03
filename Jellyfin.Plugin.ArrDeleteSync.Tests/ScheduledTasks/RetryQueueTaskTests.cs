using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.ScheduledTasks;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.ScheduledTasks;

public class RetryQueueTaskTests
{
    private static RetryQueueEntry MakeDueEntry(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        JellyfinItemId = Guid.NewGuid(),
        Granularity = DeleteGranularity.Movie,
        ProviderIdType = "tmdbId",
        ProviderIdValue = "603",
        ArrDeleteStatus = DeleteStepStatus.Succeeded,
        JellyfinCleanupStatus = DeleteStepStatus.Failed,
        NextRetryAtUtc = DateTime.UtcNow.AddMinutes(-1)
    };

    [Fact]
    public async Task Execute_SkipsEntirely_WhenBreakerTripped()
    {
        var orchestrator = new Mock<IDeleteOrchestrator>();
        var queue = new Mock<IRetryQueueStore>();
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new List<RetryQueueEntry> { MakeDueEntry() });
        var breaker = new Mock<ICircuitBreaker>();
        breaker.Setup(b => b.IsTripped).Returns(true);

        var task = new RetryQueueTask(orchestrator.Object, queue.Object, breaker.Object);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        orchestrator.Verify(o => o.ProcessRetryEntryAsync(It.IsAny<RetryQueueEntry>()), Times.Never);
    }

    [Fact]
    public async Task Execute_RemovesEntry_WhenProcessingSucceeds()
    {
        var entry = MakeDueEntry();
        var orchestrator = new Mock<IDeleteOrchestrator>();
        orchestrator.Setup(o => o.ProcessRetryEntryAsync(entry)).ReturnsAsync(true);
        var queue = new Mock<IRetryQueueStore>();
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new List<RetryQueueEntry> { entry });
        var breaker = new Mock<ICircuitBreaker>();
        breaker.Setup(b => b.IsTripped).Returns(false);

        var task = new RetryQueueTask(orchestrator.Object, queue.Object, breaker.Object);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        queue.Verify(q => q.RemoveAsync(entry.Id), Times.Once);
    }

    [Fact]
    public async Task Execute_StopsProcessingRemainingEntries_WhenBreakerTripsMidBatch()
    {
        var entry1 = MakeDueEntry();
        var entry2 = MakeDueEntry();
        var orchestrator = new Mock<IDeleteOrchestrator>();
        var breaker = new Mock<ICircuitBreaker>();
        var tripAfterFirst = false;
        breaker.Setup(b => b.IsTripped).Returns(() => tripAfterFirst);
        orchestrator.Setup(o => o.ProcessRetryEntryAsync(entry1)).Callback(() => tripAfterFirst = true).ReturnsAsync(false);

        var queue = new Mock<IRetryQueueStore>();
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new List<RetryQueueEntry> { entry1, entry2 });

        var task = new RetryQueueTask(orchestrator.Object, queue.Object, breaker.Object);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        orchestrator.Verify(o => o.ProcessRetryEntryAsync(entry2), Times.Never);
    }

    [Fact]
    public async Task Execute_SkipsEntries_NotYetDueForRetry()
    {
        var futureEntry = MakeDueEntry();
        futureEntry.NextRetryAtUtc = DateTime.UtcNow.AddHours(1);
        var orchestrator = new Mock<IDeleteOrchestrator>();
        var queue = new Mock<IRetryQueueStore>();
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new List<RetryQueueEntry> { futureEntry });
        var breaker = new Mock<ICircuitBreaker>();
        breaker.Setup(b => b.IsTripped).Returns(false);

        var task = new RetryQueueTask(orchestrator.Object, queue.Object, breaker.Object);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        orchestrator.Verify(o => o.ProcessRetryEntryAsync(It.IsAny<RetryQueueEntry>()), Times.Never);
    }
}
