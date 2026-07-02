using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>One game's metadata from the LaunchBox Games Database.</summary>
public sealed class LaunchBoxRecord
{
    public string? Overview { get; set; }
    public string? Genre { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public int? Year { get; set; }
    public int? Players { get; set; }
    public double? Rating { get; set; }
}

/// <summary>
/// Keyless rich game metadata (overview, genre, developer, publisher, year, players, rating)
/// from the LaunchBox Games Database. The whole DB is one ~100 MB download; on first use it is
/// fetched once, its Metadata.xml is streamed, and only our supported platforms and fields are
/// kept as compact per-core JSON under the plugin data folder (the big download is then
/// deleted). Lookups load a core's cache on demand and match by normalized title. Duplicate
/// names (ROM hacks, homebrew) are resolved by keeping the entry with the most community
/// ratings, which is the canonical release. All work is server-side; the client reads finished
/// fields.
/// </summary>
public class LaunchBoxService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LaunchBoxService> _logger;

    // LaunchBox platform name -> EmulatorJS core. Several LaunchBox platforms fold into one core
    // (Game Boy + Color, WonderSwan + Color, Neo Geo Pocket + Color).
    private static readonly IReadOnlyDictionary<string, string> PlatformToCore =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Nintendo Entertainment System"] = "nes",
            ["Super Nintendo Entertainment System"] = "snes",
            ["Nintendo Game Boy"] = "gb",
            ["Nintendo Game Boy Color"] = "gb",
            ["Nintendo Game Boy Advance"] = "gba",
            ["Nintendo 64"] = "n64",
            ["Nintendo DS"] = "nds",
            ["Nintendo Virtual Boy"] = "vb",
            ["Sega Genesis"] = "segaMD",
            ["Sega Master System"] = "segaMS",
            ["Sega Game Gear"] = "segaGG",
            ["Atari 2600"] = "atari2600",
            ["Atari 7800"] = "atari7800",
            ["Atari Lynx"] = "lynx",
            ["WonderSwan"] = "ws",
            ["WonderSwan Color"] = "ws",
            ["SNK Neo Geo Pocket"] = "ngp",
            ["SNK Neo Geo Pocket Color"] = "ngp",
            ["NEC TurboGrafx-16"] = "pce",
            ["Sony Playstation"] = "psx",
            ["Sony PSP"] = "psp",
        };

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, LaunchBoxRecord>> _loaded =
        new(StringComparer.Ordinal);

    private int _building;

    public LaunchBoxService(IHttpClientFactory httpClientFactory, ILogger<LaunchBoxService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Looks up metadata for a game. Returns null when disabled, the cache is not built yet (a
    /// one-time background build is kicked off so the next open resolves), or nothing matches.
    /// Never throws.
    /// </summary>
    public LaunchBoxRecord? TryLookup(string core, string? title, string fileName)
    {
        try
        {
            var config = MoonfinPlugin.Instance?.Configuration;
            if (config == null || !config.GamesLaunchBoxEnabled || !PlatformToCore.Values.Contains(core))
            {
                return null;
            }

            if (!IsBuilt())
            {
                EnsureBuilt(config);
                return null;
            }

            var index = _loaded.GetOrAdd(core, LoadCore);
            foreach (var candidate in NameCandidates(title, fileName))
            {
                if (index.TryGetValue(candidate, out var record))
                {
                    return record;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LaunchBox lookup failed for {File}", fileName);
            return null;
        }
    }

    private static IEnumerable<string> NameCandidates(string? title, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            var t = NormalizeName(title!);
            if (t.Length > 0)
            {
                yield return t;
            }
        }

        var dot = fileName.LastIndexOf('.');
        var baseName = dot > 0 ? fileName[..dot] : fileName;
        var f = NormalizeName(baseName);
        if (f.Length > 0)
        {
            yield return f;
        }
    }

    private IReadOnlyDictionary<string, LaunchBoxRecord> LoadCore(string core)
    {
        var path = CachePath(core);
        if (path == null || !File.Exists(path))
        {
            return new Dictionary<string, LaunchBoxRecord>();
        }

        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, LaunchBoxRecord>>(fs)
                ?? new Dictionary<string, LaunchBoxRecord>();
        }
        catch
        {
            return new Dictionary<string, LaunchBoxRecord>();
        }
    }

    private bool IsBuilt() => File.Exists(MarkerPath());

    private void EnsureBuilt(PluginConfiguration config)
    {
        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), $"launchbox-{Guid.NewGuid():N}.zip");
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

                await using (var src = await client.GetStreamAsync(config.GamesLaunchBoxUrl).ConfigureAwait(false))
                await using (var dst = File.Create(temp))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }

                BuildCacheFromZip(temp);
                _logger.LogInformation("Built LaunchBox game metadata cache");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Building LaunchBox metadata cache failed");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
                Interlocked.Exchange(ref _building, 0);
            }
        });
    }

    private void BuildCacheFromZip(string zipPath)
    {
        var root = CacheRoot();
        if (root == null)
        {
            return;
        }

        var perCore = new Dictionary<string, Dictionary<string, (LaunchBoxRecord rec, int ratings)>>(StringComparer.Ordinal);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry("Metadata.xml");
            if (entry == null)
            {
                return;
            }

            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "Game")
                {
                    reader.Read();
                    continue;
                }

                var game = (XElement)XNode.ReadFrom(reader);
                var platform = (string?)game.Element("Platform");
                var name = (string?)game.Element("Name");
                if (platform == null || name == null || !PlatformToCore.TryGetValue(platform, out var core))
                {
                    continue;
                }

                var key = NormalizeName(name);
                if (key.Length == 0)
                {
                    continue;
                }

                var ratings = (int?)game.Element("CommunityRatingCount") ?? 0;
                if (!perCore.TryGetValue(core, out var dict))
                {
                    dict = new Dictionary<string, (LaunchBoxRecord, int)>(StringComparer.Ordinal);
                    perCore[core] = dict;
                }

                // Keep the most-rated entry per name so the canonical release wins over hacks.
                if (dict.TryGetValue(key, out var existing) && existing.ratings >= ratings)
                {
                    continue;
                }

                dict[key] = (ToRecord(game, ratings), ratings);
            }
        }

        Directory.CreateDirectory(root);
        foreach (var (core, dict) in perCore)
        {
            var flat = dict.ToDictionary(kv => kv.Key, kv => kv.Value.rec);
            var json = JsonSerializer.SerializeToUtf8Bytes(flat);
            File.WriteAllBytes(Path.Combine(root, core + ".json"), json);
        }

        File.WriteAllText(MarkerPath()!, string.Empty);
    }

    private static LaunchBoxRecord ToRecord(XElement game, int ratings)
    {
        return new LaunchBoxRecord
        {
            Overview = Trimmed((string?)game.Element("Overview")),
            Genre = Trimmed((string?)game.Element("Genres")),
            Developer = Trimmed((string?)game.Element("Developer")),
            Publisher = Trimmed((string?)game.Element("Publisher")),
            Year = ParseYear(game),
            Players = (int?)game.Element("MaxPlayers"),
            Rating = ratings > 0 ? (double?)game.Element("CommunityRating") : null,
        };
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParseYear(XElement game)
    {
        var year = (int?)game.Element("ReleaseYear");
        if (year is > 0)
        {
            return year;
        }

        var date = (string?)game.Element("ReleaseDate");
        if (date != null && date.Length >= 4 && int.TryParse(date[..4], out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? CacheRoot()
    {
        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        return string.IsNullOrWhiteSpace(dataFolder) ? null : Path.Combine(dataFolder, "gamemeta-lb");
    }

    private static string? CachePath(string core)
    {
        var root = CacheRoot();
        return root == null ? null : Path.Combine(root, core + ".json");
    }

    private static string? MarkerPath()
    {
        var root = CacheRoot();
        return root == null ? null : Path.Combine(root, ".complete");
    }

    /// <summary>Lowercase alphanumerics with parenthesized/bracketed tags removed, so a No-Intro
    /// filename and a clean LaunchBox title collapse to the same key.</summary>
    private static string NormalizeName(string value)
    {
        var sb = new StringBuilder(value.Length);
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch is '(' or '[')
            {
                depth++;
            }
            else if (ch is ')' or ']')
            {
                if (depth > 0) depth--;
            }
            else if (depth == 0 && char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }
}
