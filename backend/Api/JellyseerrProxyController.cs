using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// API controller for Jellyseerr SSO proxy.
/// Handles authentication, session management, and API proxying so that
/// any Moonfin client can access Jellyseerr through the Jellyfin server.
/// </summary>
[ApiController]
[Route("Moonfin/Jellyseerr")]
[Produces(MediaTypeNames.Application.Json)]
public class JellyseerrProxyController : ControllerBase
{
    private readonly JellyseerrSessionService _sessionService;

    public JellyseerrProxyController(JellyseerrSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    /// <summary>
    /// Authenticate with Jellyseerr using Jellyfin credentials.
    /// The session cookie is stored server-side and associated with the Jellyfin user.
    /// Any Moonfin client can then proxy requests through this plugin.
    /// </summary>
    /// <param name="request">Jellyfin credentials for Jellyseerr auth.</param>
    /// <returns>Authentication result with Jellyseerr user info.</returns>
    [HttpPost("Login")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Login([FromBody] JellyseerrLoginRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();
        if (config?.JellyseerrEnabled != true || string.IsNullOrEmpty(jellyseerrUrl))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Jellyseerr integration is not enabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new { error = "Username and password are required" });
        }

        var result = await _sessionService.AuthenticateAsync(
            userId.Value, request.Username, request.Password,
            request.AuthType);

        if (result == null || !result.Success)
        {
            return Unauthorized(new
            {
                error = result?.Error ?? "Authentication failed",
                success = false
            });
        }

        return Ok(new
        {
            success = true,
            jellyseerrUserId = result.JellyseerrUserId,
            displayName = result.DisplayName,
            avatar = result.Avatar,
            permissions = result.Permissions
        });
    }

    /// <summary>
    /// Check the current user's Jellyseerr SSO session status.
    /// </summary>
    /// <returns>Session status including whether authenticated and user info.</returns>
    [HttpGet("Status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();
        if (config?.JellyseerrEnabled != true || string.IsNullOrEmpty(jellyseerrUrl))
        {
            return Ok(new
            {
                enabled = false,
                authenticated = false,
                url = (string?)null
            });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Ok(new
            {
                enabled = true,
                authenticated = false,
                url = jellyseerrUrl
            });
        }

        var session = await _sessionService.GetSessionAsync(userId.Value, validate: false);

        return Ok(new
        {
            enabled = true,
            authenticated = session != null,
            url = jellyseerrUrl,
            jellyseerrUserId = session?.JellyseerrUserId,
            displayName = session?.DisplayName,
            avatar = session?.Avatar,
            permissions = session?.Permissions ?? 0,
            sessionCreated = session?.CreatedAt,
            lastValidated = session?.LastValidated
        });
    }

    /// <summary>
    /// Validate the current session is still active with Jellyseerr.
    /// </summary>
    /// <returns>Whether the session is valid.</returns>
    [HttpGet("Validate")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Ok(new { valid = false, error = "Not authenticated with Jellyfin" });
        }

        var session = await _sessionService.GetSessionAsync(userId.Value, validate: true);

        return Ok(new
        {
            valid = session != null,
            lastValidated = session?.LastValidated
        });
    }

    /// <summary>
    /// Clear the current user's Jellyseerr SSO session.
    /// </summary>
    [HttpDelete("Logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        // Proxy a logout to Jellyseerr first
        await _sessionService.ProxyRequestAsync(
            userId.Value,
            HttpMethod.Post,
            "auth/logout");

        // Then clear our stored session
        await _sessionService.ClearSessionAsync(userId.Value);

        return Ok(new { success = true, message = "Logged out from Jellyseerr" });
    }

    /// <summary>
    /// Proxy GET requests to Jellyseerr API.
    /// Path is relative to /api/v1/ (e.g., "auth/me", "request", "search?query=foo").
    /// </summary>
    [HttpGet("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyGet(string path)
    {
        return await ProxyApiRequest(HttpMethod.Get, path);
    }

    /// <summary>
    /// Proxy POST requests to Jellyseerr API.
    /// </summary>
    [HttpPost("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyPost(string path)
    {
        return await ProxyApiRequest(HttpMethod.Post, path);
    }

    /// <summary>
    /// Proxy PUT requests to Jellyseerr API.
    /// </summary>
    [HttpPut("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyPut(string path)
    {
        return await ProxyApiRequest(HttpMethod.Put, path);
    }

    /// <summary>
    /// Proxy DELETE requests to Jellyseerr API.
    /// </summary>
    [HttpDelete("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyDelete(string path)
    {
        return await ProxyApiRequest(HttpMethod.Delete, path);
    }

    private async Task<IActionResult> ProxyApiRequest(HttpMethod method, string path)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();
        if (config?.JellyseerrEnabled != true || string.IsNullOrEmpty(jellyseerrUrl))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Jellyseerr integration is not enabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        // Read request body for POST/PUT
        byte[]? body = null;
        string? contentType = null;

        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            body = ms.ToArray();
            contentType = Request.ContentType;
        }

        var result = await _sessionService.ProxyRequestAsync(
            userId.Value,
            method,
            path,
            Request.QueryString.Value,
            body,
            contentType);

        return StatusCode(result.StatusCode, result.Body != null
            ? new FileContentResult(result.Body, result.ContentType)
            : null);
    }

    // ── Jellyseerr Web Proxy (iframe auth) ──────────────────────────────

    private const string ProxyBasePath = "/Moonfin/Jellyseerr/Web";

    // Short-lived proxy sessions: token → (userId, expiry)
    // Allows iframe sub-resource loads without api_key in every URL
    private static readonly ConcurrentDictionary<string, (Guid UserId, DateTimeOffset Expiry)> _proxySessions = new();

    /// <summary>
    /// Proxies Jellyseerr web content through the Jellyfin server, injecting the stored
    /// SSO session cookie. This allows the Jellyseerr iframe to be pre-authenticated.
    /// The first request must include api_key for Jellyfin auth; a proxy session cookie is
    /// then set so subsequent resource loads (scripts, styles, etc.) don't need it.
    /// </summary>
    [Route("Web/{**path}")]
    [Route("Web")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    [AcceptVerbs("GET", "POST", "PUT", "DELETE")]
    public async Task<IActionResult> ProxyWeb(string? path = null)
    {
        // Authenticate: proxy session cookie first, then Jellyfin auth (api_key)
        var userId = GetProxySessionUserId() ?? GetJellyfinAuthUserId();
        if (userId == null)
        {
            return Unauthorized("Authentication required");
        }

        // Set/refresh proxy session cookie for subsequent requests
        EnsureProxySession(userId.Value);

        var method = new HttpMethod(Request.Method);

        // Read body for POST/PUT
        byte[]? body = null;
        string? contentType = null;
        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            body = ms.ToArray();
            contentType = Request.ContentType;
        }

        // Strip api_key from query string before forwarding to Jellyseerr
        var queryString = StripQueryParam(Request.QueryString.Value, "api_key");

        var result = await _sessionService.ProxyWebRequestAsync(
            userId.Value,
            method,
            path ?? "",
            queryString,
            body,
            contentType);

        if (result.Body == null)
        {
            return StatusCode(result.StatusCode);
        }

        // For HTML responses, rewrite absolute paths and inject proxy script
        if (result.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(result.Body);
            html = RewriteHtmlForProxy(html);
            return Content(html, "text/html; charset=utf-8");
        }

        // For CSS responses, rewrite url() and @import references
        if (result.ContentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
        {
            var css = Encoding.UTF8.GetString(result.Body);
            css = RewriteCssForProxy(css);
            return File(Encoding.UTF8.GetBytes(css), result.ContentType);
        }

        return File(result.Body, result.ContentType);
    }

    private Guid? GetProxySessionUserId()
    {
        var cookie = Request.Cookies["moonfin_proxy"];
        if (string.IsNullOrEmpty(cookie)) return null;

        if (_proxySessions.TryGetValue(cookie, out var session) && session.Expiry > DateTimeOffset.UtcNow)
        {
            return session.UserId;
        }

        _proxySessions.TryRemove(cookie, out _);
        return null;
    }

    private Guid? GetJellyfinAuthUserId()
    {
        return User.Identity?.IsAuthenticated == true ? this.GetUserIdFromClaims() : null;
    }

    private void EnsureProxySession(Guid userId)
    {
        // If a valid proxy session cookie already exists, skip
        var existingCookie = Request.Cookies["moonfin_proxy"];
        if (!string.IsNullOrEmpty(existingCookie)
            && _proxySessions.TryGetValue(existingCookie, out var existing)
            && existing.Expiry > DateTimeOffset.UtcNow)
        {
            return;
        }

        // Clean expired sessions periodically
        var now = DateTimeOffset.UtcNow;
        if (_proxySessions.Count > 100)
        {
            foreach (var kvp in _proxySessions)
            {
                if (kvp.Value.Expiry < now)
                    _proxySessions.TryRemove(kvp.Key, out _);
            }
        }

        // Remove stale cookie from dictionary if present
        if (!string.IsNullOrEmpty(existingCookie))
        {
            _proxySessions.TryRemove(existingCookie, out _);
        }

        var token = Guid.NewGuid().ToString("N");
        _proxySessions[token] = (userId, now.AddHours(12));

        Response.Cookies.Append("moonfin_proxy", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = ProxyBasePath,
            MaxAge = TimeSpan.FromHours(12),
            Secure = Request.IsHttps
        });
    }

    private static string? StripQueryParam(string? queryString, string param)
    {
        if (string.IsNullOrEmpty(queryString)) return null;

        var parts = queryString.TrimStart('?').Split('&')
            .Where(p => !p.StartsWith(param + "=", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return parts.Length > 0 ? "?" + string.Join("&", parts) : null;
    }

    private static string RewriteHtmlForProxy(string html)
    {
        // Rewrite absolute paths in src, href, action, srcset attributes
        // Matches: src="/ but not src="// (protocol-relative) or already-proxied paths
        html = Regex.Replace(html,
            @"((?:src|href|action|srcset)\s*=\s*"")\/(?!\/|Moonfin)",
            $"$1{ProxyBasePath}/");
        html = Regex.Replace(html,
            @"((?:src|href|action|srcset)\s*=\s*')\/(?!\/|Moonfin)",
            $"$1{ProxyBasePath}/");

        // Inject proxy URL rewriter script after <head>
        var headIdx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
        {
            var closeIdx = html.IndexOf('>', headIdx);
            if (closeIdx >= 0)
            {
                html = html.Insert(closeIdx + 1, ProxyScript);
            }
        }

        return html;
    }

    private static string RewriteCssForProxy(string css)
    {
        // Rewrite absolute url() references: url('/path') → url('/Moonfin/Jellyseerr/Web/path')
        css = Regex.Replace(css,
            @"url\(\s*(['""]?)\/(?!\/|Moonfin)",
            $"url($1{ProxyBasePath}/");

        // Rewrite @import with absolute paths
        css = Regex.Replace(css,
            @"@import\s+(['""])\/(?!\/|Moonfin)",
            $"@import $1{ProxyBasePath}/");

        return css;
    }

    private const string ProxyScript = @"<script data-moonfin-proxy>(function(){
var b='/Moonfin/Jellyseerr/Web';
function r(v){return typeof v==='string'&&v[0]==='/'&&v[1]!=='/'&&v.indexOf(b)!==0?b+v:v}
var F=window.fetch;window.fetch=function(u,o){return F.call(this,typeof u==='string'?r(u):u,o)};
var X=XMLHttpRequest.prototype.open;XMLHttpRequest.prototype.open=function(){if(typeof arguments[1]==='string')arguments[1]=r(arguments[1]);return X.apply(this,arguments)};
var S=Element.prototype.setAttribute;Element.prototype.setAttribute=function(n,v){if(n==='src'||n==='href'||n==='action')v=r(v);return S.call(this,n,v)};
function P(p,n){var d=Object.getOwnPropertyDescriptor(p,n);if(d&&d.set)Object.defineProperty(p,n,{set:function(v){d.set.call(this,r(v))},get:d.get,configurable:true,enumerable:d.enumerable})}try{P(HTMLScriptElement.prototype,'src');P(HTMLImageElement.prototype,'src');P(HTMLSourceElement.prototype,'src');P(HTMLLinkElement.prototype,'href')}catch(e){}
document.addEventListener('click',function(e){if(e.defaultPrevented)return;var a=e.target&&e.target.closest?e.target.closest('a[href]'):null;if(!a)return;var h=a.getAttribute('href');if(h&&h[0]==='/'&&h[1]!=='/'&&h.indexOf(b)!==0){S.call(a,'href',b+h)}});
new MutationObserver(function(ms){ms.forEach(function(m){m.addedNodes.forEach(function(n){if(n.nodeType!==1)return;var fix=function(e){['src','href'].forEach(function(a){var v=e.getAttribute(a);if(v&&v[0]==='/'&&v[1]!=='/'&&v.indexOf(b)!==0)S.call(e,a,b+v)})};fix(n);if(n.querySelectorAll)n.querySelectorAll('[src],[href]').forEach(fix)})})}).observe(document.documentElement,{childList:true,subtree:true});
})()</script>";
}

/// <summary>
/// Request body for Jellyseerr login.
/// </summary>
public class JellyseerrLoginRequest
{
    /// <summary>Username (Jellyfin or local Jellyseerr account).</summary>
    public string? Username { get; set; }

    /// <summary>Password.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Authentication type: "jellyfin" (default) or "local".
    /// Determines which Jellyseerr auth endpoint is used.
    /// </summary>
    public string? AuthType { get; set; }
}
