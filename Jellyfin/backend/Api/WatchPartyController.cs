using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.WatchParty;

namespace Moonfin.Server.Api;

/// <summary>
/// Watch parties. The routes, rooms and JSON live in the shared engine, so this only hands each
/// request over and writes the answer back.
/// </summary>
[ApiController]
public class WatchPartyController : ControllerBase
{
    private readonly WatchPartyEngine _engine;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _config;

    public WatchPartyController(WatchPartyEngine engine, IServerApplicationHost appHost, IServerConfigurationManager config)
    {
        _engine = engine;
        _appHost = appHost;
        _config = config;
    }

    [AcceptVerbs("GET", "POST", "DELETE", Route = "Moonfin/WatchParty/{**path}")]
    [Authorize]
    public async Task<IActionResult> Room([FromRoute] string? path)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        var caller = new WatchPartyCaller(userId.Value.ToString("N"), this.GetDeviceIdFromClaims(), PublicBase());
        var result = await WatchPartyRoutes.HandleAsync(
            _engine,
            Request.Method,
            path,
            caller,
            Request.Headers["X-Moonfin-Room-Token"].FirstOrDefault(),
            await ReadBody().ConfigureAwait(false),
            key => Request.Query[key].FirstOrDefault(),
            HttpContext.RequestAborted).ConfigureAwait(false);
        return Send(result);
    }

    [AcceptVerbs("GET", "POST", Route = "Moonfin/Admin/WatchParty/{**path}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> Admin([FromRoute] string? path) =>
        Send(WatchPartyRoutes.HandleAdmin(_engine, Request.Method, path, await ReadBody().ConfigureAwait(false)));

    /// <summary>The invite page, which needs no sign-in and never looks the code up, and its preview image.</summary>
    [HttpGet("Moonfin/Party/{code}")]
    [AllowAnonymous]
    public IActionResult Invite([FromRoute] string code)
    {
        if (string.Equals(code, "preview.png", StringComparison.OrdinalIgnoreCase))
        {
            var png = WatchPartyInvite.PreviewPng();
            return png == null ? NotFound() : File(png, "image/png");
        }

        var normalized = WatchPartyEngine.NormalizeCode(code);
        if (normalized == null)
        {
            return NotFound();
        }

        return Content(WatchPartyInvite.Page(_appHost.FriendlyName, _appHost.SystemId, PublicBase(), normalized), "text/html; charset=utf-8");
    }

    /// <summary>
    /// Where invite links point: Moonbase's public server URL, else Jellyfin's published server URL,
    /// else the address this request came in at.
    /// </summary>
    private string PublicBase()
    {
        var request = WatchPartyInvite.PublicBase(
            Request.Scheme,
            Request.Host.Value ?? string.Empty,
            Request.Headers["X-Forwarded-Proto"].FirstOrDefault(),
            Request.Headers["X-Forwarded-Host"].FirstOrDefault(),
            Request.PathBase.Value);
        return WatchPartyInvite.ChooseBase(MoonfinPlugin.Instance?.Configuration.PublicServerUrl, WatchPartyInvite.ChooseBase(PublishedServerUrl(), request));
    }

    // The network configuration moved between Jellyfin versions, so its one setting is read by name.
    private string? PublishedServerUrl()
    {
        try
        {
            var network = _config.GetConfiguration("network");
            return WatchPartyInvite.Published(network?.GetType().GetProperty("PublishedServerUriBySubnet")?.GetValue(network) as IEnumerable<string>);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<byte[]?> ReadBody() =>
        HttpMethods.IsGet(Request.Method) ? null : await WatchPartyRoutes.ReadBodyAsync(Request.Body, HttpContext.RequestAborted).ConfigureAwait(false);

    private IActionResult Send(WatchPartyResult result)
    {
        if (result.RetryAfterSeconds > 0)
        {
            Response.Headers["Retry-After"] = result.RetryAfterSeconds.ToString();
        }

        return result.Body == null
            ? StatusCode(result.Status)
            : new ContentResult { StatusCode = result.Status, Content = Encoding.UTF8.GetString(result.Json()), ContentType = "application/json; charset=utf-8" };
    }
}
