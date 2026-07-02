using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Provides keyless game metadata by matching a ROM against the libretro <c>.rdb</c>
/// databases. Files are fetched lazily per system from the configured base (jsDelivr CDN by
/// default) and cached under the plugin data folder; a system's file only downloads when a
/// game from that system is opened. All work happens server-side: the ROM is hashed here
/// (the client never holds ROM bytes), matched by CRC (then filename/title), and the parsed
/// result is cached so repeated opens do not re-scan or re-hash.
/// </summary>
public class RdbService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RdbService> _logger;

    // EmulatorJS core -> libretro platform name. The name is both the .rdb filename (minus
    // extension) and the thumbnail folder, and mirrors the client _libretroPlatform map.
    private static readonly IReadOnlyDictionary<string, string> CoreToPlatform =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nes"] = "Nintendo - Nintendo Entertainment System",
            ["snes"] = "Nintendo - Super Nintendo Entertainment System",
            ["gb"] = "Nintendo - Game Boy",
            ["gba"] = "Nintendo - Game Boy Advance",
            ["n64"] = "Nintendo - Nintendo 64",
            ["nds"] = "Nintendo - Nintendo DS",
            ["vb"] = "Nintendo - Virtual Boy",
            ["segaMD"] = "Sega - Mega Drive - Genesis",
            ["segaMS"] = "Sega - Master System - Mark III",
            ["segaGG"] = "Sega - Game Gear",
            ["atari2600"] = "Atari - 2600",
            ["atari7800"] = "Atari - 7800",
            ["lynx"] = "Atari - Lynx",
            ["ws"] = "Bandai - WonderSwan",
            ["ngp"] = "SNK - Neo Geo Pocket",
            ["pce"] = "NEC - PC Engine - TurboGrafx 16",
            ["psx"] = "Sony - PlayStation",
            ["psp"] = "Sony - PlayStation Portable",
        };

    // Parsed indexes keyed by platform, built once per file.
    private readonly ConcurrentDictionary<string, PlatformIndex> _indexes = new(StringComparer.Ordinal);

    // Platforms whose download is in flight, so concurrent opens do not fetch twice.
    private readonly ConcurrentDictionary<string, byte> _downloading = new(StringComparer.Ordinal);

    // Final lookup result cached per ROM path (invalidated when the file changes).
    private readonly ConcurrentDictionary<string, CachedLookup> _lookupCache = new(StringComparer.Ordinal);

    public RdbService(IHttpClientFactory httpClientFactory, ILogger<RdbService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Looks up metadata for a ROM. Returns null when metadata is disabled, the system is
    /// unsupported, or the database has not been downloaded yet (a background fetch is started
    /// so the next open resolves). Never throws.
    /// </summary>
    public RdbRecord? TryLookup(string core, string romPath, string? title)
    {
        try
        {
            var config = MoonfinPlugin.Instance?.Configuration;
            if (config == null || !config.GamesMetadataEnabled)
            {
                return null;
            }

            if (!CoreToPlatform.TryGetValue(core, out var platform))
            {
                return null;
            }

            long size;
            long mtime;
            try
            {
                var info = new FileInfo(romPath);
                size = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return null;
            }

            if (_lookupCache.TryGetValue(romPath, out var cached) &&
                cached.Size == size && cached.Mtime == mtime)
            {
                return cached.Record;
            }

            var index = GetIndex(platform, config);
            if (index == null)
            {
                return null;
            }

            var record = Match(index, romPath, title);
            _lookupCache[romPath] = new CachedLookup(size, mtime, record);
            return record;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Game metadata lookup failed for {Path}", romPath);
            return null;
        }
    }

    private RdbRecord? Match(PlatformIndex index, string romPath, string? title)
    {
        foreach (var crc in ComputeCrcCandidates(romPath))
        {
            if (index.ByCrc.TryGetValue(crc, out var byCrc))
            {
                return byCrc;
            }
        }

        var fileName = NormalizeName(Path.GetFileNameWithoutExtension(romPath));
        if (fileName.Length > 0 && index.ByName.TryGetValue(fileName, out var byFile))
        {
            return byFile;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var norm = NormalizeName(title);
            if (norm.Length > 0 && index.ByName.TryGetValue(norm, out var byTitle))
            {
                return byTitle;
            }
        }

        return null;
    }

    private PlatformIndex? GetIndex(string platform, PluginConfiguration config)
    {
        if (_indexes.TryGetValue(platform, out var existing))
        {
            return existing;
        }

        var localPath = LocalPath(platform);
        if (localPath == null || !File.Exists(localPath))
        {
            EnsureDownloaded(platform, config);
            return null;
        }

        var index = BuildIndex(localPath);
        _indexes[platform] = index;
        return index;
    }

    private static PlatformIndex BuildIndex(string path)
    {
        var byCrc = new Dictionary<uint, RdbRecord>();
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal);

        foreach (var record in RdbReader.ReadAll(path))
        {
            if (record.Crc is { } crc)
            {
                byCrc[crc] = record;
            }

            if (record.RomName is { } rom)
            {
                byName[NormalizeName(Path.GetFileNameWithoutExtension(rom))] = record;
            }

            if (record.Name is { } name)
            {
                byName[NormalizeName(name)] = record;
            }
        }

        return new PlatformIndex(byCrc, byName);
    }

    private void EnsureDownloaded(string platform, PluginConfiguration config)
    {
        var localPath = LocalPath(platform);
        if (localPath == null || File.Exists(localPath))
        {
            return;
        }

        var baseLocation = config.GamesMetadataDbUrlBase;
        if (string.IsNullOrWhiteSpace(baseLocation))
        {
            return;
        }

        // A non-http base is treated as a local mirror directory (offline servers).
        if (!baseLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var source = Path.Combine(baseLocation, platform + ".rdb");
                if (File.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    File.Copy(source, localPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Copying local .rdb for {Platform} failed", platform);
            }

            return;
        }

        if (!_downloading.TryAdd(platform, 0))
        {
            return;
        }

        var url = baseLocation.TrimEnd('/') + "/" + Uri.EscapeDataString(platform) + ".rdb";
        _ = Task.Run(async () =>
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(2);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

                var data = await client.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                var temp = localPath + ".tmp";
                await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
                File.Move(temp, localPath, overwrite: true);
                _logger.LogInformation("Downloaded game metadata for {Platform} ({Bytes} bytes)", platform, data.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Downloading .rdb for {Platform} failed", platform);
            }
            finally
            {
                _downloading.TryRemove(platform, out _);
            }
        });
    }

    private static string? LocalPath(string platform)
    {
        var dataFolder = MoonfinPlugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            return null;
        }

        return Path.Combine(dataFolder, "gamemeta", platform + ".rdb");
    }

    // Candidate CRC32 values for a ROM: the whole file, plus the body without an iNES header
    // for NES dumps (No-Intro CRCs exclude that 16-byte header). Streamed so large ROMs are
    // not loaded into memory.
    private static IReadOnlyList<uint> ComputeCrcCandidates(string romPath)
    {
        var candidates = new List<uint>(2);
        try
        {
            candidates.Add(Crc32File(romPath, 0));
            if (HasInesHeader(romPath))
            {
                candidates.Add(Crc32File(romPath, 16));
            }
        }
        catch
        {
            // ignore
        }

        return candidates;
    }

    private static bool HasInesHeader(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return fs.Length > 16 && fs.Read(head) == 4 &&
            head[0] == (byte)'N' && head[1] == (byte)'E' && head[2] == (byte)'S' && head[3] == 0x1A;
    }

    private static uint Crc32File(string path, int skip)
    {
        var crc = 0xFFFFFFFFu;
        using var fs = File.OpenRead(path);
        if (skip > 0)
        {
            fs.Seek(skip, SeekOrigin.Begin);
        }

        var buffer = new byte[65536];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static string NormalizeName(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private sealed record PlatformIndex(
        IReadOnlyDictionary<uint, RdbRecord> ByCrc,
        IReadOnlyDictionary<string, RdbRecord> ByName);

    private readonly record struct CachedLookup(long Size, long Mtime, RdbRecord? Record);
}
