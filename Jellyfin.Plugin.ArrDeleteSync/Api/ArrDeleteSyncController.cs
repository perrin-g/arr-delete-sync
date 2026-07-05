// NOTE: verified via reflection probe against the installed Jellyfin.Common 10.11.11 package —
// MediaBrowser.Common.Api.Policies.RequiresElevation == "RequiresElevation". This is the standard
// policy name used by Jellyfin's own admin-only dashboard endpoints as of 10.11.
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrDeleteSync.Models;
using Jellyfin.Plugin.ArrDeleteSync.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArrDeleteSync.Api;

[ApiController]
[Route("ArrDeleteSync")]
[Authorize(Policy = "RequiresElevation")]
public class ArrDeleteSyncController : ControllerBase
{
    private readonly IDeleteOrchestrator _orchestrator;
    private readonly IRetryQueueStore _retryQueueStore;
    private readonly IAuditLogStore _auditLogStore;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IHttpClientFactory _httpClientFactory;

    public ArrDeleteSyncController(
        IDeleteOrchestrator orchestrator,
        IRetryQueueStore retryQueueStore,
        IAuditLogStore auditLogStore,
        ICircuitBreaker circuitBreaker,
        IHttpClientFactory httpClientFactory)
    {
        _orchestrator = orchestrator;
        _retryQueueStore = retryQueueStore;
        _auditLogStore = auditLogStore;
        _circuitBreaker = circuitBreaker;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("resolve")]
    public async Task<ActionResult<ResolutionResult>> Resolve([FromQuery] Guid itemId, [FromQuery] DeleteGranularity granularity)
    {
        var result = await _orchestrator.ResolveAsync(itemId, granularity);
        return Ok(result);
    }

    [HttpPost("delete")]
    public async Task<ActionResult> Delete([FromBody] DeleteRequest request)
    {
        var outcome = await _orchestrator.ExecuteDeleteAsync(request);
        return Ok(outcome);
    }

    [HttpGet("retry-queue")]
    public async Task<ActionResult> GetRetryQueue()
    {
        var entries = await _retryQueueStore.GetAllAsync();
        return Ok(entries);
    }

    [HttpPost("retry-queue/{id}/retry")]
    public async Task<ActionResult> RetryEntry(Guid id)
    {
        var entry = (await _retryQueueStore.GetAllAsync()).FirstOrDefault(e => e.Id == id);
        if (entry == null)
        {
            return NotFound();
        }

        var resolved = await _orchestrator.ProcessRetryEntryAsync(entry);
        if (resolved)
        {
            await _retryQueueStore.RemoveAsync(id);
        }
        else
        {
            var maxAttempts = Plugin.Instance?.Configuration?.RetryMaxAttempts ?? 5;
            RetryBackoffCalculator.RecordFailedAttempt(entry, maxAttempts);
            await _retryQueueStore.UpsertAsync(entry);
        }

        return Ok(new { resolved });
    }

    [HttpPost("retry-queue/{id}/dismiss")]
    public async Task<ActionResult> DismissEntry(Guid id)
    {
        var entry = (await _retryQueueStore.GetAllAsync()).FirstOrDefault(e => e.Id == id);
        if (entry == null)
        {
            return NotFound();
        }

        await _retryQueueStore.RemoveAsync(id);
        await _auditLogStore.AppendAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTime.UtcNow,
            JellyfinItemId = entry.JellyfinItemId,
            ItemDisplayName = "(dismissed retry entry)",
            Granularity = entry.Granularity,
            Action = "Dismissed",
            Outcome = "Partial",
            ErrorDetail = entry.ArrDeleteStatus != DeleteStepStatus.Succeeded
                ? "Dismissed before arr deletion completed — the file was never removed; safe to leave as-is."
                : (entry.JellyfinCleanupStatus != DeleteStepStatus.Succeeded || entry.SeerrUpdateStatus != DeleteStepStatus.Succeeded
                    ? "Dismissed with Jellyfin catalog/Seerr sync still incomplete after arr already deleted the file."
                    : null)
        });

        return Ok(new { dismissed = true });
    }

    public record TestConnectionRequest(string Service, string Url, string ApiKey);

    [HttpPost("test")]
    public async Task<ActionResult> TestConnection([FromBody] TestConnectionRequest body)
    {
        string testPath = body.Service?.ToLowerInvariant() switch
        {
            "radarr" or "sonarr" => "/api/v3/system/status",
            "seerr" => "/api/v1/settings/main",
            _ => null!
        };

        if (testPath == null)
            return Ok(new { ok = false, message = "Unknown service." });

        var baseUrl = body.Url?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(baseUrl))
            return Ok(new { ok = false, message = "URL is required." });

        if (string.IsNullOrEmpty(body.ApiKey))
            return Ok(new { ok = false, message = "API key is required." });

        try
        {
            var httpClient = _httpClientFactory.CreateClient("ArrDeleteSync-Test");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + testPath);
            request.Headers.Add("X-Api-Key", body.ApiKey);
            using var response = await httpClient.SendAsync(request);
            return response.IsSuccessStatusCode
                ? Ok(new { ok = true, message = $"Connected ({(int)response.StatusCode})" })
                : Ok(new { ok = false, message = $"HTTP {(int)response.StatusCode}" });
        }
        catch (TaskCanceledException)
        {
            return Ok(new { ok = false, message = "Connection timed out." });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    [HttpGet("audit-log")]
    public async Task<ActionResult> GetAuditLog()
    {
        var entries = await _auditLogStore.GetAllAsync();
        return Ok(entries);
    }

    [HttpPost("circuit-breaker/reset")]
    public Task<ActionResult> ResetCircuitBreaker()
    {
        _circuitBreaker.Reset();
        return Task.FromResult<ActionResult>(Ok());
    }

    [HttpGet("circuit-breaker/status")]
    public ActionResult GetCircuitBreakerStatus()
    {
        return Ok(new { IsTripped = _circuitBreaker.IsTripped });
    }
}
