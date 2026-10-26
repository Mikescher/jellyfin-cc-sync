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

    public CcSyncController(CcProbe probe, HistoryStore history)
    {
        _probe = probe;
        _history = history;
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
}
