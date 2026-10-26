using System.Net.Mime;
using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Configuration;
using Jellyfin.Plugin.CcSync.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CcSync.Api;

/// <summary>
/// The endpoints the plugin's dashboard pages call.
/// </summary>
/// <remarks>
/// Do not inject <c>ILogger&lt;T&gt;</c> here: plugin controllers are activated by a logger
/// factory that cannot resolve it (jellyfin#11488). Logging stays in the injected services.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("CcSync")]
[Produces(MediaTypeNames.Application.Json)]
public class CcSyncController : ControllerBase
{
    private readonly CcProbe _probe;
    private readonly HistoryStore _history;
    private readonly PendingStore _pending;

    public CcSyncController(CcProbe probe, HistoryStore history, PendingStore pending)
    {
        _probe = probe;
        _history = history;
        _pending = pending;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    [HttpPost("Probe")]
    public async Task<ActionResult<ProbeResponse>> Probe([FromBody] ProbeRequest? request, CancellationToken cancellationToken)
    {
        var config = Config;
        return await _probe.RunAsync(
            request?.Directory ?? config.ClipCornDirectory,
            request?.LockStaleMinutes ?? config.LockStaleMinutes,
            request?.TimeZone ?? config.ClipCornTimeZone,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Paged history, newest first. <paramref name="kind"/> ending in <c>*</c> matches a prefix.</summary>
    [HttpGet("History")]
    public ActionResult<HistoryPage> GetHistory(
        [FromQuery] string? kind,
        [FromQuery] string? search,
        [FromQuery] string? runId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
        => _history.Query(new HistoryQuery(kind, search, runId, from?.ToUniversalTime(), to?.ToUniversalTime(), limit ?? 100, offset ?? 0));

    [HttpDelete("History")]
    public ActionResult<DeleteResponse> PruneHistory([FromQuery] int olderThanDays)
    {
        if (olderThanDays < 0)
        {
            return BadRequest("olderThanDays must not be negative");
        }

        return new DeleteResponse { Deleted = _history.DeleteOlderThan(DateTime.UtcNow.AddDays(-olderThanDays)) };
    }

    [HttpGet("Pending")]
    public ActionResult<PendingResponse> GetPending([FromQuery] string? status, [FromQuery] int? limit, [FromQuery] int? offset)
        => new PendingResponse
        {
            Total = _pending.Count(status),
            Counts = _pending.CountsByStatus(),
            Items = _pending.List(status, Math.Clamp(limit ?? 100, 1, 1000), Math.Max(0, offset ?? 0)),
        };

    /// <summary><c>failed</c> → <c>pending</c>.</summary>
    [HttpPost("Pending/{id}/Retry")]
    public ActionResult RetryPending([FromRoute] long id)
    {
        if (!_pending.Retry(id))
        {
            return Conflict("Only failed updates can be retried.");
        }

        RecordManual(id, HistoryKinds.PendingRetry, "retried by hand");
        return NoContent();
    }

    /// <summary>A pending or failed update is discarded; it will never be written.</summary>
    [HttpDelete("Pending/{id}")]
    public ActionResult DiscardPending([FromRoute] long id)
    {
        if (!_pending.Discard(id, DateTime.UtcNow))
        {
            return Conflict("Only pending or failed updates can be discarded.");
        }

        RecordManual(id, HistoryKinds.PendingDiscarded, "discarded by hand");
        return NoContent();
    }

    private void RecordManual(long id, string kind, string what)
    {
        var update = _pending.Get(id);
        _history.Add(new HistoryEntry
        {
            TsUtc = DateTime.UtcNow,
            Kind = kind,
            JfItemId = update?.JfItemId.ToString("N"),
            CcId = update?.CcId,
            ItemName = update?.ItemName,
            ItemKind = update?.ItemKind,
            Detail = $"#{id} {what}",
        });
    }
}
