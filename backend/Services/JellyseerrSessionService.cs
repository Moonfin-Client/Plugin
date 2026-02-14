using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Manages per-user Jellyseerr session cookies for SSO.
/// Sessions are stored server-side so any Moonfin client
/// can access Jellyseerr through the Jellyfin plugin without re-authenticating.
/// </summary>
public class JellyseerrSessionService
{
    private readonly string _sessionsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<JellyseerrSessionService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public JellyseerrSessionService(
        ILogger<JellyseerrSessionService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        _sessionsPath = Path.Combine(dataPath, "jellyseerr-sessions");

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

    /// <summary>
    /// Authenticates a Jellyfin user with Jellyseerr and stores the session.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <param name="authType">Auth type: "jellyfin" (default) or "local" for a native Jellyseerr account.</param>
    /// <returns>The authenticated Jellyseerr user info, or null on failure.</returns>
    public async Task<JellyseerrAuthResult?> AuthenticateAsync(Guid userId, string username, string password, string? authType = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();

        if (string.IsNullOrEmpty(jellyseerrUrl))
        {
            _logger.LogError("Jellyseerr URL not configured");
            return null;
        }

        try
        {
            // Use a handler that captures cookies
            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);

            var isLocal = string.Equals(authType, "local", StringComparison.OrdinalIgnoreCase);
            var authEndpoint = isLocal
                ? $"{jellyseerrUrl}/api/v1/auth/local"
                : $"{jellyseerrUrl}/api/v1/auth/jellyfin";

            var authPayload = isLocal
                ? (object)new { email = username, password = password }
                : new { username = username, password = password };

            var content = new StringContent(
                JsonSerializer.Serialize(authPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(authEndpoint, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Jellyseerr auth failed for user {Username}: {Status} - {Error}",
                    username, response.StatusCode, errorBody);
                return new JellyseerrAuthResult
                {
                    Success = false,
                    Error = response.StatusCode == HttpStatusCode.Forbidden
                        ? "Access denied. Make sure you have a Jellyseerr account."
                        : $"Authentication failed: {response.StatusCode}"
                };
            }

            // Extract session cookie
            // CookieContainer.GetCookies() can fail with IP-based URLs, so
            // fall back to parsing the Set-Cookie header directly.
            var cookies = cookieContainer.GetCookies(new Uri(jellyseerrUrl));
            var sessionCookie = cookies["connect.sid"]?.Value;

            if (string.IsNullOrEmpty(sessionCookie) &&
                response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                foreach (var header in setCookieHeaders)
                {
                    if (header.StartsWith("connect.sid=", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = header.Substring("connect.sid=".Length);
                        var semicolonIdx = value.IndexOf(';');
                        if (semicolonIdx > 0)
                        {
                            value = value.Substring(0, semicolonIdx);
                        }
                        sessionCookie = Uri.UnescapeDataString(value);
                        _logger.LogInformation("Extracted connect.sid from Set-Cookie header (CookieContainer fallback)");
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(sessionCookie))
            {
                _logger.LogWarning("No session cookie received from Jellyseerr for user {Username}", username);
                return new JellyseerrAuthResult
                {
                    Success = false,
                    Error = "No session cookie received from Jellyseerr"
                };
            }

            // Parse the user response
            var responseBody = await response.Content.ReadAsStringAsync();
            var userInfo = JsonSerializer.Deserialize<JsonElement>(responseBody);

            // Store the session
            var session = new JellyseerrSession
            {
                JellyfinUserId = userId,
                SessionCookie = sessionCookie,
                JellyseerrUserId = userInfo.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0,
                Username = username,
                DisplayName = userInfo.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : username,
                Avatar = userInfo.TryGetProperty("avatar", out var avProp) ? avProp.GetString() : null,
                Permissions = userInfo.TryGetProperty("permissions", out var permProp) ? permProp.GetInt32() : 0,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastValidated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            await SaveSessionAsync(session);

            _logger.LogInformation("Jellyseerr SSO session created for user {Username} (Jellyfin: {UserId})",
                username, userId);

            return new JellyseerrAuthResult
            {
                Success = true,
                JellyseerrUserId = session.JellyseerrUserId,
                DisplayName = session.DisplayName,
                Avatar = session.Avatar,
                Permissions = session.Permissions
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Jellyseerr at {Url}", jellyseerrUrl);
            return new JellyseerrAuthResult
            {
                Success = false,
                Error = $"Cannot reach Jellyseerr: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Jellyseerr auth for user {Username}", username);
            return new JellyseerrAuthResult
            {
                Success = false,
                Error = "An unexpected error occurred"
            };
        }
    }

    /// <summary>
    /// Gets the stored session for a user, optionally validating it.
    /// </summary>
    public async Task<JellyseerrSession?> GetSessionAsync(Guid userId, bool validate = false)
    {
        var session = await LoadSessionAsync(userId);
        if (session == null || string.IsNullOrEmpty(session.SessionCookie))
        {
            if (session != null)
            {
                _logger.LogWarning("Jellyseerr session for user {UserId} has empty cookie, treating as invalid", userId);
            }
            return null;
        }

        if (validate)
        {
            var isValid = await ValidateSessionAsync(session);
            if (!isValid)
            {
                _logger.LogInformation("Jellyseerr session expired for user {UserId}, removing", userId);
                await ClearSessionAsync(userId);
                return null;
            }
        }

        return session;
    }

    /// <summary>
    /// Validates a stored session by calling Jellyseerr's /auth/me endpoint.
    /// </summary>
    private async Task<bool> ValidateSessionAsync(JellyseerrSession session)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();

        if (string.IsNullOrEmpty(jellyseerrUrl)) return false;

        try
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri(jellyseerrUrl), new Cookie("connect.sid", session.SessionCookie));

            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetAsync($"{jellyseerrUrl}/api/v1/auth/me");

            if (response.IsSuccessStatusCode)
            {
                // Check if Jellyseerr rotated the session cookie
                await CheckForRotatedCookieAsync(session, response, cookieContainer, jellyseerrUrl);

                // Update last validated timestamp
                session.LastValidated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await SaveSessionAsync(session);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate Jellyseerr session for user {UserId}", session.JellyfinUserId);
            return false;
        }
    }

    /// <summary>
    /// Checks if Jellyseerr rotated the connect.sid cookie and updates the session if so.
    /// Express.js with rolling sessions may issue a new cookie on every response.
    /// </summary>
    private async Task CheckForRotatedCookieAsync(
        JellyseerrSession session,
        HttpResponseMessage response,
        CookieContainer cookieContainer,
        string jellyseerrUrl)
    {
        var updatedCookie = cookieContainer.GetCookies(new Uri(jellyseerrUrl))["connect.sid"]?.Value;
        if (string.IsNullOrEmpty(updatedCookie) &&
            response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            foreach (var header in setCookieHeaders)
            {
                if (header.StartsWith("connect.sid=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = header.Substring("connect.sid=".Length);
                    var semicolonIdx = value.IndexOf(';');
                    if (semicolonIdx > 0) value = value.Substring(0, semicolonIdx);
                    updatedCookie = Uri.UnescapeDataString(value);
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(updatedCookie) && updatedCookie != session.SessionCookie)
        {
            _logger.LogInformation("Jellyseerr rotated session cookie for user {UserId}, updating", session.JellyfinUserId);
            session.SessionCookie = updatedCookie;
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
                _logger.LogInformation("Jellyseerr session cleared for user {UserId}", userId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Proxies an HTTP request to Jellyseerr with the user's stored session cookie.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">API path (e.g., "auth/me", "request", "search").</param>
    /// <param name="queryString">Optional query string.</param>
    /// <param name="body">Optional request body.</param>
    /// <param name="contentType">Content type of the body.</param>
    /// <returns>The proxied response.</returns>
    public async Task<JellyseerrProxyResponse> ProxyRequestAsync(
        Guid userId,
        HttpMethod method,
        string path,
        string? queryString = null,
        byte[]? body = null,
        string? contentType = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();

        if (string.IsNullOrEmpty(jellyseerrUrl))
        {
            return new JellyseerrProxyResponse
            {
                StatusCode = 503,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Jellyseerr URL not configured" }),
                ContentType = "application/json"
            };
        }

        var session = await LoadSessionAsync(userId);
        if (session == null)
        {
            return new JellyseerrProxyResponse
            {
                StatusCode = 401,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Not authenticated with Jellyseerr", code = "NO_SESSION" }),
                ContentType = "application/json"
            };
        }

        try
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri(jellyseerrUrl), new Cookie("connect.sid", session.SessionCookie));

            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);

            // Build the target URL
            var targetUrl = $"{jellyseerrUrl}/api/v1/{path.TrimStart('/')}";
            if (!string.IsNullOrEmpty(queryString))
            {
                targetUrl += $"?{queryString.TrimStart('?')}";
            }

            var request = new HttpRequestMessage(method, targetUrl);

            if (body != null && body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    contentType ?? "application/json");
            }

            var response = await client.SendAsync(request);

            var responseBody = await response.Content.ReadAsByteArrayAsync();
            var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            // If auth expired, clear session
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogInformation("Jellyseerr session expired for user {UserId}", userId);
                await ClearSessionAsync(userId);

                return new JellyseerrProxyResponse
                {
                    StatusCode = 401,
                    Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Jellyseerr session expired", code = "SESSION_EXPIRED" }),
                    ContentType = "application/json"
                };
            }

            // Check if Jellyseerr rotated the session cookie
            if (response.IsSuccessStatusCode)
            {
                await CheckForRotatedCookieAsync(session, response, cookieContainer, jellyseerrUrl);
            }

            return new JellyseerrProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = responseBody,
                ContentType = responseContentType
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to proxy request to Jellyseerr: {Path}", path);
            return new JellyseerrProxyResponse
            {
                StatusCode = 502,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = $"Cannot reach Jellyseerr: {ex.Message}" }),
                ContentType = "application/json"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error proxying to Jellyseerr: {Path}", path);
            return new JellyseerrProxyResponse
            {
                StatusCode = 500,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Internal proxy error" }),
                ContentType = "application/json"
            };
        }
    }

    /// <summary>
    /// Proxies a raw HTTP request to Jellyseerr for web content (no /api/v1/ prefix).
    /// Session cookie is optional — if unavailable, the request proceeds without auth.
    /// </summary>
    public async Task<JellyseerrProxyResponse> ProxyWebRequestAsync(
        Guid userId,
        HttpMethod method,
        string path,
        string? queryString = null,
        byte[]? body = null,
        string? contentType = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var jellyseerrUrl = config?.GetEffectiveJellyseerrUrl();

        if (string.IsNullOrEmpty(jellyseerrUrl))
        {
            return new JellyseerrProxyResponse
            {
                StatusCode = 503,
                Body = Encoding.UTF8.GetBytes("Jellyseerr not configured"),
                ContentType = "text/plain"
            };
        }

        var session = await LoadSessionAsync(userId);

        try
        {
            var cookieContainer = new CookieContainer();
            if (session != null)
            {
                cookieContainer.Add(new Uri(jellyseerrUrl), new Cookie("connect.sid", session.SessionCookie));
            }

            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);

            var targetUrl = $"{jellyseerrUrl}/{path?.TrimStart('/') ?? ""}";
            if (!string.IsNullOrEmpty(queryString))
            {
                targetUrl += queryString.StartsWith('?') ? queryString : $"?{queryString}";
            }

            var request = new HttpRequestMessage(method, targetUrl);

            if (body != null && body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                if (!string.IsNullOrEmpty(contentType))
                {
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsByteArrayAsync();
            var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            // Check if Jellyseerr rotated the session cookie
            if (session != null && response.IsSuccessStatusCode)
            {
                await CheckForRotatedCookieAsync(session, response, cookieContainer, jellyseerrUrl);
            }

            return new JellyseerrProxyResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = responseBody,
                ContentType = responseContentType
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to proxy web request to Jellyseerr: {Path}", path);
            return new JellyseerrProxyResponse
            {
                StatusCode = 502,
                Body = Encoding.UTF8.GetBytes($"Cannot reach Jellyseerr: {ex.Message}"),
                ContentType = "text/plain"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error proxying web request to Jellyseerr: {Path}", path);
            return new JellyseerrProxyResponse
            {
                StatusCode = 500,
                Body = Encoding.UTF8.GetBytes("Internal proxy error"),
                ContentType = "text/plain"
            };
        }
    }

    private async Task SaveSessionAsync(JellyseerrSession session)
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

    private async Task<JellyseerrSession?> LoadSessionAsync(Guid userId)
    {
        var path = GetSessionPath(userId);
        if (!File.Exists(path)) return null;

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<JellyseerrSession>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Jellyseerr session for user {UserId}", userId);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Stored Jellyseerr session data for a Jellyfin user.
/// </summary>
public class JellyseerrSession
{
    /// <summary>The Jellyfin user ID this session belongs to.</summary>
    [JsonPropertyName("jellyfinUserId")]
    public Guid JellyfinUserId { get; set; }

    /// <summary>The Jellyseerr connect.sid session cookie value.</summary>
    [JsonPropertyName("sessionCookie")]
    public string SessionCookie { get; set; } = string.Empty;

    /// <summary>The Jellyseerr internal user ID.</summary>
    [JsonPropertyName("jellyseerrUserId")]
    public int JellyseerrUserId { get; set; }

    /// <summary>The username used to authenticate.</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Display name from Jellyseerr.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Avatar URL from Jellyseerr.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    /// <summary>Jellyseerr permission bitmask.</summary>
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
/// Result of a Jellyseerr authentication attempt.
/// </summary>
public class JellyseerrAuthResult
{
    /// <summary>Whether authentication succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Error message if auth failed.</summary>
    public string? Error { get; set; }

    /// <summary>Jellyseerr user ID if successful.</summary>
    public int JellyseerrUserId { get; set; }

    /// <summary>Display name from Jellyseerr.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Avatar URL.</summary>
    public string? Avatar { get; set; }

    /// <summary>Jellyseerr permission bitmask.</summary>
    public int Permissions { get; set; }
}

/// <summary>
/// Response from a proxied Jellyseerr request.
/// </summary>
public class JellyseerrProxyResponse
{
    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; set; }

    /// <summary>Response body bytes.</summary>
    public byte[]? Body { get; set; }

    /// <summary>Response content type.</summary>
    public string ContentType { get; set; } = "application/json";
}
