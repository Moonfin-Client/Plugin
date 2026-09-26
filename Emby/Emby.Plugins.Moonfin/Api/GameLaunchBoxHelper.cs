using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Emby.Plugins.Moonfin.Api
{
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
    /// Keyless rich game metadata from the LaunchBox Games Database. The whole DB is one ~100 MB
    /// download, fetched once and reduced to a compact per-core JSON cache under the plugin data
    /// folder. The big download is then deleted. Lookups load a core's cache on demand and match
    /// by normalized title, keeping the most-rated entry per name (the canonical release).
    /// Static because the Emby scanner is created per request.
    /// </summary>
    internal static class GameLaunchBoxHelper
    {
        private static readonly Dictionary<string, string> PlatformToCore =
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

        private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, LaunchBoxRecord>> Loaded =
            new ConcurrentDictionary<string, IReadOnlyDictionary<string, LaunchBoxRecord>>(StringComparer.Ordinal);

        private static int _building;

        public static LaunchBoxRecord? TryLookup(string core, string? title, string fileName)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null || !config.GamesLaunchBoxEnabled || !PlatformToCore.Values.Contains(core))
                {
                    return null;
                }

                if (!IsBuilt())
                {
                    EnsureBuilt(config);
                    return null;
                }

                var index = Loaded.GetOrAdd(core, LoadCore);
                foreach (var candidate in NameCandidates(title, fileName))
                {
                    if (index.TryGetValue(candidate, out var record))
                    {
                        return record;
                    }
                }

                return null;
            }
            catch
            {
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
            var baseName = dot > 0 ? fileName.Substring(0, dot) : fileName;
            var f = NormalizeName(baseName);
            if (f.Length > 0)
            {
                yield return f;
            }
        }

        private static IReadOnlyDictionary<string, LaunchBoxRecord> LoadCore(string core)
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

        private static bool IsBuilt()
        {
            var marker = MarkerPath();
            return marker != null && File.Exists(marker);
        }

        private static void EnsureBuilt(PluginConfiguration config)
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
                    using (var client = MoonfinHttp.CreateClient(TimeSpan.FromMinutes(20), "Moonfin/1.0"))
                    {
                        var data = await client.GetByteArrayAsync(config.GamesLaunchBoxUrl).ConfigureAwait(false);
                        File.WriteAllBytes(temp, data);
                    }

                    BuildCacheFromZip(temp);
                }
                catch
                {
                    // ignore. A later open retries
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    Interlocked.Exchange(ref _building, 0);
                }
            });
        }

        private static void BuildCacheFromZip(string zipPath)
        {
            var root = CacheRoot();
            if (root == null)
            {
                return;
            }

            var perCore = new Dictionary<string, Dictionary<string, KeyValuePair<LaunchBoxRecord, int>>>(StringComparer.Ordinal);

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
                        dict = new Dictionary<string, KeyValuePair<LaunchBoxRecord, int>>(StringComparer.Ordinal);
                        perCore[core] = dict;
                    }

                    // Keep the most-rated entry per name so the canonical release wins over hacks.
                    if (dict.TryGetValue(key, out var existing) && existing.Value >= ratings)
                    {
                        continue;
                    }

                    dict[key] = new KeyValuePair<LaunchBoxRecord, int>(ToRecord(game, ratings), ratings);
                }
            }

            Directory.CreateDirectory(root);
            foreach (var pair in perCore)
            {
                var flat = pair.Value.ToDictionary(kv => kv.Key, kv => kv.Value.Key);
                File.WriteAllBytes(Path.Combine(root, pair.Key + ".json"), JsonSerializer.SerializeToUtf8Bytes(flat));
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
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        private static int? ParseYear(XElement game)
        {
            var year = (int?)game.Element("ReleaseYear");
            if (year.HasValue && year.Value > 0)
            {
                return year;
            }

            var date = (string?)game.Element("ReleaseDate");
            if (date != null && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static string? CacheRoot()
        {
            var dataFolder = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrWhiteSpace(dataFolder) ? null : Path.Combine(dataFolder!, "gamemeta-lb");
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

        // Accents fold out first, as in LaunchBoxService.NormalizeName on the Jellyfin side.
        internal static string NormalizeName(string value)
        {
            var sb = new StringBuilder(value.Length);
            var depth = 0;
            foreach (var ch in GamesScanner.FoldedForm(value))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (ch == '(' || ch == '[')
                {
                    depth++;
                }
                else if (ch == ')' || ch == ']')
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
}
