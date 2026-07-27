using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Sends push messages through the FCM HTTP v1 API. Auth is hand-rolled: a service
/// account signs a JWT which is exchanged for a short-lived OAuth2 access token.
/// The access token is cached in memory until shortly before it expires.
/// </summary>
public class FcmSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    // Must match the channel the app creates at startup, otherwise pushes land
    // in the generic channel Android makes up and lose their importance.
    private const string AndroidChannelId = "seerr_notifications";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FcmSender> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    // Tracks the account the cached token was minted for, so a config change invalidates it.
    private string? _tokenAccountEmail;

    public FcmSender(IHttpClientFactory httpClientFactory, ILogger<FcmSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Sends a notification to a single device token. Returns a result the caller can use
    /// to prune dead tokens. Never throws for the expected failure paths.
    /// </summary>
    public async Task<FcmSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        string route,
        string? requestId = null,
        string? platform = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            return FcmSendResult.Failed;
        }

        var account = LoadServiceAccount();
        if (account == null)
        {
            return FcmSendResult.Failed;
        }

        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(account, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mint FCM access token");
            return FcmSendResult.Failed;
        }

        object payload;
        var isRequest = !string.IsNullOrEmpty(requestId);
        var isIos = string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase);

        if (isRequest && isIos)
        {
            // iOS request: keep the notification so it shows closed, plus data + category so the
            // client can attach Approve/Deny actions.
            payload = new
            {
                message = new
                {
                    token = deviceToken,
                    notification = new { title, body },
                    data = new { route, requestId = requestId!, kind = "request" },
                    android = new
                    {
                        priority = "high",
                        notification = new { channel_id = AndroidChannelId }
                    },
                    apns = new
                    {
                        headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
                        payload = new { aps = new { sound = "default", category = "seerr_request" } }
                    }
                }
            };
        }
        else if (isRequest)
        {
            // Android request (also the fallback for unknown platforms): a normal notification so
            // the OS renders it even when the app is force-killed; tapping opens the app.
            payload = new
            {
                message = new
                {
                    token = deviceToken,
                    notification = new { title, body },
                    data = new { route },
                    android = new
                    {
                        priority = "high",
                        notification = new { channel_id = AndroidChannelId }
                    }
                }
            };
        }
        else
        {
            payload = new
            {
                message = new
                {
                    token = deviceToken,
                    notification = new { title, body },
                    data = new { route },
                    android = new
                    {
                        priority = "high",
                        notification = new { channel_id = AndroidChannelId }
                    },
                    apns = new
                    {
                        headers = new Dictionary<string, string> { ["apns-priority"] = "10" },
                        payload = new { aps = new { sound = "default" } }
                    }
                }
            };
        }

        var url = $"https://fcm.googleapis.com/v1/projects/{account.ProjectId}/messages:send";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return FcmSendResult.Ok;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsTokenDead(response.StatusCode, responseBody))
            {
                return FcmSendResult.TokenDead;
            }

            _logger.LogWarning("FCM send failed with status {Status}: {Detail}",
                (int)response.StatusCode, ExtractErrorStatus(responseBody));
            return FcmSendResult.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FCM send request failed");
            return FcmSendResult.Failed;
        }
    }

    private ServiceAccount? LoadServiceAccount()
    {
        var json = MoonfinPlugin.Instance?.Configuration?.GetFcmServiceAccountJson();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var clientEmail = GetString(root, "client_email");
            var privateKey = GetString(root, "private_key");
            var projectId = GetString(root, "project_id");
            var tokenUri = GetString(root, "token_uri") ?? DefaultTokenUri;

            if (string.IsNullOrEmpty(clientEmail) ||
                string.IsNullOrEmpty(privateKey) ||
                string.IsNullOrEmpty(projectId))
            {
                _logger.LogInformation("FCM service account is missing required fields; push disabled");
                return null;
            }

            return new ServiceAccount(clientEmail, privateKey, projectId, tokenUri);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "FCM service account JSON could not be parsed; push disabled");
            return null;
        }
    }

    private async Task<string> GetAccessTokenAsync(ServiceAccount account, CancellationToken cancellationToken)
    {
        // Refresh ~5 minutes before expiry.
        if (_cachedToken != null &&
            _tokenAccountEmail == account.ClientEmail &&
            DateTimeOffset.UtcNow < _tokenExpiry - TimeSpan.FromMinutes(5))
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken != null &&
                _tokenAccountEmail == account.ClientEmail &&
                DateTimeOffset.UtcNow < _tokenExpiry - TimeSpan.FromMinutes(5))
            {
                return _cachedToken;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var jwt = BuildSignedJwt(account, now);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", jwt)
            });

            using var response = await client.PostAsync(account.TokenUri, form, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Token endpoint returned {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var accessToken = GetString(doc.RootElement, "access_token");
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) &&
                exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt32()
                : 3600;

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new InvalidOperationException("Token endpoint returned no access token");
            }

            _cachedToken = accessToken;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            _tokenAccountEmail = account.ClientEmail;
            return accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string BuildSignedJwt(ServiceAccount account, long now)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" })));

        var claims = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = account.ClientEmail,
            scope = Scope,
            aud = account.TokenUri,
            iat = now,
            exp = now + 3600
        })));

        var signingInput = header + "." + claims;

        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return signingInput + "." + Base64Url(signature);
    }

    private static bool IsTokenDead(System.Net.HttpStatusCode status, string responseBody)
    {
        if (status == System.Net.HttpStatusCode.NotFound)
        {
            return true;
        }

        var errorStatus = ExtractErrorStatus(responseBody);
        return string.Equals(errorStatus, "UNREGISTERED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(errorStatus, "NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    // Pulls error.status from an FCM error body without exposing the rest of it.
    private static string? ExtractErrorStatus(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("status", out var statusProp))
            {
                return statusProp.GetString();
            }
        }
        catch
        {
            // Non-JSON body; nothing to extract.
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ServiceAccount(
        string ClientEmail, string PrivateKey, string ProjectId, string TokenUri);
}

/// <summary>Outcome of an FCM send, used by the caller to prune dead tokens.</summary>
public enum FcmSendResult
{
    /// <summary>Delivered to FCM.</summary>
    Ok,

    /// <summary>The token is no longer valid and should be removed.</summary>
    TokenDead,

    /// <summary>A transient or unexpected failure; the token is kept.</summary>
    Failed
}
