using System.Net.Mime;
using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.Configuration;
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

    public CcSyncController(CcProbe probe)
    {
        _probe = probe;
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
}
