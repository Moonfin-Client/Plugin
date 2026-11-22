using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Romm;

/// <summary>
/// Talks to one RomM server with a client token. RomM only accepts the token as a bearer
/// header, never in the URL, so every request goes through here.
/// </summary>
public sealed class RommClient
{
    private const int PageSize = 500;

    // Guards a runaway listing, far past any real library.
    private const int MaxPages = 2000;

    private static readonly Regex ByteRange = new Regex(@"^bytes=[0-9]*-[0-9]*(,\s*[0-9]*-[0-9]*)*$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public RommClient(HttpClient http, string baseUrl, string token)
    {
        _http = http;
        _baseUrl = baseUrl;
        _token = token;
    }

    /// <summary>
    /// The server address as an http or https URL with no trailing slash, query or credentials,
    /// or null when the setting isn't a usable address.
    /// </summary>
    public static string? NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>The token as RomM expects it, or null when it is blank or holds whitespace.</summary>
    public static string? NormalizeToken(string? token)
    {
        var trimmed = token?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed!.Any(char.IsWhiteSpace) ? null : trimmed;
    }

    public async Task<RommSnapshot> LoadAsync(RommFileRules rules, CancellationToken cancellationToken)
    {
        // shortcut: each refresh lists the whole library again (about 5 KB a game), switch to updated_after deltas if large libraries make refreshes slow.
        List<RommPlatform> platforms;
        using (var document = await GetJsonAsync("/api/platforms", cancellationToken).ConfigureAwait(false))
        {
            platforms = RommParser.ParsePlatforms(document.RootElement);
        }

        var byId = platforms.ToDictionary(platform => platform.Id);
        var games = new List<RommGame>();
        if (byId.Count == 0)
        {
            return new RommSnapshot(platforms, games, DateTime.UtcNow);
        }

        var platformFilter = string.Join("&", byId.Keys.Select(id => "platform_ids=" + id));
        for (var page = 0; page < MaxPages; page++)
        {
            var offset = page * PageSize;
            using var document = await GetJsonAsync(
                $"/api/roms?with_files=true&order_by=id&order_dir=asc&limit={PageSize}&offset={offset}&{platformFilter}",
                cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            games.AddRange(RommParser.ParseRoms(items, byId, rules));
            if (items.GetArrayLength() < PageSize)
            {
                break;
            }
        }

        return new RommSnapshot(platforms, games, DateTime.UtcNow);
    }

    /// <summary>
    /// Checks the address and token the way the admin page needs: the server's version from the
    /// open heartbeat, then a platform listing, which fails when the token is wrong.
    /// </summary>
    public async Task<RommServerInfo> CheckAsync(CancellationToken cancellationToken)
    {
        string? version = null;
        using (var heartbeat = await GetJsonAsync("/api/heartbeat", cancellationToken, authorize: false).ConfigureAwait(false))
        {
            if (heartbeat.RootElement.ValueKind == JsonValueKind.Object &&
                heartbeat.RootElement.TryGetProperty("SYSTEM", out var system) &&
                system.ValueKind == JsonValueKind.Object &&
                system.TryGetProperty("VERSION", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                version = value.GetString();
            }
        }

        using var platforms = await GetJsonAsync("/api/platforms", cancellationToken).ConfigureAwait(false);
        var supported = RommParser.ParsePlatforms(platforms.RootElement);
        var supportedIds = new HashSet<int>(supported.Select(platform => platform.Id));
        var platformCount = 0;
        var supportedRomCount = 0;
        if (platforms.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var platform in platforms.RootElement.EnumerateArray())
            {
                platformCount++;
                if (platform.TryGetProperty("id", out var id) && id.TryGetInt32(out var platformId) &&
                    supportedIds.Contains(platformId) &&
                    platform.TryGetProperty("rom_count", out var count) && count.TryGetInt32(out var roms))
                {
                    supportedRomCount += roms;
                }
            }
        }

        return new RommServerInfo(version, platformCount, supported.Count, supportedRomCount);
    }

    /// <summary>The caller disposes the response.</summary>
    public Task<HttpResponseMessage> SendRomAsync(HttpMethod method, RommGame game, string? range, CancellationToken cancellationToken) =>
        SendAsync(
            method,
            $"/api/roms/{game.Id}/content/{Uri.EscapeDataString(game.Rom.FileName)}?file_ids={string.Join(",", game.FileIds.DefaultIfEmpty(game.Rom.Id))}&purpose=play",
            range,
            cancellationToken);

    /// <summary>The caller disposes the response.</summary>
    public Task<HttpResponseMessage> SendFirmwareAsync(HttpMethod method, RommFile firmware, string? range, CancellationToken cancellationToken) =>
        SendAsync(
            method,
            $"/api/firmware/{firmware.Id}/content/{Uri.EscapeDataString(firmware.FileName)}",
            range,
            cancellationToken);

    /// <summary>
    /// Saves a game's ROM file for the plugin to unpack. Returns RequestEntityTooLarge once the
    /// download passes <paramref name="maxBytes"/>, otherwise RomM's status.
    /// </summary>
    public async Task<HttpStatusCode> DownloadRomAsync(RommGame game, string path, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await SendRomAsync(HttpMethod.Get, game, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return response.StatusCode;
        }

        using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var destination = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return HttpStatusCode.RequestEntityTooLarge;
            }

            await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
        }

        return HttpStatusCode.OK;
    }

    /// <summary>Starts RomM's device pairing, which needs no token yet.</summary>
    public async Task<RommDeviceLink> StartDeviceLinkAsync(string identifier, string name, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["client_device_identifier"] = identifier,
            ["name"] = name,
            ["client"] = "Moonfin",
            ["requested_scopes"] = scopes.ToArray(),
        });
        using var request = NewRequest(HttpMethod.Post, "/api/auth/device/init", authorize: false);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RommRequestException((int)response.StatusCode, "/api/auth/device/init");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = document.RootElement;
        var deviceCode = RommParser.Text(root, "device_code");
        var userCode = RommParser.Text(root, "user_code");
        if (deviceCode == null || userCode == null)
        {
            throw new RommRequestException(502, "/api/auth/device/init");
        }

        return new RommDeviceLink(
            deviceCode,
            userCode,
            _baseUrl + (RommParser.Text(root, "verification_path") ?? "/pair/device"),
            _baseUrl + (RommParser.Text(root, "verification_path_complete") ?? "/pair/device"),
            RommParser.Int(root, "expires_in") ?? 600,
            RommParser.Int(root, "interval") ?? 5);
    }

    /// <summary>
    /// One poll of a pairing. Returns the token once the user approved, or the reason RomM gave
    /// for not having one yet: authorization_pending, slow_down, access_denied or expired_token.
    /// </summary>
    public async Task<(string? Token, string? Reason)> PollDeviceLinkAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["device_code"] = deviceCode });
        using var request = NewRequest(HttpMethod.Post, "/api/auth/device/token", authorize: false);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            using var error = JsonDocument.Parse(text);
            return (null, RommParser.Text(error.RootElement, "detail"));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RommRequestException((int)response.StatusCode, "/api/auth/device/token");
        }

        using var document = JsonDocument.Parse(text);
        return (NormalizeToken(RommParser.Text(document.RootElement, "access_token")), null);
    }

    public async Task<string?> GetUsernameAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("/api/users/me", cancellationToken).ConfigureAwait(false);
        return RommParser.Text(document.RootElement, "username");
    }

    /// <summary>The newest battery save in a slot, from any emulator, so RomM's own player shares it.</summary>
    public async Task<RommAsset?> LatestSaveAsync(int romId, string slot, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"/api/saves?rom_id={romId}&slot={Uri.EscapeDataString(slot)}", cancellationToken).ConfigureAwait(false);
        return RommParser.ParseAssets(document.RootElement).OrderByDescending(asset => asset.UpdatedUtc).FirstOrDefault();
    }

    public async Task<RommAsset?> FindStateAsync(int romId, string fileName, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"/api/states?rom_id={romId}", cancellationToken).ConfigureAwait(false);
        return RommParser.ParseAssets(document.RootElement)
            .Where(asset => asset.FileName == fileName)
            .OrderByDescending(asset => asset.UpdatedUtc)
            .FirstOrDefault();
    }

    /// <summary><paramref name="folder"/> is "saves" or "states".</summary>
    public async Task<byte[]> DownloadAssetAsync(string folder, int id, CancellationToken cancellationToken)
    {
        var path = $"/api/{folder}/{id}/content";
        using var request = NewRequest(HttpMethod.Get, path, authorize: true);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RommRequestException((int)response.StatusCode, path);
        }

        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Files a battery save in a slot the way RomM's own player does: each upload is a new
    /// version and RomM keeps a short history.
    /// </summary>
    public Task<RommAsset> UploadSaveAsync(int romId, string emulator, string slot, string fileName, byte[] data, CancellationToken cancellationToken) =>
        UploadAsync(
            $"/api/saves?rom_id={romId}&emulator={Uri.EscapeDataString(emulator)}&slot={Uri.EscapeDataString(slot)}&overwrite=true&autocleanup=true",
            "saveFile",
            fileName,
            data,
            cancellationToken);

    /// <summary>RomM replaces a state with the same file name, so one name keeps one state.</summary>
    public Task<RommAsset> UploadStateAsync(int romId, string emulator, string fileName, byte[] data, CancellationToken cancellationToken) =>
        UploadAsync($"/api/states?rom_id={romId}&emulator={Uri.EscapeDataString(emulator)}", "stateFile", fileName, data, cancellationToken);

    /// <summary>
    /// The full URL of a RomM artwork file. Only RomM's public asset folder is accepted, so a
    /// path from RomM can't point the plugin's downloader anywhere else.
    /// </summary>
    public static string? AssetUrl(string baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path!.StartsWith("/assets/", StringComparison.Ordinal) || path.Contains(".."))
        {
            return null;
        }

        return baseUrl + path.Replace(" ", "%20");
    }

    public static string? SafeRange(string? range)
    {
        var trimmed = range?.Trim();
        return !string.IsNullOrEmpty(trimmed) && ByteRange.IsMatch(trimmed!) ? trimmed : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathAndQuery, string? range, CancellationToken cancellationToken)
    {
        var request = NewRequest(method, pathAndQuery, authorize: true);
        var safeRange = SafeRange(range);
        if (safeRange != null)
        {
            request.Headers.TryAddWithoutValidation("Range", safeRange);
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RommAsset> UploadAsync(string pathAndQuery, string field, string fileName, byte[] data, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, pathAndQuery, authorize: true);
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = new MultipartFormDataContent { { file, field, fileName } };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RommRequestException((int)response.StatusCode, pathAndQuery);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return RommParser.ParseAsset(document.RootElement) ?? throw new RommRequestException(502, pathAndQuery);
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken, bool authorize = true)
    {
        using var request = NewRequest(HttpMethod.Get, pathAndQuery, authorize);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RommRequestException((int)response.StatusCode, pathAndQuery);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string pathAndQuery, bool authorize)
    {
        var request = new HttpRequestMessage(method, _baseUrl + pathAndQuery);
        if (authorize)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }
}

public sealed class RommServerInfo
{
    public RommServerInfo(string? version, int platformCount, int supportedPlatformCount, int supportedRomCount)
    {
        Version = version;
        PlatformCount = platformCount;
        SupportedPlatformCount = supportedPlatformCount;
        SupportedRomCount = supportedRomCount;
    }

    public string? Version { get; }

    public int PlatformCount { get; }

    public int SupportedPlatformCount { get; }

    public int SupportedRomCount { get; }
}

public sealed class RommRequestException : Exception
{
    public RommRequestException(int statusCode, string pathAndQuery)
        : base(DescribeStatus(statusCode) + " (" + PathOnly(pathAndQuery) + ")")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    private static string DescribeStatus(int statusCode) => statusCode switch
    {
        401 => "RomM rejected the token",
        403 => "The RomM token is missing a scope this needs",
        404 => "RomM answered 404",
        _ => "RomM answered " + statusCode,
    };

    private static string PathOnly(string pathAndQuery)
    {
        var query = pathAndQuery.IndexOf('?');
        return query >= 0 ? pathAndQuery.Substring(0, query) : pathAndQuery;
    }
}
