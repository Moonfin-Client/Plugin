using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Facade for libretro RDB metadata lookup and artwork-name resolution.
/// </summary>
public class RdbService
{
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
            ["mame"] = "MAME",
            ["arcade"] = "FBNeo - Arcade Games",
        };

    private static readonly IReadOnlyDictionary<string, string[]> PlatformFamily =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Sega - Master System - Mark III"] = new[] { "Sega - SG-1000", "Sega - Game Gear" },
            ["Sega - SG-1000"] = new[] { "Sega - Master System - Mark III" },
            ["Nintendo - Game Boy"] = new[] { "Nintendo - Game Boy Color" },
            ["Atari - 7800"] = new[] { "Atari - 2600" },
        };

    internal const int MaxCandidatePlatformsPerRequest = 4;
    internal const int MaxSiblingArtworkNamesPerRequest = 6;
    private const int MaxLookupCacheEntries = 4096;

    private readonly ILogger<RdbService> _logger;
    private readonly RdbIndexStore _indexStore;
    private readonly RdbMatcher _matcher = new();
    private readonly ConcurrentDictionary<string, LookupCacheResult> _lookupCache = new(StringComparer.Ordinal);

    /// <summary>Test seam: overrides the configuration used by artwork resolution.</summary>
    internal PluginConfiguration? ConfigOverrideForTests;

    /// <summary>Test seam: number of index builds executed by the backing store.</summary>
    internal int BuildIndexCallCountForTests => _indexStore.BuildIndexCallCountForTests;

    /// <summary>Test seam invoked immediately before the store parses an RDB.</summary>
    internal Action? BuildIndexAboutToRunForTests
    {
        get => _indexStore.BuildIndexAboutToRunForTests;
        set => _indexStore.BuildIndexAboutToRunForTests = value;
    }

    public RdbService(IHttpClientFactory httpClientFactory, ILogger<RdbService> logger)
    {
        _logger = logger;
        _indexStore = new RdbIndexStore(httpClientFactory, logger, dataFolderPathOverride: null);
    }

    internal RdbService(IHttpClientFactory httpClientFactory, ILogger<RdbService> logger, string dataFolderPath)
    {
        ArgumentNullException.ThrowIfNull(dataFolderPath);
        _logger = logger;
        _indexStore = new RdbIndexStore(httpClientFactory, logger, dataFolderPath);
    }

    /// <summary>The libretro platform a core maps to, or false when the core has none.</summary>
    public static bool TryGetPlatform(string? core, out string platform)
    {
        if (!string.IsNullOrEmpty(core) && CoreToPlatform.TryGetValue(core, out var found))
        {
            platform = found;
            return true;
        }

        platform = string.Empty;
        return false;
    }

    /// <summary>
    /// Looks up optional game metadata without waiting for a cold index. A cache miss hashes the
    /// ROM (a multi-GB read for psx/psp) off the calling thread via <see cref="Task.Run(Action, CancellationToken)"/>,
    /// mirroring <see cref="ResolveArtworkNameCoreAsync"/> so this is safe to await from a Kestrel
    /// request thread.
    /// </summary>
    public async Task<RdbRecord?> TryLookupAsync(string core, string romPath, string? title, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = MoonfinPlugin.Instance?.Configuration;
            if (config == null || !config.GamesMetadataEnabled || !CoreToPlatform.TryGetValue(core, out var platform))
            {
                return null;
            }

            if (!TryGetFileStamp(romPath, out var size, out var mtime))
            {
                return null;
            }

            if (_lookupCache.TryGetValue(romPath, out var cached) && cached.Matches(size, mtime))
            {
                return cached switch
                {
                    DetailLookupCacheResult detail => detail.Record,
                    ArtworkLookupCacheResult artwork => artwork.Primary,
                    CachedLookup legacy => legacy.Record,
                    _ => null,
                };
            }

            var index = GetIndexOrStartBuild(platform, config);
            if (index == null)
            {
                return null;
            }

            var record = await Task.Run(() => _matcher.Match(index, romPath, title), cancellationToken).ConfigureAwait(false);
            CacheLookup(romPath, new DetailLookupCacheResult(size, mtime, record));
            return record;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller giving up is not a lookup failure. Without this the catch-all below would
            // swallow the cancellation and log noise on every aborted request -- a hazard this
            // method only acquired when it became cancellable.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Game metadata lookup failed for {Path}", romPath);
            return null;
        }
    }

    internal static IReadOnlyList<string> GetCandidatePlatforms(
        string core,
        bool coreWasDefaulted,
        IReadOnlyList<string> fuzzyDirectoryCores)
    {
        var result = new List<string>(MaxCandidatePlatformsPerRequest);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void TryAdd(string? platform)
        {
            if (!string.IsNullOrEmpty(platform) && result.Count < MaxCandidatePlatformsPerRequest && seen.Add(platform))
            {
                result.Add(platform);
            }
        }

        if (!coreWasDefaulted && TryGetPlatform(core, out var primary))
        {
            TryAdd(primary);
            if (PlatformFamily.TryGetValue(primary, out var family))
            {
                foreach (var related in family)
                {
                    TryAdd(related);
                }
            }
        }

        foreach (var fuzzyCore in fuzzyDirectoryCores)
        {
            if (TryGetPlatform(fuzzyCore, out var fuzzyPlatform))
            {
                TryAdd(fuzzyPlatform);
            }
        }

        if (coreWasDefaulted && TryGetPlatform(core, out var defaultedPlatform))
        {
            TryAdd(defaultedPlatform);
        }

        return result;
    }

    public async Task<ArtworkNameResolution?> ResolveArtworkNameAsync(
        IReadOnlyList<string> candidatePlatforms,
        string romPath,
        string? title,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = ConfigOverrideForTests ?? MoonfinPlugin.Instance?.Configuration;
            return config == null
                ? null
                : await ResolveArtworkNameCoreAsync(candidatePlatforms, romPath, title, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolving artwork name failed for {Path}", romPath);
            return null;
        }
    }

    // Config-independent seam retained for focused service tests.
    private async Task<ArtworkNameResolution?> ResolveArtworkNameCoreAsync(
        IReadOnlyList<string> candidatePlatforms,
        string romPath,
        string? title,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (candidatePlatforms.Count == 0 || !TryGetFileStamp(romPath, out var size, out var mtime))
        {
            return null;
        }

        if (_lookupCache.TryGetValue(romPath, out var cached) && cached.Matches(size, mtime))
        {
            return cached is ArtworkLookupCacheResult artwork
                ? new ArtworkNameResolution(BuildNameList(artwork.Primary, artwork.Siblings), artwork.Platform)
                : null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<uint>? crcCandidates = null;
        for (var i = 0; i < candidatePlatforms.Count; i++)
        {
            var platform = candidatePlatforms[i];
            var index = await GetIndexAsync(platform, config).ConfigureAwait(false);
            if (index == null)
            {
                continue;
            }

            if (i == 0)
            {
                var matches = await Task.Run(() => _matcher.MatchWithSiblings(index, romPath, title, MaxSiblingArtworkNamesPerRequest), cancellationToken)
                    .ConfigureAwait(false);
                if (matches.Count > 0)
                {
                    var primary = matches[0];
                    var siblings = matches.Count > 1 ? matches.GetRange(1, matches.Count - 1) : null;
                    CacheLookup(romPath, new ArtworkLookupCacheResult(size, mtime, primary, platform, siblings));
                    return new ArtworkNameResolution(BuildNameList(primary, siblings), platform);
                }
            }
            else
            {
                crcCandidates ??= await Task.Run(() => RdbMatcher.ComputeCrcCandidates(romPath), cancellationToken).ConfigureAwait(false);
                var record = RdbMatcher.MatchByCrc(index, crcCandidates);
                if (record != null)
                {
                    CacheLookup(romPath, new ArtworkLookupCacheResult(size, mtime, record, platform, null));
                    return new ArtworkNameResolution(BuildNameList(record, null), platform);
                }
            }
        }

        CacheLookup(romPath, new DetailLookupCacheResult(size, mtime, null));
        return null;
    }

    private static bool TryGetFileStamp(string romPath, out long size, out long mtime)
    {
        try
        {
            var info = new FileInfo(romPath);
            size = info.Length;
            mtime = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            size = 0;
            mtime = 0;
            return false;
        }
    }

    private static List<string> BuildNameList(RdbRecord primary, IReadOnlyList<RdbRecord>? siblings)
    {
        var names = new List<string>(1 + (siblings?.Count ?? 0));
        if (!string.IsNullOrEmpty(primary.Name))
        {
            names.Add(primary.Name);
        }

        if (siblings != null)
        {
            foreach (var sibling in siblings)
            {
                if (!string.IsNullOrEmpty(sibling.Name))
                {
                    names.Add(sibling.Name);
                }
            }
        }

        return names;
    }

    private void CacheLookup(string romPath, LookupCacheResult lookup)
    {
        if (!_lookupCache.ContainsKey(romPath) && _lookupCache.Count >= MaxLookupCacheEntries)
        {
            _lookupCache.Clear();
        }

        _lookupCache[romPath] = lookup;
    }

    // These forwards preserve existing reflection-based tests while RdbMatcher owns the policy.
    private RdbRecord? Match(PlatformIndex index, string romPath, string? title) => _matcher.Match(index.ToStoreIndex(), romPath, title);
    private List<RdbRecord> MatchWithSiblings(PlatformIndex index, string romPath, string? title) =>
        _matcher.MatchWithSiblings(index.ToStoreIndex(), romPath, title, MaxSiblingArtworkNamesPerRequest);
    private static string ExtractBaseTitle(string name) => RdbMatcher.ExtractBaseTitle(name);

    private RdbPlatformIndex? GetIndexOrStartBuild(string platform, PluginConfiguration config) =>
        _indexStore.GetIndexOrStartBuild(platform, config);

    // Kept as a private facade seam for the existing lifecycle tests.
    private Task<RdbPlatformIndex?> GetIndexAsync(string platform, PluginConfiguration config) =>
        _indexStore.GetIndexAsync(platform, config);

    // Kept as a private facade seam for the existing retry-backoff tests.
    private Task EnsureDownloadedAsync(string platform, PluginConfiguration config) =>
        _indexStore.EnsureDownloadedAsync(platform, config);

    private sealed record PlatformIndex(
        IReadOnlyDictionary<uint, RdbRecord> ByCrc,
        IReadOnlyDictionary<string, RdbRecord> ByName)
    {
        internal RdbPlatformIndex ToStoreIndex() => new(ByCrc, ByName);
    }

    private abstract record LookupCacheResult(long Size, long Mtime)
    {
        internal bool Matches(long size, long mtime) => Size == size && Mtime == mtime;
    }

    private sealed record DetailLookupCacheResult(long Size, long Mtime, RdbRecord? Record) : LookupCacheResult(Size, Mtime);

    private sealed record ArtworkLookupCacheResult(
        long Size,
        long Mtime,
        RdbRecord Primary,
        string Platform,
        IReadOnlyList<RdbRecord>? Siblings) : LookupCacheResult(Size, Mtime);

    // Compatibility type for the historical reflection-based cache-cap test. Production entries
    // use the two explicit variants above, so detail and artwork results cannot be confused.
    private sealed record CachedLookup(
        long Size,
        long Mtime,
        RdbRecord? Record,
        string? Platform,
        IReadOnlyList<RdbRecord>? Siblings = null) : LookupCacheResult(Size, Mtime);
}

/// <summary>The ordered artwork names and the libretro platform that matched the ROM.</summary>
public sealed record ArtworkNameResolution(IReadOnlyList<string> Names, string Platform);
