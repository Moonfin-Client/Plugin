using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Manages per-user Seerr session cookies for SSO.
/// Sessions are stored server-side so any Moonfin client
/// can access Seerr through the Jellyfin plugin without re-authenticating.
/// </summary>
public class SeerrSessionService
{
    private readonly string _sessionsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<SeerrSessionService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public SeerrSessionService(
        ILogger<SeerrSessionService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

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
        if (!Directory.Exists(_sessionsPath))
        {
            Directory.CreateDirectory(_sessionsPath);
        }
    }

    private string GetSessionPath(Guid userId) =>
        Path.Combine(_sessionsPath, $"{userId}.json");

    // Seerr v3 enables CSRF via @dr.pogodin/csurf when network.csrfProtection is on.
    // Every response carries an httpOnly `_csrf` secret and a readable `XSRF-TOKEN`
    // token; a state-changing request must send the secret cookie plus the token in
    // a header. .NET's CookieContainer silently drops the cookies because they are
    // marked Secure (the secret is unusable over plain HTTP) and SameSite=Strict (mis-parsed)
    // so the secret never reaches the auth POST and Seerr replies "invalid csrf token".
    // We therefore read both cookies straight from the GET's Set-Cookie header and 
    // re-add them to the jar without the Secure flag so they are guaranteed to
    // ride along on the POST.
    private async Task<string?> FetchCsrfTokenAsync(HttpClient client, string seerrUrl, CookieContainer cookieContainer)
    {
        var baseUrl = seerrUrl.TrimEnd('/');
        var baseUri = new Uri(baseUrl);

        string? xsrfToken = null;
        string? csrfSecret = null;

        try
        {
            // /api/v1/auth/me returns 401 but still runs the global csurf middleware,
            // so it seeds both cookies without a redirect. A single probe avoids
            // rotating the secret between requests.
            using var response = await client.GetAsync(
                baseUrl + "/api/v1/auth/me",
                HttpCompletionOption.ResponseHeadersRead);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                foreach (var header in setCookieHeaders)
                {
                    if (TryReadSetCookie(header, "XSRF-TOKEN", out var token)) xsrfToken = token;
                    else if (TryReadSetCookie(header, "_csrf", out var secret)) csrfSecret = secret;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "CSRF prefetch failed for {Url} (non-fatal)", baseUrl);
        }

        // Fall back to whatever the CookieContainer did manage to capture.
        var jarCookies = cookieContainer.GetCookies(baseUri);
        xsrfToken ??= jarCookies["XSRF-TOKEN"]?.Value;
        csrfSecret ??= jarCookies["_csrf"]?.Value;

        if (!string.IsNullOrEmpty(csrfSecret))
            cookieContainer.Add(baseUri, new Cookie("_csrf", csrfSecret) { Secure = false });
        if (!string.IsNullOrEmpty(xsrfToken))
            cookieContainer.Add(baseUri, new Cookie("XSRF-TOKEN", xsrfToken) { Secure = false });

        return xsrfToken;
    }

    // Reads a single cookie value verbatim from a Set-Cookie header. The value is
    // sent back unchanged (csurf tokens are URL-safe), matching the browser.
    private static bool TryReadSetCookie(string setCookieHeader, string name, out string value)
    {
        value = string.Empty;
        var prefix = name + "=";
        if (!setCookieHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = setCookieHeader[prefix.Length..];
        var semi = raw.IndexOf(';');
        if (semi >= 0) raw = raw[..semi];

        value = raw.Trim();
        return value.Length > 0;
    }

    // CSRF cookies Seerr sets next to the session cookie. They are never the session
    // cookie, so we skip them when looking for a renamed one.
    private static readonly string[] NonSessionCookieNames = { "XSRF-TOKEN", "_csrf" };

    // express-session signs its cookie, so the decoded value starts with "s:". That tells
    // the real session cookie apart from CSRF tokens and proxy or CDN cookies.
    private static bool LooksLikeSignedSession(string decodedValue)
        => decodedValue.StartsWith("s:", StringComparison.Ordinal);

    // Reads the Seerr session cookie and its name from a response, decoding the value. We
    // check the Set-Cookie header first because CookieContainer.GetCookies can miss it for
    // IP-based hosts.
    //
    // The name is not fixed. Standard Jellyseerr and Overseerr use "connect.sid", but a
    // rebranded Seerr can rename it (SparkBox issues "sb.sid"). We take "connect.sid" when
    // it is there, otherwise the cookie carrying an express-session signed value, which
    // identifies the session without special-casing any one name.
    private static (string? Name, string? Value) ReadSessionCookie(HttpResponseMessage response, CookieContainer jar, string seerrUrl)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            var headers = setCookieHeaders.ToList();

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

                var name = header[..eq].Trim();
                if (NonSessionCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!TryReadSetCookie(header, name, out var rawValue))
                    continue;

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
            if (NonSessionCookieNames.Contains(cookie.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(cookie.Value))
                continue;

            var value = Uri.UnescapeDataString(cookie.Value);
            if (LooksLikeSignedSession(value))
                return (cookie.Name, value);
        }

        return (null, null);
    }

    // Maps a non-API upstream response (a redirect or an HTML proxy login/error page)
    // to a structured 502 the clients can surface, or null to forward the response as-is.
    private SeerrProxyResponse? ClassifyUpstreamFailure(
        HttpResponseMessage response, byte[] body, string? contentType)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location?.ToString();
            _logger.LogWarning("Seerr returned a redirect ({Status} -> {Location})", response.StatusCode, location);
            return new SeerrProxyResponse
            {
                StatusCode = 502,
                Body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    error = "Seerr redirected the request. Verify the Seerr URL in Moonfin matches its public " +
                            "address (scheme + sub-path), or bypass any reverse-proxy auth for the media server.",
                    code = "UPSTREAM_REDIRECT",
                    location
                }),
                ContentType = "application/json"
            };
        }

        if (LooksLikeHtml(contentType, body))
        {
            _logger.LogWarning("Seerr returned an HTML response ({ContentType})", contentType);
            return new SeerrProxyResponse
            {
                StatusCode = 502,
                Body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    error = "Seerr returned an HTML page instead of API data. A reverse proxy in front of Seerr " +
                            "is likely intercepting requests. Bypass its auth for the media server.",
                    code = "UPSTREAM_HTML"
                }),
                ContentType = "application/json"
            };
        }

        return null;
    }

    // Content-type or a sniff of the leading bytes; some proxies omit the content-type.
    private static bool LooksLikeHtml(string? contentType, byte[] body)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var probeLen = Math.Min(body.Length, 256);
        if (probeLen == 0) return false;

        var head = Encoding.UTF8.GetString(body, 0, probeLen)
            .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Authenticates a Jellyfin user with Seerr and stores the session.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="authType">Auth type: "jellyfin" (default) or "local" for a native Seerr account.</param>
    /// <returns>The authenticated Seerr user info, or null on failure.</returns>
    public async Task<SeerrAuthResult?> AuthenticateAsync(Guid userId, string username, string? password, string? authType = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();

        if (string.IsNullOrEmpty(seerrUrl))
        {
            _logger.LogError("Seerr URL not configured");
            return null;
        }

        try
        {
            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Moonfin-Server");

            var isLocal = string.Equals(authType, "local", StringComparison.OrdinalIgnoreCase);
            var authEndpoint = isLocal
                ? $"{seerrUrl}/api/v1/auth/local"
                : $"{seerrUrl}/api/v1/auth/jellyfin";

            var authPayload = isLocal
                ? (object)new { email = username, password = password }
                : new { username = username, password = password ?? string.Empty };

            var content = new StringContent(
                JsonSerializer.Serialize(authPayload),
                Encoding.UTF8,
                "application/json");

            var csrfToken = await FetchCsrfTokenAsync(client, seerrUrl, cookieContainer);

            var request = new HttpRequestMessage(HttpMethod.Post, authEndpoint) { Content = content };
            var originValue = new Uri(seerrUrl).GetLeftPart(UriPartial.Authority);
            request.Headers.TryAddWithoutValidation("Origin", originValue);
            request.Headers.TryAddWithoutValidation("Referer", seerrUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(csrfToken))
            {
                request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrfToken);
                request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
            }

            var response = await client.SendAsync(request);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                _logger.LogWarning(
                    "Seerr auth redirected ({Status} -> {Location}) for user {Username}. " +
                    "Check the Seerr URL configured in Moonfin matches the public address (scheme + sub-path).",
                    response.StatusCode, response.Headers.Location?.ToString(), username);
                return new SeerrAuthResult
                {
                    Success = false,
                    Error = "Seerr redirected the login request. Verify the Seerr URL in Moonfin matches its public address (https and any sub-path)."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Seerr auth failed for user {Username}: {Status} - {Error}",
                    username, response.StatusCode, errorBody);
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
                _logger.LogWarning("No session cookie received from Seerr for user {Username}", username);
                return new SeerrAuthResult
                {
                    Success = false,
                    Error = "No session cookie received from Seerr"
                };
            }

            // Parse the user response
            var responseBody = await response.Content.ReadAsStringAsync();
            var userInfo = JsonSerializer.Deserialize<JsonElement>(responseBody);

            // Store the session
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

            await SaveSessionAsync(session);

            _logger.LogInformation("Seerr SSO session created for user {Username} (Jellyfin: {UserId})",
                username, userId);

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
            _logger.LogError(ex, "Failed to connect to Seerr at {Url}", seerrUrl);
            return new SeerrAuthResult
            {
                Success = false,
                Error = $"Cannot reach Seerr: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Seerr auth for user {Username}", username);
            return new SeerrAuthResult
            {
                Success = false,
                Error = "An unexpected error occurred"
            };
        }
    }

    /// <summary>
    /// Gets the stored session for a user, optionally validating it.
    /// </summary>
    public async Task<SeerrSession?> GetSessionAsync(Guid userId, bool validate = false)
    {
        var session = await LoadSessionAsync(userId);
        if (session == null || string.IsNullOrEmpty(session.SessionCookie))
        {
            if (session != null)
            {
                _logger.LogWarning("Seerr session for user {UserId} has empty cookie, treating as invalid", userId);
            }
            return null;
        }

        if (validate)
        {
            var isValid = await ValidateSessionAsync(session);
            if (!isValid)
            {
                _logger.LogInformation("Seerr session expired for user {UserId}, removing", userId);
                await ClearSessionAsync(userId);
                return null;
            }
        }

        return session;
    }

    /// <summary>
    /// Validates a stored session by calling Seerr's /auth/me endpoint.
    /// </summary>
    private async Task<bool> ValidateSessionAsync(SeerrSession session)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();

        if (string.IsNullOrEmpty(seerrUrl)) return false;

        try
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri(seerrUrl), new Cookie(session.SessionCookieName, session.SessionCookie));

            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetAsync($"{seerrUrl}/api/v1/auth/me");

            if (response.IsSuccessStatusCode)
            {
                // A proxy login page can return 200 HTML; that is not a valid session.
                var body = await response.Content.ReadAsByteArrayAsync();
                if (LooksLikeHtml(response.Content.Headers.ContentType?.ToString(), body))
                {
                    _logger.LogWarning("Seerr validate returned HTML for user {UserId}; treating session as invalid", session.JellyfinUserId);
                    return false;
                }

                await CheckForRotatedCookieAsync(session, response, cookieContainer, seerrUrl);

                // Update last validated timestamp
                session.LastValidated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await SaveSessionAsync(session);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate Seerr session for user {UserId}", session.JellyfinUserId);
            return false;
        }
    }

    /// <summary>
    /// Checks if Seerr rotated the session cookie and updates the session if so.
    /// Express.js with rolling sessions may issue a new cookie on every response.
    /// </summary>
    private async Task CheckForRotatedCookieAsync(
        SeerrSession session,
        HttpResponseMessage response,
        CookieContainer cookieContainer,
        string seerrUrl)
    {
        var (updatedName, updatedCookie) = ReadSessionCookie(response, cookieContainer, seerrUrl);
        if (!string.IsNullOrEmpty(updatedCookie) && updatedCookie != session.SessionCookie)
        {
            session.SessionCookie = updatedCookie;
            if (!string.IsNullOrEmpty(updatedName))
            {
                session.SessionCookieName = updatedName;
            }
            await SaveSessionAsync(session);
        }
    }

    public async Task ClearSessionAsync(Guid userId)
    {
        await _lock.WaitAsync();
        try
        {
            var path = GetSessionPath(userId);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Seerr session cleared for user {UserId}", userId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Proxies an HTTP request to Seerr with the user's stored session cookie.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">API path (e.g., "auth/me", "request", "search").</param>
    /// <param name="queryString">Optional query string.</param>
    /// <param name="body">Optional request body.</param>
    /// <param name="contentType">Content type of the body.</param>
    /// <returns>The proxied response.</returns>
    public async Task<SeerrProxyResponse> ProxyRequestAsync(
        Guid userId,
        HttpMethod method,
        string path,
        string? queryString = null,
        byte[]? body = null,
        string? contentType = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();

        if (string.IsNullOrEmpty(seerrUrl))
        {
            return new SeerrProxyResponse
            {
                StatusCode = 503,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Seerr URL not configured" }),
                ContentType = "application/json"
            };
        }

        var session = await LoadSessionAsync(userId);
        if (session == null)
        {
            return new SeerrProxyResponse
            {
                StatusCode = 401,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Not authenticated with Seerr", code = "NO_SESSION" }),
                ContentType = "application/json"
            };
        }

        try
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri(seerrUrl), new Cookie(session.SessionCookieName, session.SessionCookie));

            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);

            // Build the target URL
            var targetUrl = $"{seerrUrl}/api/v1/{path.TrimStart('/')}";
            if (!string.IsNullOrEmpty(queryString))
            {
                targetUrl += $"?{queryString.TrimStart('?')}";
            }

            var request = new HttpRequestMessage(method, targetUrl);

            if (method != HttpMethod.Get && method != HttpMethod.Head)
            {
                var csrfToken = await FetchCsrfTokenAsync(client, seerrUrl, cookieContainer);
                if (!string.IsNullOrEmpty(csrfToken))
                {
                    request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrfToken);
                    request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
                }
            }

            if (body != null && body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    contentType ?? "application/json");
            }

            var response = await client.SendAsync(request);

            var responseBody = await response.Content.ReadAsByteArrayAsync();
            var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            var upstreamFailure = ClassifyUpstreamFailure(response, responseBody, responseContentType);
            if (upstreamFailure != null)
            {
                return upstreamFailure;
            }

            // If auth expired, clear session
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogInformation("Seerr session expired for user {UserId}", userId);
                await ClearSessionAsync(userId);

                return new SeerrProxyResponse
                {
                    StatusCode = 401,
                    Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Seerr session expired", code = "SESSION_EXPIRED" }),
                    ContentType = "application/json"
                };
            }

            if (response.IsSuccessStatusCode)
            {
                await CheckForRotatedCookieAsync(session, response, cookieContainer, seerrUrl);
            }

            return new SeerrProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = responseBody,
                ContentType = responseContentType
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to proxy request to Seerr: {Path}", path);
            return new SeerrProxyResponse
            {
                StatusCode = 502,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = $"Cannot reach Seerr: {ex.Message}" }),
                ContentType = "application/json"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error proxying to Seerr: {Path}", path);
            return new SeerrProxyResponse
            {
                StatusCode = 500,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Internal proxy error" }),
                ContentType = "application/json"
            };
        }
    }

    /// <summary>
    /// Enumerates all stored Seerr sessions.
    /// </summary>
    public IEnumerable<SeerrSession> EnumerateSessions()
    {
        if (!Directory.Exists(_sessionsPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_sessionsPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            SeerrSession? session = null;
            try
            {
                var json = File.ReadAllText(path);
                session = JsonSerializer.Deserialize<SeerrSession>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Seerr session file {Path}", path);
            }

            if (session != null)
            {
                yield return session;
            }
        }
    }

    /// <summary>
    /// Maps a Seerr internal user ID back to the Jellyfin user ID that owns that session.
    /// </summary>
    public Guid? GetJellyfinUserForSeerrUser(int seerrUserId)
    {
        foreach (var session in EnumerateSessions())
        {
            if (session.SeerrUserId == seerrUserId)
            {
                return session.JellyfinUserId;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a Seerr username back to the Jellyfin user ID that owns that session (case-insensitive).
    /// </summary>
    public Guid? GetJellyfinUserForSeerrUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        foreach (var session in EnumerateSessions())
        {
            if (string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                return session.JellyfinUserId;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes an authenticated request to Seerr using a specific stored session's cookie,
    /// rather than resolving the session from a Jellyfin user ID. Used by server-side jobs
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
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();

        if (string.IsNullOrEmpty(seerrUrl))
        {
            return new SeerrProxyResponse
            {
                StatusCode = 503,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Seerr URL not configured" }),
                ContentType = "application/json"
            };
        }

        if (string.IsNullOrEmpty(session.SessionCookie))
        {
            return new SeerrProxyResponse
            {
                StatusCode = 401,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Session has no cookie", code = "NO_SESSION" }),
                ContentType = "application/json"
            };
        }

        try
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri(seerrUrl), new Cookie(session.SessionCookieName, session.SessionCookie));

            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);

            var targetUrl = $"{seerrUrl}/api/v1/{path.TrimStart('/')}";
            var request = new HttpRequestMessage(method, targetUrl);

            if (method != HttpMethod.Get && method != HttpMethod.Head)
            {
                var csrfToken = await FetchCsrfTokenAsync(client, seerrUrl, cookieContainer);
                var originValue = new Uri(seerrUrl).GetLeftPart(UriPartial.Authority);
                request.Headers.TryAddWithoutValidation("Origin", originValue);
                request.Headers.TryAddWithoutValidation("Referer", seerrUrl.TrimEnd('/') + "/");
                if (!string.IsNullOrEmpty(csrfToken))
                {
                    request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrfToken);
                    request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
                }
            }

            if (body != null && body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    contentType ?? "application/json");
            }

            var response = await client.SendAsync(request, cancellationToken);

            var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            var upstreamFailure = ClassifyUpstreamFailure(response, responseBody, responseContentType);
            if (upstreamFailure != null)
            {
                return upstreamFailure;
            }

            if (response.IsSuccessStatusCode)
            {
                await CheckForRotatedCookieAsync(session, response, cookieContainer, seerrUrl);
            }

            return new SeerrProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = responseBody,
                ContentType = responseContentType
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed authenticated Seerr request: {Path}", path);
            return new SeerrProxyResponse
            {
                StatusCode = 502,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = $"Cannot reach Seerr: {ex.Message}" }),
                ContentType = "application/json"
            };
        }
    }

    private async Task SaveSessionAsync(SeerrSession session)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureDirectory();
            var json = JsonSerializer.Serialize(session, _jsonOptions);
            await File.WriteAllTextAsync(GetSessionPath(session.JellyfinUserId), json);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SeerrSession?> LoadSessionAsync(Guid userId)
    {
        var path = GetSessionPath(userId);
        if (!File.Exists(path)) return null;

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<SeerrSession>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Seerr session for user {UserId}", userId);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Stored Seerr session data for a Jellyfin user.
/// </summary>
public class SeerrSession
{
    /// <summary>The Jellyfin user ID this session belongs to.</summary>
    [JsonPropertyName("jellyfinUserId")]
    public Guid JellyfinUserId { get; set; }

    /// <summary>The Seerr session cookie value.</summary>
    [JsonPropertyName("sessionCookie")]
    public string SessionCookie { get; set; } = string.Empty;

    /// <summary>
    /// Name of the session cookie Seerr issues. Standard Jellyseerr and Overseerr use
    /// "connect.sid", but rebranded editions can rename it (SparkBox issues "sb.sid").
    /// Defaults to "connect.sid" so existing stored sessions keep working.
    /// </summary>
    [JsonPropertyName("sessionCookieName")]
    public string SessionCookieName { get; set; } = "connect.sid";

    /// <summary>The Seerr internal user ID.</summary>
    [JsonPropertyName("seerrUserId")]
    public int SeerrUserId { get; set; }

    /// <summary>The username used to authenticate.</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Display name from Seerr.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Avatar URL from Seerr.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    /// <summary>Seerr permission bitmask.</summary>
    [JsonPropertyName("permissions")]
    public int Permissions { get; set; }

    /// <summary>When the session was created (unix ms).</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    /// <summary>When the session was last validated (unix ms).</summary>
    [JsonPropertyName("lastValidated")]
    public long LastValidated { get; set; }
}

/// <summary>
/// Result of a Seerr authentication attempt.
/// </summary>
public class SeerrAuthResult
{
    /// <summary>Whether authentication succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Error message if auth failed.</summary>
    public string? Error { get; set; }

    /// <summary>Seerr user ID if successful.</summary>
    public int SeerrUserId { get; set; }

    /// <summary>Display name from Seerr.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Avatar URL.</summary>
    public string? Avatar { get; set; }

    /// <summary>Seerr permission bitmask.</summary>
    public int Permissions { get; set; }
}

/// <summary>
/// Response from a proxied Seerr request.
/// </summary>
public class SeerrProxyResponse
{
    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; set; }

    /// <summary>Response body bytes.</summary>
    public byte[]? Body { get; set; }

    /// <summary>Response content type.</summary>
    public string ContentType { get; set; } = "application/json";
}
