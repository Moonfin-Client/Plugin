using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    public class SeerrSessionService
    {
        private readonly string _sessionsPath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILogger _logger;
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private static readonly string[] CsrfCookieNames = { "XSRF-TOKEN", "_csrf", "csrf", "csrfToken" };
        private static readonly string[] CsrfProbePaths = { "/", "/api/v1/settings/public" };
        // CSRF cookies Seerr sets next to the session cookie. They are never the session
        // cookie, so we skip them when looking for a renamed one.
        private static readonly string[] NonSessionCookieNames = { "XSRF-TOKEN", "_csrf" };

        public SeerrSessionService(ILogger logger)
        {
            _logger = logger;
            var dataPath = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "programdata", "plugins", "Moonfin");
            _sessionsPath = Path.Combine(dataPath, "seerr-sessions");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            EnsureDirectory();
        }

        private void EnsureDirectory()
        {
            if (!Directory.Exists(_sessionsPath)) Directory.CreateDirectory(_sessionsPath);
        }

        private string GetSessionPath(Guid userId) => Path.Combine(_sessionsPath, $"{userId}.json");

        private async Task<string?> FetchCsrfTokenAsync(HttpClient client, string seerrUrl, CookieContainer cookieContainer)
        {
            var baseUri = new Uri(seerrUrl.TrimEnd('/'));
            foreach (var path in CsrfProbePaths)
            {
                try
                {
                    using var response = await client.GetAsync(baseUri.AbsoluteUri + path, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    var cookies = cookieContainer.GetCookies(baseUri);
                    foreach (var name in CsrfCookieNames)
                    {
                        var cookie = cookies[name];
                        if (cookie != null && !string.IsNullOrEmpty(cookie.Value))
                            return Uri.UnescapeDataString(cookie.Value);
                    }

                    if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) continue;
                    string? tokenFromHeader = null;
                    foreach (var header in headers)
                    {
                        foreach (var name in CsrfCookieNames)
                        {
                            var prefix = name + "=";
                            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            var value = header.Substring(prefix.Length);
                            var semi = value.IndexOf(';');
                            if (semi >= 0) value = value.Substring(0, semi);
                            // Re-add the CSRF cookie without the Secure flag so .NET's CookieContainer
                            // keeps it over plain HTTP and replays it on the login POST, which Seerr's
                            // double-submit CSRF check needs.
                            try { cookieContainer.Add(baseUri, new Cookie(name, value) { Secure = false }); }
                            catch { }
                            if (tokenFromHeader == null) tokenFromHeader = Uri.UnescapeDataString(value);
                        }
                    }
                    if (tokenFromHeader != null) return tokenFromHeader;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Debug("CSRF prefetch failed for " + baseUri.AbsoluteUri + path + " (non-fatal)");
                }
            }
            return null;
        }

        private static bool IsNonSessionCookie(string name)
        {
            foreach (var n in NonSessionCookieNames)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // express-session signs its cookie, so the decoded value starts with "s:". That tells
        // the real session cookie apart from CSRF tokens and proxy or CDN cookies.
        private static bool LooksLikeSignedSession(string decodedValue)
            => decodedValue.StartsWith("s:", StringComparison.Ordinal);

        private static bool TryReadSetCookie(string setCookieHeader, string name, out string value)
        {
            value = string.Empty;
            var prefix = name + "=";
            if (!setCookieHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var raw = setCookieHeader.Substring(prefix.Length);
            var semi = raw.IndexOf(';');
            if (semi >= 0) raw = raw.Substring(0, semi);

            value = raw.Trim();
            return value.Length > 0;
        }

        // Reads the Seerr session cookie and its name from a response, decoding the value. We
        // check the Set-Cookie header first because CookieContainer.GetCookies can miss it for
        // IP-based hosts.
        //
        // The name is not fixed. Standard Seerr uses "connect.sid", but a rebranded deployment
        // can rename it. We take "connect.sid" when present, otherwise the cookie carrying an
        // express-session signed value, so any name works.
        private static (string? Name, string? Value) ReadSessionCookie(HttpResponseMessage response, CookieContainer jar, string seerrUrl)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                var headers = new List<string>(setCookieHeaders);

                // Try the express-session default name first.
                foreach (var header in headers)
                {
                    if (TryReadSetCookie(header, "connect.sid", out var value))
                        return ("connect.sid", Uri.UnescapeDataString(value));
                }

                // Otherwise take the first cookie with a signed value that is not a CSRF cookie.
                foreach (var header in headers)
                {
                    var eq = header.IndexOf('=');
                    if (eq <= 0) continue;

                    var name = header.Substring(0, eq).Trim();
                    if (IsNonSessionCookie(name)) continue;

                    if (!TryReadSetCookie(header, name, out var rawValue)) continue;

                    var value = Uri.UnescapeDataString(rawValue);
                    if (LooksLikeSignedSession(value))
                        return (name, value);
                }
            }

            // Fall back to the cookie jar. Default name first, then any cookie with a signed value.
            var jarCookies = jar.GetCookies(new Uri(seerrUrl));
            var known = jarCookies["connect.sid"];
            if (known != null && !string.IsNullOrEmpty(known.Value))
                return ("connect.sid", Uri.UnescapeDataString(known.Value));

            foreach (Cookie cookie in jarCookies)
            {
                if (IsNonSessionCookie(cookie.Name)) continue;
                if (string.IsNullOrEmpty(cookie.Value)) continue;

                var value = Uri.UnescapeDataString(cookie.Value);
                if (LooksLikeSignedSession(value))
                    return (cookie.Name, value);
            }

            return (null, null);
        }

        // Maps a non-API upstream response (a redirect or an HTML proxy login/error page)
        // to a structured 502 the clients can surface, or null to forward the response as-is.
        private SeerrProxyResponse? ClassifyUpstreamFailure(HttpResponseMessage response, byte[] body, string? contentType)
        {
            var status = (int)response.StatusCode;
            if (status >= 300 && status < 400)
            {
                var location = response.Headers.Location?.ToString();
                _logger.Warn("Seerr returned a redirect (" + response.StatusCode + " -> " + (location ?? "?") + ")");
                return ErrorResponse(502,
                    "Seerr redirected the request. Verify the Seerr URL in Moonfin matches its public address "
                    + "(scheme + sub-path), or bypass any reverse-proxy auth for the media server.",
                    "UPSTREAM_REDIRECT");
            }

            if (LooksLikeHtml(contentType, body))
            {
                _logger.Warn("Seerr returned an HTML response (" + (contentType ?? "?") + ")");
                return ErrorResponse(502,
                    "Seerr returned an HTML page instead of API data. A reverse proxy in front of Seerr is likely "
                    + "intercepting requests. Bypass its auth for the media server.",
                    "UPSTREAM_HTML");
            }

            return null;
        }

        // Content-type or a sniff of the leading bytes. Some proxies omit the content-type.
        private static bool LooksLikeHtml(string? contentType, byte[]? body)
        {
            if (!string.IsNullOrEmpty(contentType) &&
                contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (body == null || body.Length == 0) return false;
            var probeLen = Math.Min(body.Length, 256);
            var head = Encoding.UTF8.GetString(body, 0, probeLen).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<SeerrAuthResult?> AuthenticateAsync(Guid userId, string username, string? password, string? authType = null)
        {
            var config = Plugin.Instance?.Configuration;
            var seerrUrl = config?.GetEffectiveSeerrUrl();
            if (string.IsNullOrEmpty(seerrUrl)) { _logger.Error("Seerr URL not configured", 0); return null; }

            try
            {
                var cookieContainer = new CookieContainer();
                var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true, AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Moonfin-Server");

                var isLocal = string.Equals(authType, "local", StringComparison.OrdinalIgnoreCase);
                var authEndpoint = isLocal ? $"{seerrUrl}/api/v1/auth/local" : $"{seerrUrl}/api/v1/auth/jellyfin";
                object authPayload = isLocal
                    ? (object)new { email = username, password = password }
                    : new { username = username, password = password ?? string.Empty };

                var content = new StringContent(JsonSerializer.Serialize(authPayload), Encoding.UTF8, "application/json");
                var csrfToken = await FetchCsrfTokenAsync(client, seerrUrl, cookieContainer).ConfigureAwait(false);

                var request = new HttpRequestMessage(HttpMethod.Post, authEndpoint) { Content = content };
                var originValue = new Uri(seerrUrl).GetLeftPart(UriPartial.Authority);
                request.Headers.TryAddWithoutValidation("Origin", originValue);
                request.Headers.TryAddWithoutValidation("Referer", seerrUrl.TrimEnd('/') + "/");
                if (!string.IsNullOrEmpty(csrfToken))
                {
                    request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrfToken);
                    request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
                }

                var response = await client.SendAsync(request).ConfigureAwait(false);

                var authStatus = (int)response.StatusCode;
                if (authStatus >= 300 && authStatus < 400)
                {
                    _logger.Warn("Seerr auth redirected (" + response.StatusCode + " -> "
                        + (response.Headers.Location?.ToString() ?? "?") + ") for user " + username
                        + ". Check the Seerr URL configured in Moonfin matches the public address (scheme + sub-path).");
                    return new SeerrAuthResult
                    {
                        Success = false,
                        Error = "Seerr redirected the login request. Verify the Seerr URL in Moonfin matches its public address (https and any sub-path)."
                    };
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _logger.Warn("Seerr auth failed for user " + username + ": " + response.StatusCode + " - " + errorBody);
                    return new SeerrAuthResult
                    {
                        Success = false,
                        Error = response.StatusCode == HttpStatusCode.Forbidden
                            ? "Access denied. Make sure you have a Seerr account."
                            : $"Authentication failed: {response.StatusCode}"
                    };
                }

                var (sessionCookieName, sessionCookie) = ReadSessionCookie(response, cookieContainer, seerrUrl);

                if (string.IsNullOrEmpty(sessionCookie))
                {
                    return new SeerrAuthResult { Success = false, Error = "No session cookie received from Seerr" };
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var userInfo = JsonSerializer.Deserialize<JsonElement>(responseBody);

                var session = new SeerrSession
                {
                    JellyfinUserId = userId,
                    SessionCookie = sessionCookie,
                    SessionCookieName = sessionCookieName ?? "connect.sid",
                    SeerrUserId = userInfo.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0,
                    Username = username,
                    DisplayName = userInfo.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : username,
                    Avatar = userInfo.TryGetProperty("avatar", out var avProp) ? avProp.GetString() : null,
                    Permissions = userInfo.TryGetProperty("permissions", out var permProp) ? permProp.GetInt32() : 0,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastValidated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                await SaveSessionAsync(session).ConfigureAwait(false);

                return new SeerrAuthResult
                {
                    Success = true,
                    SeerrUserId = session.SeerrUserId,
                    DisplayName = session.DisplayName,
                    Avatar = session.Avatar,
                    Permissions = session.Permissions
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.ErrorException("Failed to connect to Seerr at " + seerrUrl, ex);
                return new SeerrAuthResult { Success = false, Error = $"Cannot reach Seerr: {ex.Message}" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Unexpected error during Seerr auth for user " + username, ex);
                return new SeerrAuthResult { Success = false, Error = "An unexpected error occurred" };
            }
        }

        public async Task<SeerrSession?> GetSessionAsync(Guid userId, bool validate = false)
        {
            var session = await LoadSessionAsync(userId).ConfigureAwait(false);
            if (session == null || string.IsNullOrEmpty(session.SessionCookie)) return null;

            if (validate)
            {
                var isValid = await ValidateSessionAsync(session).ConfigureAwait(false);
                if (!isValid)
                {
                    await ClearSessionAsync(userId).ConfigureAwait(false);
                    return null;
                }
            }
            return session;
        }

        private async Task<bool> ValidateSessionAsync(SeerrSession session)
        {
            var seerrUrl = Plugin.Instance?.Configuration?.GetEffectiveSeerrUrl();
            if (string.IsNullOrEmpty(seerrUrl)) return false;
            try
            {
                var cookieContainer = new CookieContainer();
                cookieContainer.Add(new Uri(seerrUrl), new Cookie(session.SessionCookieName, session.SessionCookie));
                var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true, AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var response = await client.GetAsync($"{seerrUrl}/api/v1/auth/me").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    // A proxy login page can return 200 HTML. That is not a valid session.
                    var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (LooksLikeHtml(response.Content.Headers.ContentType?.ToString(), body))
                    {
                        _logger.Warn("Seerr validate returned HTML for user " + session.JellyfinUserId + "; treating session as invalid");
                        return false;
                    }

                    await CheckForRotatedCookieAsync(session, response, cookieContainer, seerrUrl).ConfigureAwait(false);
                    session.LastValidated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await SaveSessionAsync(session).ConfigureAwait(false);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to validate Seerr session for user " + session.JellyfinUserId + ": " + ex.Message);
                return false;
            }
        }

        private async Task CheckForRotatedCookieAsync(SeerrSession session, HttpResponseMessage response, CookieContainer cookieContainer, string seerrUrl)
        {
            var (updatedName, updatedCookie) = ReadSessionCookie(response, cookieContainer, seerrUrl);
            if (!string.IsNullOrEmpty(updatedCookie) && updatedCookie != session.SessionCookie)
            {
                session.SessionCookie = updatedCookie;
                if (!string.IsNullOrEmpty(updatedName))
                    session.SessionCookieName = updatedName;
                await SaveSessionAsync(session).ConfigureAwait(false);
            }
        }

        public async Task ClearSessionAsync(Guid userId)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Takes the backup with it, otherwise recovery could bring back a session the
                // user just signed out of.
                AtomicFile.DeleteWithSidecars(GetSessionPath(userId));
            }
            finally { _lock.Release(); }
        }

        // Shared send path for the two public proxy entry points. Flags carry the per-caller
        // differences so each keeps its exact behavior: queryString appending, whether to send
        // Origin/Referer + X-XSRF-TOKEN (vs X-CSRF-Token only), and whether a 401/403 evicts the
        // owning user's session. Network exceptions propagate to the caller's own catch.
        private async Task<SeerrProxyResponse> SendProxyCoreAsync(
            string seerrUrl,
            SeerrSession session,
            HttpMethod method,
            string path,
            string? queryString,
            byte[]? body,
            string? contentType,
            bool sendOriginReferer,
            Guid? evictUserIdOn401,
            CancellationToken ct)
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri(seerrUrl), new Cookie(session.SessionCookieName, session.SessionCookie));
            var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var targetUrl = $"{seerrUrl}/api/v1/{path.TrimStart('/')}";
            if (!string.IsNullOrEmpty(queryString))
                targetUrl += "?" + queryString.TrimStart('?');

            var request = new HttpRequestMessage(method, targetUrl);

            if (method != HttpMethod.Get && method != HttpMethod.Head)
            {
                var csrf = await FetchCsrfTokenAsync(client, seerrUrl, cookieContainer).ConfigureAwait(false);
                if (sendOriginReferer)
                {
                    var originValue = new Uri(seerrUrl).GetLeftPart(UriPartial.Authority);
                    request.Headers.TryAddWithoutValidation("Origin", originValue);
                    request.Headers.TryAddWithoutValidation("Referer", seerrUrl.TrimEnd('/') + "/");
                    if (!string.IsNullOrEmpty(csrf))
                    {
                        request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrf);
                        request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
                    }
                }
                else if (!string.IsNullOrEmpty(csrf))
                {
                    request.Headers.Add("X-CSRF-Token", csrf);
                }
            }

            if (body != null && body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
            }

            var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            var upstreamFailure = ClassifyUpstreamFailure(response, responseBody, responseContentType);
            if (upstreamFailure != null)
                return upstreamFailure;

            if (evictUserIdOn401.HasValue &&
                (response.StatusCode == HttpStatusCode.Unauthorized))
            {
                await ClearSessionAsync(evictUserIdOn401.Value).ConfigureAwait(false);
                return ErrorResponse(401, "Seerr session expired", "SESSION_EXPIRED");
            }

            if (response.IsSuccessStatusCode)
                await CheckForRotatedCookieAsync(session, response, cookieContainer, seerrUrl).ConfigureAwait(false);

            return new SeerrProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = responseBody,
                ContentType = responseContentType
            };
        }

        public async Task<SeerrProxyResponse> ProxyRequestAsync(Guid userId, HttpMethod method, string path,
            string? queryString = null, byte[]? body = null, string? contentType = null)
        {
            var seerrUrl = Plugin.Instance?.Configuration?.GetEffectiveSeerrUrl();
            if (string.IsNullOrEmpty(seerrUrl))
                return ErrorResponse(503, "Seerr URL not configured");

            var session = await LoadSessionAsync(userId).ConfigureAwait(false);
            if (session == null)
                return ErrorResponse(401, "Not authenticated with Seerr", "NO_SESSION");

            try
            {
                return await SendProxyCoreAsync(seerrUrl, session, method, path, queryString, body, contentType,
                    sendOriginReferer: false, evictUserIdOn401: userId, ct: default).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.ErrorException("Failed to proxy request to Seerr: " + path, ex);
                return ErrorResponse(502, "Cannot reach Seerr: " + ex.Message);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Unexpected error proxying to Seerr: " + path, ex);
                return ErrorResponse(500, "Internal proxy error");
            }
        }

        private static SeerrProxyResponse ErrorResponse(int status, string error, string? code = null)
        {
            var body = code != null
                ? JsonSerializer.Serialize(new { error, code })
                : JsonSerializer.Serialize(new { error });
            return new SeerrProxyResponse
            {
                StatusCode = status,
                Body = Encoding.UTF8.GetBytes(body),
                ContentType = "application/json"
            };
        }

        /// <summary>Enumerates all stored Seerr sessions.</summary>
        // Seerr owner is user 1, and permissions bit 2 is the admin flag.
        private const int OwnerSeerrUserId = 1;
        private const int AdminBit = 2;

        /// <summary>
        /// Fetches the Radarr or Sonarr upcoming calendar server-side so the API key never reaches the
        /// client. The arr connection is read from an admin Seerr session and the arr is called directly,
        /// which also lets remote clients see the calendar when the arr host is only on the local network.
        /// </summary>
        public async Task<SeerrProxyResponse> GetArrCalendarAsync(string service, string? start, string? end)
        {
            var isSonarr = string.Equals(service, "sonarr", StringComparison.OrdinalIgnoreCase);
            var settingsPath = isSonarr ? "settings/sonarr" : "settings/radarr";

            var adminSession = EnumerateSessions().FirstOrDefault(
                s => s.SeerrUserId == OwnerSeerrUserId || (s.Permissions & AdminBit) != 0);
            if (adminSession == null)
            {
                return CalendarError(503, "No admin Seerr session is available to read the arr settings");
            }

            var settings = await ProxyRequestAsync(adminSession.JellyfinUserId, HttpMethod.Get, settingsPath).ConfigureAwait(false);
            if (settings.Body == null || settings.StatusCode != 200)
            {
                return settings;
            }

            JsonElement server;
            try
            {
                using var doc = JsonDocument.Parse(settings.Body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    return CalendarError(404, "No " + service + " server is configured in Seerr");
                }
                server = doc.RootElement[0].Clone();
            }
            catch (JsonException)
            {
                return CalendarError(502, "Could not read the arr settings from Seerr");
            }

            var hostname = server.TryGetProperty("hostname", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            if (hostname.Length == 0)
            {
                return CalendarError(404, "No " + service + " host is configured in Seerr");
            }

            var port = server.TryGetProperty("port", out var p) && p.TryGetInt32(out var portValue)
                ? portValue
                : (isSonarr ? 8989 : 7878);
            var useSsl = server.TryGetProperty("useSsl", out var ssl) && ssl.ValueKind == JsonValueKind.True;
            var baseUrl = (server.TryGetProperty("baseUrl", out var b) ? b.GetString() : null)?.TrimEnd('/') ?? string.Empty;
            var apiKey = server.TryGetProperty("apiKey", out var k) ? k.GetString() ?? string.Empty : string.Empty;

            var protocol = useSsl ? "https" : "http";
            var startDate = string.IsNullOrWhiteSpace(start) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : start;
            var endDate = string.IsNullOrWhiteSpace(end) ? DateTime.UtcNow.AddDays(90).ToString("yyyy-MM-dd") : end;
            var query = "start=" + Uri.EscapeDataString(startDate) + "&end=" + Uri.EscapeDataString(endDate);
            if (isSonarr)
            {
                query += "&includeSeries=true";
            }
            var url = protocol + "://" + hostname + ":" + port + baseUrl + "/api/v3/calendar?" + query;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Api-Key", apiKey);
                using var arrResponse = await client.SendAsync(request).ConfigureAwait(false);
                var arrBody = await arrResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return new SeerrProxyResponse
                {
                    StatusCode = (int)arrResponse.StatusCode,
                    Body = arrBody,
                    ContentType = arrResponse.Content.Headers.ContentType?.ToString() ?? "application/json"
                };
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to fetch " + service + " calendar: " + ex.Message);
                return CalendarError(502, "Could not reach the " + service + " server");
            }
        }

        private static SeerrProxyResponse CalendarError(int statusCode, string message) => new SeerrProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.SerializeToUtf8Bytes(new { error = message }),
            ContentType = "application/json"
        };

        public IEnumerable<SeerrSession> EnumerateSessions()
        {
            if (!Directory.Exists(_sessionsPath)) yield break;

            foreach (var path in Directory.EnumerateFiles(_sessionsPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                SeerrSession? session = null;
                try
                {
                    // Recover from the backup so one bad file doesn't drop a user from the results.
                    session = AtomicFile.ReadWithRecovery(
                        path,
                        json => JsonSerializer.Deserialize<SeerrSession>(json, _jsonOptions));
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to read Seerr session file " + path + ": " + ex.Message);
                }

                if (session != null) yield return session;
            }
        }

        /// <summary>Maps a Seerr internal user id back to the Emby user id that owns that session.</summary>
        public Guid? GetJellyfinUserForSeerrUser(int seerrUserId)
        {
            foreach (var session in EnumerateSessions())
                if (session.SeerrUserId == seerrUserId) return session.JellyfinUserId;
            return null;
        }

        /// <summary>Maps a Seerr username back to the Emby user id that owns that session (case-insensitive).</summary>
        public Guid? GetJellyfinUserForSeerrUsername(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            foreach (var session in EnumerateSessions())
                if (string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase))
                    return session.JellyfinUserId;
            return null;
        }

        /// <summary>
        /// Makes an authenticated request to Seerr using a specific stored session's cookie,
        /// rather than resolving the session from an Emby user id. Used by server-side jobs
        /// that already hold an admin session (e.g. webhook auto-provisioning).
        /// </summary>
        public async Task<SeerrProxyResponse> RequestWithSessionAsync(
            SeerrSession session,
            HttpMethod method,
            string path,
            byte[]? body = null,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            var seerrUrl = Plugin.Instance?.Configuration?.GetEffectiveSeerrUrl();
            if (string.IsNullOrEmpty(seerrUrl))
                return ErrorResponse(503, "Seerr URL not configured");

            if (string.IsNullOrEmpty(session.SessionCookie))
                return ErrorResponse(401, "Session has no cookie", "NO_SESSION");

            try
            {
                return await SendProxyCoreAsync(seerrUrl, session, method, path, queryString: null, body, contentType,
                    sendOriginReferer: true, evictUserIdOn401: null, ct: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed authenticated Seerr request: " + path, ex);
                return ErrorResponse(502, "Cannot reach Seerr: " + ex.Message);
            }
        }

        private async Task SaveSessionAsync(SeerrSession session)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureDirectory();
                var json = JsonSerializer.Serialize(session, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(GetSessionPath(session.JellyfinUserId), json)).ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        private async Task<SeerrSession?> LoadSessionAsync(Guid userId)
        {
            var path = GetSessionPath(userId);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => AtomicFile.ReadWithRecovery(
                    path,
                    json => JsonSerializer.Deserialize<SeerrSession>(json, _jsonOptions))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to load Seerr session for user " + userId, ex);
                return null;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// One-time healing sweep over stored Seerr sessions. A lost session only forces one
        /// re-login, so unparseable files are quarantined rather than repaired.
        /// </summary>
        public Task<HealSummary> HealDataFilesAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_sessionsPath))
            {
                return Task.FromResult(new HealSummary());
            }

            var dataPath = Path.GetDirectoryName(_sessionsPath) ?? _sessionsPath;
            return FileHealer.HealDirectoryAsync(
                _sessionsPath,
                Path.Combine(dataPath, "quarantine", "seerr-sessions"),
                _lock,
                text =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<SeerrSession>(text, _jsonOptions) != null;
                    }
                    catch
                    {
                        return false;
                    }
                },
                salvage: null,
                cancellationToken);
        }
    }

    public class SeerrSession
    {
        [JsonPropertyName("jellyfinUserId")] public Guid JellyfinUserId { get; set; }
        [JsonPropertyName("sessionCookie")] public string SessionCookie { get; set; } = string.Empty;
        [JsonPropertyName("sessionCookieName")] public string SessionCookieName { get; set; } = "connect.sid";
        [JsonPropertyName("seerrUserId")] public int SeerrUserId { get; set; }
        [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("avatar")] public string? Avatar { get; set; }
        [JsonPropertyName("permissions")] public int Permissions { get; set; }
        [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
        [JsonPropertyName("lastValidated")] public long LastValidated { get; set; }
    }

    public class SeerrAuthResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int SeerrUserId { get; set; }
        public string? DisplayName { get; set; }
        public string? Avatar { get; set; }
        public int Permissions { get; set; }
    }

    public class SeerrProxyResponse
    {
        public int StatusCode { get; set; }
        public byte[]? Body { get; set; }
        public string ContentType { get; set; } = "application/json";
    }
}
