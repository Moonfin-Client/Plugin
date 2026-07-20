using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fetches libretro box art and screenshots and caches them under game_thumbs/.
///
/// Clients cannot reach thumbnails.libretro.com directly from a browser because it serves no
/// CORS headers, so the art is fetched here and served back from the plugin's own origin.
/// Going through the server also means one download is shared by every client instead of each
/// of them hitting libretro on every scroll.
/// </summary>
public class GameThumbService
{
    /// <summary>The libretro thumbnail folder for a kind of art.</summary>
    public enum ThumbKind
    {
        Boxart,
        Snap,
        Title
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GameThumbService> _logger;
    private readonly string _cacheDir;

    // Names that resolved to a 404 upstream. Thumbnails are keyed on the No-Intro name, so a
    // ROM whose filename does not match has no art at all and re-asking on every request would
    // just be a slow way to fail.
    private readonly ConcurrentDictionary<string, byte> _misses = new(StringComparer.Ordinal);

    // Downloads already running, keyed the same way as the cache.
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new(StringComparer.Ordinal);

    public GameThumbService(IHttpClientFactory httpClientFactory, ILogger<GameThumbService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");
        _cacheDir = Path.Combine(dataPath, "game_thumbs");
    }

    public static ThumbKind ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "snap" => ThumbKind.Snap,
        "title" => ThumbKind.Title,
        _ => ThumbKind.Boxart
    };

    /// <summary>
    /// The cached file for a game's art, downloading it first when needed. Null when the core has
    /// no libretro platform, the name has no art upstream, or the download failed.
    /// </summary>
    public async Task<string?> GetThumbPathAsync(string core, string romFileName, ThumbKind kind)
    {
        if (!RdbService.TryGetPlatform(core, out var platform))
        {
            return null;
        }

        var name = LibretroThumbName(romFileName);
        if (name.Length == 0)
        {
            return null;
        }

        var cacheKey = CacheKey(platform, kind, name);
        if (_misses.ContainsKey(cacheKey))
        {
            return null;
        }

        var localPath = Path.Combine(_cacheDir, cacheKey + ".png");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // The detail screen asks for the same box art for its poster and its blurred backdrop at
        // once. GetOrAdd can run its factory on several threads but only ever returns one Lazy, so
        // holding the Lazy rather than the task is what keeps that to a single download.
        var lazy = _inFlight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<string?>>(() => DownloadAsync(platform, kind, name, cacheKey, localPath)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(cacheKey, out _);
        }
    }

    private async Task<string?> DownloadAsync(string platform, ThumbKind kind, string name, string cacheKey, string localPath)
    {
        var url = BuildUrl(platform, kind, name);
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            using var response = await client.GetAsync(url).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _misses.TryAdd(cacheKey, 0);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            Directory.CreateDirectory(_cacheDir);
            var temp = localPath + ".tmp";
            await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
            File.Move(temp, localPath, overwrite: true);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Downloading {Kind} art for {Name} failed", kind, name);
            return null;
        }
    }

    private static string BuildUrl(string platform, ThumbKind kind, string name) =>
        "https://thumbnails.libretro.com/"
        + Uri.EscapeDataString(platform) + "/" + FolderFor(kind) + "/"
        + Uri.EscapeDataString(name) + ".png";

    private static string FolderFor(ThumbKind kind) => kind switch
    {
        ThumbKind.Snap => "Named_Snaps",
        ThumbKind.Title => "Named_Titles",
        _ => "Named_Boxarts"
    };

    // The characters libretro rewrites to '_' in thumbnail filenames.
    private static readonly char[] ReservedChars = { '&', '*', '/', ':', '`', '<', '>', '?', '\\', '|', '"' };

    /// <summary>
    /// The name libretro keys thumbnails on: the ROM filename with its region and revision tags,
    /// minus the extension. This is the filename rather than the display title, which for a game
    /// in its own folder is the folder name and would not match.
    /// </summary>
    private static string LibretroThumbName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(ReservedChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    // Hashed down to one flat file name so the cache stays a single directory and no ROM name can
    // steer the path. Platform and kind are in the key because the same name exists on several
    // systems.
    private static string CacheKey(string platform, ThumbKind kind, string name)
    {
        var raw = platform + "|" + FolderFor(kind) + "|" + name;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
