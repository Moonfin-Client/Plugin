using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Moonfin.WatchParty;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Watch parties. The routes, rooms and JSON live in the shared engine, so this only hands each
    /// request over and writes the answer back.
    /// </summary>
    public class WatchPartyService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;
        private readonly IServerApplicationHost _appHost;
        private readonly IServerConfigurationManager _serverConfig;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public WatchPartyService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            _appHost = appHost.Resolve<IServerApplicationHost>();
            _serverConfig = appHost.Resolve<IServerConfigurationManager>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        public Task<object?> Get(WatchPartyGetRequest request) => Room("GET", request.Path, null);

        public async Task<object?> Post(WatchPartyPostRequest request) =>
            await Room("POST", request.Path, await ReadBody(request.RequestStream).ConfigureAwait(false)).ConfigureAwait(false);

        public Task<object?> Delete(WatchPartyDeleteRequest request) => Room("DELETE", request.Path, null);

        public object? Get(WatchPartyAdminGetRequest request) => Admin("GET", request.Path, null);

        public async Task<object?> Post(WatchPartyAdminPostRequest request) =>
            Admin("POST", request.Path, await ReadBody(request.RequestStream).ConfigureAwait(false));

        public object? Get(WatchPartyInviteRequest request)
        {
            var code = request.Code ?? string.Empty;
            if (code.Equals("preview.png", StringComparison.OrdinalIgnoreCase) || code.Equals("preview", StringComparison.OrdinalIgnoreCase))
            {
                var png = WatchPartyInvite.PreviewPng();
                if (png == null)
                {
                    Request.Response.StatusCode = 404;
                    return null;
                }

                return ResultFactory.GetResult(Request, new MemoryStream(png), "image/png", null);
            }

            var normalized = WatchPartyEngine.NormalizeCode(code);
            if (normalized == null)
            {
                Request.Response.StatusCode = 404;
                return null;
            }

            var page = WatchPartyInvite.Page(_appHost.FriendlyName, _appHost.SystemId, PublicBase(), normalized);
            return ResultFactory.GetResult(Request, new MemoryStream(Encoding.UTF8.GetBytes(page)), "text/html; charset=utf-8", null);
        }

        private async Task<object?> Room(string method, string? path, byte[]? body)
        {
            var engine = Plugin.Instance?.WatchParty;
            if (engine == null)
            {
                return Send(WatchPartyResult.Error("disabled", "The plugin isn't ready yet."));
            }

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null)
            {
                Request.Response.StatusCode = 401;
                return MoonfinJson.Result(Request, ResultFactory, new { error = "A valid user token is required" });
            }

            var deviceId = _authContext.GetAuthorizationInfo(Request)?.DeviceId.ToString();
            var result = await WatchPartyRoutes.HandleAsync(
                engine,
                method,
                path,
                new WatchPartyCaller(userId.Value.ToString("N"), deviceId, PublicBase()),
                Request.Headers["X-Moonfin-Room-Token"],
                body,
                key => Request.QueryString[key]).ConfigureAwait(false);
            return Send(result);
        }

        private object? Admin(string method, string? path, byte[]? body)
        {
            var engine = Plugin.Instance?.WatchParty;
            return engine == null
                ? Send(WatchPartyResult.Error("disabled", "The plugin isn't ready yet."))
                : Send(WatchPartyRoutes.HandleAdmin(engine, method, path, body));
        }

        private object? Send(WatchPartyResult result)
        {
            Request.Response.StatusCode = result.Status;
            if (result.RetryAfterSeconds > 0)
            {
                Request.Response.AddHeader("Retry-After", result.RetryAfterSeconds.ToString());
            }

            return result.Body == null ? null : ResultFactory.GetResult(Request, new MemoryStream(result.Json()), "application/json", null);
        }

        /// <summary>
        /// Where invite links point: Moonbase's public server URL, else Emby's external domain, else
        /// the address this request came in at.
        /// </summary>
        private string PublicBase() =>
            WatchPartyInvite.ChooseBase(Plugin.Instance?.Configuration.PublicServerUrl, WatchPartyInvite.ChooseBase(WanUrl(), RequestBase(new Uri(Request.AbsoluteUri))));

        private string? WanUrl()
        {
            var c = _serverConfig.Configuration;
            var host = c?.WanDdns?.Trim();
            if (c == null || string.IsNullOrEmpty(host))
            {
                return null;
            }

            if (host!.Contains("://"))
            {
                return host;
            }

            var https = c.EnableHttps && c.PublicHttpsPort > 0;
            var port = https ? c.PublicHttpsPort : c.PublicPort;
            var plain = port <= 0 || (https && port == 443) || (!https && port == 80);
            return (https ? "https://" : "http://") + host + (plain ? string.Empty : ":" + port);
        }

        /// <summary>The request's address, the proxy's when one passed it on, without Emby's own /emby prefix.</summary>
        private string RequestBase(Uri uri)
        {
            var path = uri.AbsolutePath;
            var at = path.IndexOf("/Moonfin/Party/", StringComparison.OrdinalIgnoreCase);
            var basePath = at > 0 ? path.Substring(0, at) : string.Empty;
            foreach (var own in new[] { "/emby", "/mediabrowser" })
            {
                if (basePath.EndsWith(own, StringComparison.OrdinalIgnoreCase))
                {
                    basePath = basePath.Substring(0, basePath.Length - own.Length);
                }
            }

            return WatchPartyInvite.PublicBase(uri.Scheme, uri.Authority, Request.Headers["X-Forwarded-Proto"], Request.Headers["X-Forwarded-Host"], basePath);
        }

        private static async Task<byte[]?> ReadBody(Stream? stream) =>
            stream == null ? null : await WatchPartyRoutes.ReadBodyAsync(stream).ConfigureAwait(false);
    }
}
