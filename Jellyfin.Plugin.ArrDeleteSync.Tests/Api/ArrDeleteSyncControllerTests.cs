using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Api;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Api;

public class ArrDeleteSyncControllerTests
{
    private static ArrDeleteSyncController MakeController(
        out Mock<IDeleteOrchestrator> orchestrator,
        out Mock<IRetryQueueStore> queue,
        out Mock<IAuditLogStore> audit,
        out Mock<ICircuitBreaker> breaker)
    {
        orchestrator = new Mock<IDeleteOrchestrator>();
        queue = new Mock<IRetryQueueStore>();
        audit = new Mock<IAuditLogStore>();
        breaker = new Mock<ICircuitBreaker>();
        return new ArrDeleteSyncController(orchestrator.Object, queue.Object, audit.Object, breaker.Object);
    }

    [Theory]
    [InlineData(nameof(ArrDeleteSyncController.Resolve))]
    [InlineData(nameof(ArrDeleteSyncController.Delete))]
    [InlineData(nameof(ArrDeleteSyncController.GetRetryQueue))]
    [InlineData(nameof(ArrDeleteSyncController.RetryEntry))]
    [InlineData(nameof(ArrDeleteSyncController.DismissEntry))]
    [InlineData(nameof(ArrDeleteSyncController.GetAuditLog))]
    [InlineData(nameof(ArrDeleteSyncController.ResetCircuitBreaker))]
    public void EveryAction_RequiresAdminAuthorization(string methodName)
    {
        var method = typeof(ArrDeleteSyncController).GetMethod(methodName);
        Assert.NotNull(method);

        var hasClassLevelAuth = typeof(ArrDeleteSyncController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Any(a => a.Policy == "RequiresElevation");
        var hasMethodLevelAuth = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Any(a => a.Policy == "RequiresElevation");

        Assert.True(hasClassLevelAuth || hasMethodLevelAuth, $"{methodName} is not admin-gated");
    }

    [Fact]
    public async Task Delete_ReturnsOkWithOutcome_OnSuccess()
    {
        var controller = MakeController(out var orchestrator, out _, out _, out _);
        orchestrator.Setup(o => o.ExecuteDeleteAsync(It.IsAny<DeleteRequest>()))
            .ReturnsAsync(new DeleteOutcome { ArrDeleted = true, JellyfinCleanedUp = true, SeerrUpdated = true });

        var result = await controller.Delete(new DeleteRequest { JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var outcome = Assert.IsType<DeleteOutcome>(okResult.Value);
        Assert.True(outcome.ArrDeleted);
    }

    [Fact]
    public async Task DismissEntry_RemovesFromQueue_AndAuditLogs()
    {
        var entryId = Guid.NewGuid();
        var controller = MakeController(out _, out var queue, out var audit, out _);
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new[] { new RetryQueueEntry
        {
            Id = entryId, JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie,
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Failed,
            NextRetryAtUtc = DateTime.UtcNow
        }});

        var result = await controller.DismissEntry(entryId);

        // Must be a real JSON body, not a bare 200 -- the Delete Manager UI's fetchJson() forces
        // dataType: "json" on every call, and jQuery throws a client-side "unexpected end of
        // data" parse error trying to JSON.parse an empty response body, even though the dismiss
        // itself succeeded server-side. Reproduced live: clicking "Give up" threw exactly that
        // error and the UI never refreshed to reflect the removal.
        Assert.IsType<OkObjectResult>(result);
        queue.Verify(q => q.RemoveAsync(entryId), Times.Once);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "Dismissed")), Times.Once);
    }

    [Fact]
    public async Task RetryEntry_AppliesExponentialBackoff_OnFailure()
    {
        var entryId = Guid.NewGuid();
        var controller = MakeController(out var orchestrator, out var queue, out _, out _);
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new[] { new RetryQueueEntry
        {
            Id = entryId, JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie,
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Failed,
            AttemptCount = 0,
            NextRetryAtUtc = DateTime.UtcNow
        }});
        orchestrator.Setup(o => o.ProcessRetryEntryAsync(It.IsAny<RetryQueueEntry>())).ReturnsAsync(false);

        var beforeCall = DateTime.UtcNow;
        var result = await controller.RetryEntry(entryId);

        Assert.IsType<OkObjectResult>(result);
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e =>
            e.Id == entryId &&
            e.AttemptCount == 1 &&
            e.NextRetryAtUtc > beforeCall)), Times.Once);
        queue.Verify(q => q.RemoveAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RetryEntry_FlagsMaxAttemptsExceeded_WhenFallbackLimitReached()
    {
        // Plugin.Instance is null in this test host, so the controller falls back to its
        // documented default of 5 -- AttemptCount=4 means this failure is the 5th.
        var entryId = Guid.NewGuid();
        var controller = MakeController(out var orchestrator, out var queue, out _, out _);
        queue.Setup(q => q.GetAllAsync()).ReturnsAsync(new[] { new RetryQueueEntry
        {
            Id = entryId, JellyfinItemId = Guid.NewGuid(), Granularity = DeleteGranularity.Movie,
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Failed,
            AttemptCount = 4,
            NextRetryAtUtc = DateTime.UtcNow
        }});
        orchestrator.Setup(o => o.ProcessRetryEntryAsync(It.IsAny<RetryQueueEntry>())).ReturnsAsync(false);

        var result = await controller.RetryEntry(entryId);

        Assert.IsType<OkObjectResult>(result);
        queue.Verify(q => q.UpsertAsync(It.Is<RetryQueueEntry>(e =>
            e.Id == entryId &&
            e.AttemptCount == 5 &&
            e.MaxAttemptsExceeded &&
            e.NextRetryAtUtc == DateTime.MaxValue)), Times.Once);
    }

    [Fact]
    public async Task ResetCircuitBreaker_CallsReset()
    {
        var controller = MakeController(out _, out _, out _, out var breaker);

        var result = await controller.ResetCircuitBreaker();

        Assert.IsType<OkResult>(result);
        breaker.Verify(b => b.Reset(), Times.Once);
    }
}
