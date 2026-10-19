using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

internal enum ListingState
{
    /// <summary>No listing is held yet (fetch pending or failed, nothing stored).</summary>
    Unavailable,
    Available,
    /// <summary>The provider has no listing for this folder (HTTP 404).</summary>
    Absent,
}

internal readonly record struct ListingSnapshot(ListingState State, ThumbnailListing? Listing);

/// <summary>The sorted, de-duplicated art names of one provider folder.</summary>
internal sealed class ThumbnailListing
{
    private readonly HashSet<string> _names;
    private readonly Dictionary<string, List<string>> _byKey = new(StringComparer.Ordinal);

    internal ThumbnailListing(IEnumerable<string> names)
    {
        Names = names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        _names = new HashSet<string>(Names, StringComparer.Ordinal);
        foreach (var name in Names)
        {
            if (TryAliasKey(name, out var key))
            {
                if (!_byKey.TryGetValue(key, out var list))
                {
                    _byKey[key] = list = new List<string>(1);
                }

                list.Add(name);
            }
        }
    }

    internal IReadOnlyList<string> Names { get; }

    internal string JoinedNames => string.Join('\n', Names);

    internal string Digest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JoinedNames))).ToLowerInvariant();

    internal bool Contains(string name) => _names.Contains(name);

    /// <summary>
    /// The one listed spelling of the same title and trailing groups as <paramref name="sanitizedName"/>,
    /// or null when the name is already listed or zero or several spellings share its key.
    /// </summary>
    internal string? FindAlias(string sanitizedName)
    {
        if (_names.Contains(sanitizedName) || !TryAliasKey(sanitizedName, out var key))
        {
            return null;
        }

        return _byKey.TryGetValue(key, out var matches) && matches.Count == 1 ? matches[0] : null;
    }

    private static bool TryAliasKey(string name, out string key)
    {
        try
        {
            var title = RdbIdentity.CdnTitleKey(name);
            key = title + "|" + RdbIdentity.TrailingGroupText(name);
            return title.Length > 0;
        }
        catch (ArgumentException)
        {
            key = string.Empty;
            return false;
        }
    }
}

/// <summary>
/// Holds the provider's art listing per (platform, folder). Reads never wait on the network: the
/// first need starts one background fetch, and a stored copy serves until it succeeds.
/// </summary>
internal sealed class ThumbnailListingStore
{
    private const long MaxListingBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan FetchRetryBackoff = TimeSpan.FromMinutes(5);
    private static readonly Regex HrefPattern = new(
        "href\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(10));

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly Func<string?> _dataFolder;
    private readonly ConcurrentDictionary<(string Platform, string Folder), Entry> _entries = new();

    internal ThumbnailListingStore(IHttpClientFactory httpClientFactory, ILogger logger, Func<string?> dataFolder)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _dataFolder = dataFolder;
    }

    internal ListingSnapshot Get(string platform, GameThumbService.ThumbKind kind)
    {
        var folder = GameArtworkStore.FolderFor(kind);
        var entry = _entries.GetOrAdd((platform, folder), key => new Entry(key.Platform, key.Folder, LoadStored(key.Platform, key.Folder)));
        lock (entry)
        {
            if (!entry.Succeeded && !entry.Absent && entry.InFlight == null && DateTime.UtcNow >= entry.RetryAfterUtc)
            {
                entry.InFlight = Task.Run(() => FetchAsync(entry, kind));
            }

            return entry.Absent
                ? new ListingSnapshot(ListingState.Absent, null)
                : entry.Listing != null
                    ? new ListingSnapshot(ListingState.Available, entry.Listing)
                    : new ListingSnapshot(ListingState.Unavailable, null);
        }
    }

    /// <summary>Test seam: the fetch currently running for a key, if any.</summary>
    internal Task? InFlightForTests(string platform, GameThumbService.ThumbKind kind)
    {
        if (!_entries.TryGetValue((platform, GameArtworkStore.FolderFor(kind)), out var entry))
        {
            return null;
        }

        lock (entry)
        {
            return entry.InFlight;
        }
    }

    private async Task FetchAsync(Entry entry, GameThumbService.ThumbKind kind)
    {
        ThumbnailListing? fetched = null;
        var notFound = false;
        try
        {
            (fetched, notFound) = await DownloadAsync(entry, kind).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fetching the {Folder} listing for {Platform} failed", entry.Folder, entry.Platform);
        }

        lock (entry)
        {
            if (fetched != null)
            {
                entry.Listing = fetched;
                entry.Succeeded = true;
            }
            else if (notFound)
            {
                entry.Listing = null;
                entry.Absent = true;
            }
            else
            {
                entry.RetryAfterUtc = DateTime.UtcNow + FetchRetryBackoff;
            }
        }

        if (fetched != null)
        {
            TryPersist(entry, fetched);
        }

        lock (entry)
        {
            entry.InFlight = null;
        }
    }

    private async Task<(ThumbnailListing? Listing, bool NotFound)> DownloadAsync(Entry entry, GameThumbService.ThumbKind kind)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

        using var response = await client
            .GetAsync(GameArtworkStore.BuildListingUrl(entry.Platform, kind), HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, true);
        }

        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxListingBytes)
        {
            return (null, false);
        }

        using var body = new MemoryStream();
        var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxListingBytes)
                {
                    return (null, false);
                }

                body.Write(buffer, 0, read);
            }
        }

        var names = ParseComplete(Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
        return (names == null ? null : new ThumbnailListing(names), false);
    }

    /// <summary>The listed names, or null when the page is cut short or lists nothing.</summary>
    internal static List<string>? ParseComplete(string html)
    {
        if (html.IndexOf("</html>", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var names = new List<string>();
        try
        {
            foreach (Match match in HrefPattern.Matches(html))
            {
                var href = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (href.Contains('/') || !href.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Uri.UnescapeDataString(WebUtility.HtmlDecode(href))[..^4];
                if (name.Length > 0 && !name.Contains('\n') && !name.Contains('\r'))
                {
                    names.Add(name);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return names.Count > 0 ? names : null;
    }

    private ThumbnailListing? LoadStored(string platform, string folder)
    {
        try
        {
            var (namesPath, sidecarPath) = StoredPaths(platform, folder);
            if (namesPath == null || sidecarPath == null || !File.Exists(namesPath) || !File.Exists(sidecarPath))
            {
                return null;
            }

            var text = File.ReadAllText(namesPath, Encoding.UTF8);
            if (text.Length == 0)
            {
                return null;
            }

            using var sidecar = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
            var listing = new ThumbnailListing(text.Split('\n'));
            var root = sidecar.RootElement;
            return root.TryGetProperty("sha256", out var sha) && string.Equals(sha.GetString(), listing.Digest, StringComparison.Ordinal)
                && root.TryGetProperty("count", out var count) && count.TryGetInt32(out var n) && n == listing.Names.Count
                    ? listing
                    : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the stored {Folder} listing for {Platform} failed", folder, platform);
            return null;
        }
    }

    private void TryPersist(Entry entry, ThumbnailListing listing)
    {
        try
        {
            var (namesPath, sidecarPath) = StoredPaths(entry.Platform, entry.Folder);
            if (namesPath == null || sidecarPath == null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(namesPath)!);
            var sidecar = JsonSerializer.Serialize(new
            {
                fetchedUtc = DateTime.UtcNow.ToString("O"),
                sha256 = listing.Digest,
                count = listing.Names.Count,
            });
            WriteAtomic(namesPath, listing.JoinedNames);
            WriteAtomic(sidecarPath, sidecar);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Storing the {Folder} listing for {Platform} failed", entry.Folder, entry.Platform);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private (string? Names, string? Sidecar) StoredPaths(string platform, string folder)
    {
        var dataFolder = _dataFolder();
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            return (null, null);
        }

        var stem = Path.Combine(dataFolder, "gamemeta", "listings", Uri.EscapeDataString(platform) + "__" + folder);
        return (stem + ".txt", stem + ".json");
    }

    private sealed class Entry
    {
        internal Entry(string platform, string folder, ThumbnailListing? stored)
        {
            Platform = platform;
            Folder = folder;
            Listing = stored;
        }

        internal string Platform { get; }

        internal string Folder { get; }

        // The fields below are guarded by lock (this).
        internal ThumbnailListing? Listing { get; set; }

        internal bool Absent { get; set; }

        internal bool Succeeded { get; set; }

        internal Task? InFlight { get; set; }

        internal DateTime RetryAfterUtc { get; set; }
    }
}
