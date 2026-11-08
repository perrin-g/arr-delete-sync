using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ArrDeleteSync.Tests.Services;

// Seerr being down during a delete (seen live 2026-11-08: container stopped, Chef's Table S7
// deleted, logged "Success", Seerr never told). These use the real SeerrClient so the
// "can't reach Seerr" signal comes from the same code path production runs.
public class SeerrUnreachableTests
{
    private delegate void DeleteItemCallback(Guid id, out bool structural, out string? err);

    // A stopped container: the TCP connect is refused, HttpClient throws.
    private static SeerrClient StoppedSeerr() => new(
        new HttpClient(new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)))),
        "http://seerr:5055", "fakekey");

    // A running Seerr v3.5.1 that has the show as media 99 (tmdb 1408). The status route
    // needs a JSON body, as the real one does.
    private static SeerrClient RunningSeerr(List<string> calls) => new(
        new HttpClient(new FakeHttpMessageHandler(req =>
        {
            calls.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == "/api/v1/tv/1408")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1408,\"mediaInfo\":{\"id\":99,\"status\":4}}") };
            }

            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/api/v1/media/99/partial")
            {
                return req.Content?.Headers.ContentType?.MediaType == "application/json"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":99,\"status\":4}") }
                    : new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })),
        "http://seerr:5055", "fakekey");

    private static (DeleteOrchestrator, Mock<IRetryQueueStore>, Mock<IAuditLogStore>, Guid) SeasonDelete(ISeerrClient seerr)
    {
        var itemId = Guid.NewGuid();
        var accessor = new Mock<IJellyfinItemAccessor>();
        accessor.Setup(a => a.GetItem(itemId)).Returns(new JellyfinItemInfo
        {
            Id = itemId, Name = "Season 7", Granularity = DeleteGranularity.Season,
            TvdbId = "371572", SeriesTmdbId = "1408", SeasonNumber = 7, HasPhysicalPath = true
        });
        accessor.Setup(a => a.DeleteItem(itemId, out It.Ref<bool>.IsAny, out It.Ref<string?>.IsAny))
            .Callback(new DeleteItemCallback((Guid id, out bool structural, out string? err) => { structural = false; err = null; }))
            .Returns(true);
        var arr = new Mock<IArrClient>();
        arr.Setup(a => a.FindByProviderIdAsync("tvdbId", "371572", true)).ReturnsAsync(new ArrLookupResult { State = ArrTrackingState.Tracked, InternalId = 7 });
        arr.Setup(a => a.DeleteSeasonFilesAsync(7, 7)).ReturnsAsync(true);
        var factory = new Mock<IArrClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<bool>())).Returns(arr.Object);
        var queue = new Mock<IRetryQueueStore>();
        var audit = new Mock<IAuditLogStore>();
        return (new DeleteOrchestrator(accessor.Object, factory.Object, seerr, queue.Object, audit.Object, new Mock<ICircuitBreaker>().Object), queue, audit, itemId);
    }

    [Fact]
    public async Task SeasonDelete_WhileSeerrIsDown_IsPartial_AndQueuesARetryThatCanFindTheShowLater()
    {
        var (orchestrator, queue, audit, itemId) = SeasonDelete(StoppedSeerr());
        RetryQueueEntry? queued = null;
        queue.Setup(q => q.UpsertAsync(It.IsAny<RetryQueueEntry>())).Callback<RetryQueueEntry>(e => queued = e).Returns(Task.CompletedTask);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Season });

        Assert.True(outcome.ArrDeleted);
        Assert.True(outcome.JellyfinCleanedUp);
        Assert.False(outcome.SeerrUpdated);
        Assert.True(outcome.QueuedForRetry);
        Assert.NotNull(queued);
        Assert.Equal(DeleteStepStatus.Succeeded, queued!.ArrDeleteStatus);
        Assert.Equal(DeleteStepStatus.Succeeded, queued.JellyfinCleanupStatus);
        Assert.Equal(DeleteStepStatus.Failed, queued.SeerrUpdateStatus);
        Assert.Null(queued.SeerrMediaId);
        Assert.Equal(1408, queued.SeerrTmdbId);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Outcome == "Partial" && e.ErrorDetail == "Seerr unreachable")), Times.Once);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Outcome == "Success")), Times.Never);
    }

    [Fact]
    public async Task SeasonDelete_WithSeerrNotConfigured_IsStillSuccess()
    {
        var unconfigured = new SeerrClient(new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no base address"))), "", "");
        var (orchestrator, queue, audit, itemId) = SeasonDelete(unconfigured);

        var outcome = await orchestrator.ExecuteDeleteAsync(new DeleteRequest { JellyfinItemId = itemId, Granularity = DeleteGranularity.Season });

        Assert.False(outcome.QueuedForRetry);
        queue.Verify(q => q.UpsertAsync(It.IsAny<RetryQueueEntry>()), Times.Never);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Outcome == "Success")), Times.Once);
    }

    [Fact]
    public async Task Retry_AfterSeerrIsBack_LooksUpTheShowAndMarksItPartial()
    {
        var calls = new List<string>();
        var (orchestrator, _, audit, itemId) = SeasonDelete(RunningSeerr(calls));
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = itemId, ItemDisplayName = "Chef's Table - Season 7", Granularity = DeleteGranularity.Season,
            ProviderIdType = "tvdbId", ProviderIdValue = "371572", SeasonNumber = 7, SeerrMediaId = null, SeerrTmdbId = 1408,
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Succeeded,
            SeerrUpdateStatus = DeleteStepStatus.Failed, NextRetryAtUtc = DateTime.UtcNow
        };

        Assert.True(await orchestrator.ProcessRetryEntryAsync(entry));

        Assert.Equal(new[] { "GET /api/v1/tv/1408", "POST /api/v1/media/99/partial" }, calls);
        audit.Verify(a => a.AppendAsync(It.Is<AuditLogEntry>(e => e.Action == "Retry" && e.Outcome == "Success")), Times.Once);
    }

    [Fact]
    public async Task Retry_WhileSeerrIsStillDown_StaysQueued()
    {
        var (orchestrator, _, _, itemId) = SeasonDelete(StoppedSeerr());
        var entry = new RetryQueueEntry
        {
            Id = Guid.NewGuid(), JellyfinItemId = itemId, ItemDisplayName = "Chef's Table - Season 7", Granularity = DeleteGranularity.Season,
            ProviderIdType = "tvdbId", ProviderIdValue = "371572", SeasonNumber = 7, SeerrMediaId = null, SeerrTmdbId = 1408,
            ArrDeleteStatus = DeleteStepStatus.Succeeded, JellyfinCleanupStatus = DeleteStepStatus.Succeeded,
            SeerrUpdateStatus = DeleteStepStatus.Failed, NextRetryAtUtc = DateTime.UtcNow
        };

        Assert.False(await orchestrator.ProcessRetryEntryAsync(entry));
        Assert.Equal(DeleteStepStatus.Failed, entry.SeerrUpdateStatus);
    }
}
