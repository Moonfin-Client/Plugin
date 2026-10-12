using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Moonfin.Seasonal;
using Moonfin.Seerr;

namespace Emby.Plugins.Moonfin.Api
{
    public class SeerrService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        // How long a status check trusts Seerr's last answer about a session before asking again.
        private static readonly TimeSpan SessionFreshness = TimeSpan.FromMinutes(5);

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private SeerrSessionService Session => Plugin.Instance?.SeerrService
            ?? throw new InvalidOperationException("SeerrSessionService not initialized");

        public SeerrService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        public async Task<object?> Post(SeerrLoginRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
            { Request.Response.StatusCode = 503; return new { error = "Seerr integration is not enabled" }; }

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }
            if (string.IsNullOrEmpty(request.Username)) { Request.Response.StatusCode = 400; return new { error = "Username is required" }; }

            var result = await Session.AuthenticateAsync(userId.Value, request.Username, request.Password, request.AuthType).ConfigureAwait(false);

            if (result == null || !result.Success)
            {
                Request.Response.StatusCode = 401;
                return new { error = result?.Error ?? "Authentication failed", success = false };
            }

            // Only an admin or the owner session can provision the webhook, so trigger it for
            // those logins instead of every successful login. Best-effort, throttled internally.
            const int adminBit = 2;
            const int ownerSeerrUserId = 1;
            var provisioning = Plugin.Instance?.SeerrProvisioning;
            if (provisioning != null
                && (result.SeerrUserId == ownerSeerrUserId || (result.Permissions & adminBit) != 0))
            {
                _ = Task.Run(async () =>
                {
                    try { await provisioning.EnsureWebhookAsync(System.Threading.CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                });
            }

            return new { success = true, seerrUserId = result.SeerrUserId, jellyseerrUserId = result.SeerrUserId, displayName = result.DisplayName, avatar = result.Avatar, permissions = result.Permissions };
        }

        public async Task<object?> Get(GetSeerrStatusRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
                return new { enabled = false, authenticated = false, url = (string?)null };

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return new { enabled = true, authenticated = false, url = config.SeerrUrl };

            // Confirm the session against Seerr rather than trusting that a file exists. A
            // reinstalled or reconfigured Seerr keeps none of its old sessions, and reporting
            // authenticated on one of those left the client connected to a Seerr that refused
            // every call, with no prompt to sign in again.
            var sess = await Session.GetSessionAsync(
                userId.Value, validate: true, maxAge: SessionFreshness).ConfigureAwait(false);
            return new
            {
                enabled = true,
                authenticated = sess != null,
                url = config.SeerrUrl,
                seerrUserId = sess?.SeerrUserId,
                jellyseerrUserId = sess?.SeerrUserId, // alias kept for older clients
                displayName = sess?.DisplayName,
                avatar = sess?.Avatar,
                permissions = sess?.Permissions ?? 0,
                sessionCreated = sess?.CreatedAt,
                lastValidated = sess?.LastValidated
            };
        }

        public async Task<object?> Get(ValidateSeerrSessionRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return new { valid = false, error = "Not authenticated" };
            var sess = await Session.GetSessionAsync(userId.Value, validate: true).ConfigureAwait(false);
            return new { valid = sess != null, lastValidated = sess?.LastValidated };
        }

        public async Task<object?> Delete(SeerrLogoutRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }

            await Session.ProxyRequestAsync(userId.Value, HttpMethod.Post, "auth/logout").ConfigureAwait(false);
            await Session.ClearSessionAsync(userId.Value).ConfigureAwait(false);
            return new { success = true, message = "Logged out from Seerr" };
        }

        public async Task<object?> Get(SeerrProxyGetRequest request) => await ProxyApiRequest(HttpMethod.Get, request.Path, null).ConfigureAwait(false);
        public async Task<object?> Post(SeerrProxyPostRequest request) => await ProxyApiRequest(HttpMethod.Post, request.Path, request.RequestStream).ConfigureAwait(false);
        public async Task<object?> Put(SeerrProxyPutRequest request) => await ProxyApiRequest(HttpMethod.Put, request.Path, request.RequestStream).ConfigureAwait(false);
        public async Task<object?> Delete(SeerrProxyDeleteRequest request) => await ProxyApiRequest(HttpMethod.Delete, request.Path, null).ConfigureAwait(false);

        public Task<object?> Get(GetRadarrCalendarRequest request) => GetArrCalendar("radarr", request.Start, request.End);
        public Task<object?> Get(GetSonarrCalendarRequest request) => GetArrCalendar("sonarr", request.Start, request.End);

        private async Task<object?> GetArrCalendar(string service, string? start, string? end)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
            { Request.Response.StatusCode = 503; return new { error = "Seerr integration is not enabled" }; }

            if (AuthHelpers.GetCurrentUserId(Request, _authContext) == null)
            { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }

            var result = await Session.GetArrCalendarAsync(service, start, end).ConfigureAwait(false);

            Request.Response.StatusCode = result.StatusCode;
            if (result.Body == null) return null;

            var responseContentType = string.IsNullOrWhiteSpace(result.ContentType) ? "application/octet-stream" : result.ContentType;
            return ResultFactory.GetResult(Request, new MemoryStream(result.Body), responseContentType, null);
        }

        private async Task<object?> ProxyApiRequest(HttpMethod method, string? path, System.IO.Stream? bodyStream)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.SeerrEnabled != true || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
            { Request.Response.StatusCode = 503; return new { error = "Seerr integration is not enabled" }; }

            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            var userId = (user != null && user.Id != Guid.Empty) ? user.Id : (Guid?)null;
            if (userId == null) { Request.Response.StatusCode = 401; return new { error = "User not authenticated" }; }

            byte[]? body = null;
            string? contentType = null;
            if (bodyStream != null && (method == HttpMethod.Post || method == HttpMethod.Put))
            {
                using var ms = new MemoryStream();
                await bodyStream.CopyToAsync(ms).ConfigureAwait(false);
                body = ms.ToArray();
                contentType = Request.ContentType;
            }

            // A user with a parental limit gets Seerr held to it: lists lose the titles above it,
            // a details page above it is refused, and so is a request for one. Everyone else is
            // passed through as they were.
            var verdict = await VerdictFor(user!).ConfigureAwait(false);
            if (verdict != null && method == HttpMethod.Post && SeerrParentalFilter.IsRequestPath(path))
            {
                var target = SeerrParentalFilter.RequestTarget(body);
                if (target == null || !await verdict(target.Value.MediaType, target.Value.TmdbId).ConfigureAwait(false))
                { Request.Response.StatusCode = 403; return new { message = AboveLimit }; }
            }

            var result = await Session.ProxyRequestAsync(userId.Value, method, path ?? string.Empty, Http.QueryString(Request), body, contentType).ConfigureAwait(false);

            if (verdict != null && method == HttpMethod.Get && result.StatusCode == 200 && result.Body != null && IsJson(result.ContentType))
            {
                var mediaType = SeerrParentalFilter.DetailsMediaType(path);
                if (mediaType != null)
                {
                    var tmdbId = SeerrParentalFilter.DetailsTmdbId(result.Body);
                    if (tmdbId == null || !await verdict(mediaType, tmdbId.Value).ConfigureAwait(false))
                    { Request.Response.StatusCode = 403; return new { message = AboveLimit }; }
                }

                var filtered = await SeerrParentalFilter.FilterListAsync(path, result.Body, verdict, CancellationToken.None).ConfigureAwait(false);
                if (filtered != null)
                {
                    result.Body = filtered;
                    var ratings = Plugin.Instance?.TmdbRatings;
                    if (ratings != null) await ratings.FlushAsync().ConfigureAwait(false);
                }
            }

            Request.Response.StatusCode = result.StatusCode;
            if (result.Body == null) return null;

            var responseContentType = string.IsNullOrWhiteSpace(result.ContentType) ? "application/octet-stream" : result.ContentType;
            return ResultFactory.GetResult(Request, new MemoryStream(result.Body), responseContentType, null);
        }

        private const string AboveLimit = "This title is above the parental rating of this account";

        /// <summary>
        /// The verdict a limited user's titles are held to, or null for a user without a limit.
        /// Without a TMDB key nothing can be judged, so nothing passes: the same rule the custom
        /// rows follow. A lookup that fails is a title that cannot be placed, and goes the same way.
        /// </summary>
        private static async Task<SeerrParentalFilter.Verdict?> VerdictFor(User user)
        {
            var limit = Plugin.Instance?.RatingLimits?.ForUser(user) ?? RatingLimit.None;
            if (!limit.HasLimit) return null;

            var ratings = Plugin.Instance?.TmdbRatings;
            var settings = Plugin.Instance?.SettingsService;
            var profile = settings == null ? null : await settings.GetResolvedProfileAsync(user.Id, "global").ConfigureAwait(false);
            var apiKey = string.IsNullOrWhiteSpace(profile?.TmdbApiKey) ? Plugin.Instance?.Configuration?.TmdbApiKey : profile!.TmdbApiKey;

            return async (mediaType, tmdbId) =>
            {
                if (ratings == null || string.IsNullOrWhiteSpace(apiKey)) return false;
                try
                {
                    var certification = await ratings.GetCertificationAsync(mediaType, tmdbId, apiKey!, CancellationToken.None).ConfigureAwait(false);
                    return limit.IsAllowed(certification);
                }
                catch (Exception)
                {
                    return false;
                }
            };
        }

        private static bool IsJson(string? contentType) =>
            contentType != null && contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
