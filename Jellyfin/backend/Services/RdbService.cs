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
    private const int MaxCompletedMatchesPerRom = 16;

    private readonly ILogger<RdbService> _logger;
    private readonly RdbIndexStore _indexStore;
    private readonly RdbMatcher _matcher = new();
    private readonly ConcurrentDictionary<string, RomMatchState> _lookupCache = new(StringComparer.Ordinal);

    /// <summary>Test seam: overrides the configuration used by artwork resolution.</summary>
    internal PluginConfiguration? ConfigOverrideForTests;

    /// <summary>Test seam: replaces the ROM checksum computation.</summary>
    internal Func<string, IReadOnlyList<uint>>? CrcOverrideForTests;

    /// <summary>Test seam invoked as a match operation starts, with (platform, crcOnly, normalized title).</summary>
    internal Action<string, bool, string?>? MatchOperationForTests;

    /// <summary>Test seam: number of index builds executed by the backing store.</summary>
    internal int BuildIndexCallCountForTests => _indexStore.BuildIndexCallCountForTests;

    /// <summary>The plugin data folder, or null when none is available.</summary>
    internal string? DataFolderPath => _indexStore.DataFolderPath;

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
            var config = ConfigOverrideForTests ?? MoonfinPlugin.Instance?.Configuration;
            if (config == null || !config.GamesMetadataEnabled || !CoreToPlatform.TryGetValue(core, out var platform))
            {
                return null;
            }

            if (!TryGetFileStamp(romPath, out var size, out var mtime))
            {
                return null;
            }

            var state = GetOrCreateState(romPath, size, mtime);
            var key = new MatchKey(platform, MatchMode.CrcThenName, NormalizeTitle(title));
            var match = await GetMatchAsync(
                state, romPath, title, key, () => Task.FromResult(GetIndexOrStartBuild(platform, config)), cancellationToken)
                .ConfigureAwait(false);
            return match?.Record;
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
        var lookup = await LookupArtworkNameAsync(candidatePlatforms, romPath, title, cancellationToken)
            .ConfigureAwait(false);
        return lookup.Resolution;
    }

    /// <summary>
    /// The shared once-per-process check of one platform's RDB for a newer upstream copy. Completes
    /// without checking when metadata is disabled or no local copy exists yet.
    /// </summary>
    internal Task<RdbCheckResult> EnsureUpdateCheckedAsync(string platform, CancellationToken cancellationToken = default)
    {
        var config = ConfigOverrideForTests ?? MoonfinPlugin.Instance?.Configuration;
        return config == null || !config.GamesMetadataEnabled
            ? Task.FromResult(RdbCheckResult.None)
            : _indexStore.EnsureUpdateCheckedAsync(platform, config).WaitAsync(cancellationToken);
    }

    /// <summary>Drops the per-ROM match cache; requests already running keep their own state.</summary>
    internal void ClearLookupCache() => _lookupCache.Clear();

    /// <summary>
    /// Fetches a newer RDB for <paramref name="platform"/> and stages it beside the live file for the
    /// next start. Returns true only when a changed, valid file was staged; never throws.
    /// </summary>
    internal Task<bool> StageUpdateAsync(string platform, CancellationToken cancellationToken = default)
    {
        var config = ConfigOverrideForTests ?? MoonfinPlugin.Instance?.Configuration;
        return config == null || !config.GamesMetadataEnabled
            ? Task.FromResult(false)
            : _indexStore.StageUpdateAsync(platform, config, cancellationToken);
    }

    internal void ClearPendingReopen(string platform, string digest) =>
        _indexStore.ClearPendingReopen(platform, digest);

    internal event Action<string>? IndexAvailable
    {
        add => _indexStore.IndexAvailable += value;
        remove => _indexStore.IndexAvailable -= value;
    }

    /// <summary>
    /// Resolves canonical artwork names while preserving whether a required RDB index is still
    /// loading. A missing index is not evidence that artwork is absent: callers may probe a raw
    /// filename fallback, but must retry rather than persist a terminal miss if it also fails.
    /// </summary>
    internal async Task<ArtworkNameLookupResult> LookupArtworkNameAsync(
        IReadOnlyList<string> candidatePlatforms,
        string romPath,
        string? title,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = ConfigOverrideForTests ?? MoonfinPlugin.Instance?.Configuration;
            return config == null
                ? ArtworkNameLookupResult.NoMatch()
                : await LookupArtworkNameCoreAsync(candidatePlatforms, romPath, title, config, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolving artwork name failed for {Path}", romPath);
            return ArtworkNameLookupResult.NoMatch();
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
        var lookup = await LookupArtworkNameCoreAsync(candidatePlatforms, romPath, title, config, cancellationToken)
            .ConfigureAwait(false);
        return lookup.Resolution;
    }

    private async Task<ArtworkNameLookupResult> LookupArtworkNameCoreAsync(
        IReadOnlyList<string> candidatePlatforms,
        string romPath,
        string? title,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (candidatePlatforms.Count == 0 || !TryGetFileStamp(romPath, out var size, out var mtime))
        {
            return ArtworkNameLookupResult.NoMatch();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = GetOrCreateState(romPath, size, mtime);
        var normalizedTitle = NormalizeTitle(title);
        var indexPending = false;
        for (var i = 0; i < candidatePlatforms.Count; i++)
        {
            var platform = candidatePlatforms[i];
            var key = i == 0
                ? new MatchKey(platform, MatchMode.CrcThenName, normalizedTitle)
                : new MatchKey(platform, MatchMode.CrcOnly, null);
            var match = await GetMatchAsync(state, romPath, title, key, () => GetIndexAsync(platform, config), cancellationToken)
                .ConfigureAwait(false);
            if (match == null)
            {
                indexPending = true;
                continue;
            }

            if (match.Record == null)
            {
                // An unreadable ROM is not a confirmed miss; defer it rather than record Missing.
                indexPending |= match.Degraded;
                continue;
            }

            var siblings = i == 0
                ? await Task.Run(() => match.Siblings.Value, cancellationToken).ConfigureAwait(false)
                : null;
            var names = BuildNameList(match.Record, siblings);
            // A name fallback can serve art now, but failed checksum work must remain retryable
            // if those names miss; it may later identify a different name or enable listing aliases.
            return ArtworkNameLookupResult.Resolved(new ArtworkNameResolution(names, platform)
            {
                PrimaryIsCrcMatch = match.ByCrc && names.Count > 0 && names[0] == match.Record.Name,
            }, isIndexPending: match.Degraded);
        }

        // An index-pending outcome is never stored: a download may complete between this call and the next one.
        return indexPending ? ArtworkNameLookupResult.IndexPending() : ArtworkNameLookupResult.NoMatch();
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

    private static string NormalizeTitle(string? title) => title == null ? string.Empty : RdbMatcher.NormalizeName(title);

    private RomMatchState GetOrCreateState(string romPath, long size, long mtime)
    {
        if (_lookupCache.TryGetValue(romPath, out var existing) && existing.HasStamp(size, mtime))
        {
            return existing;
        }

        if (!_lookupCache.ContainsKey(romPath) && _lookupCache.Count >= MaxLookupCacheEntries)
        {
            _lookupCache.Clear();
        }

        RomMatchState Create() => new(romPath, size, mtime, ComputeCrcs);
        return _lookupCache.AddOrUpdate(
            romPath,
            _ => Create(),
            (_, current) => current.HasStamp(size, mtime) ? current : Create());
    }

    private IReadOnlyList<uint> ComputeCrcs(string romPath) =>
        (CrcOverrideForTests ?? RdbMatcher.ComputeCrcCandidates)(romPath);

    // Null means the platform index is not ready; that outcome is never stored.
    private async Task<PlatformMatch?> GetMatchAsync(
        RomMatchState state,
        string romPath,
        string? title,
        MatchKey key,
        Func<Task<RdbPlatformIndex?>> getIndex,
        CancellationToken cancellationToken)
    {
        if (!state.Results.TryGetValue(key, out var entry))
        {
            var index = await getIndex().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (index == null)
            {
                return null;
            }

            entry = state.GetOrAddMatch(key, () => Task.Run(() => ComputeMatchAsync(state, romPath, title, key, index)));
        }

        return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlatformMatch> ComputeMatchAsync(
        RomMatchState state,
        string romPath,
        string? title,
        MatchKey key,
        RdbPlatformIndex index)
    {
        IReadOnlyList<uint> crcs;
        var degraded = false;
        try
        {
            crcs = await state.CurrentCrcs.Value.ConfigureAwait(false);
        }
        catch (Exception)
        {
            crcs = Array.Empty<uint>();
            degraded = true;
        }

        var crcOnly = key.Mode == MatchMode.CrcOnly;
        MatchOperationForTests?.Invoke(key.Platform, crcOnly, key.NormalizedTitle);
        var (record, byCrc) = crcOnly
            ? (RdbMatcher.MatchByCrc(index, crcs), true)
            : _matcher.MatchWithKind(index, romPath, title, crcs);
        return new PlatformMatch(
            record,
            new Lazy<IReadOnlyList<RdbRecord>>(() =>
                record == null ? Array.Empty<RdbRecord>() : _matcher.FindSiblings(index, record, MaxSiblingArtworkNamesPerRequest)),
            degraded,
            byCrc && record != null);
    }

    // These forwards preserve existing reflection-based tests while RdbMatcher owns the policy.
    private RdbRecord? Match(PlatformIndex index, string romPath, string? title) =>
        _matcher.Match(index.ToStoreIndex(), romPath, title, RdbMatcher.ComputeCrcCandidates(romPath));

    private List<RdbRecord> MatchWithSiblings(PlatformIndex index, string romPath, string? title)
    {
        var results = new List<RdbRecord>();
        var primary = Match(index, romPath, title);
        if (primary != null)
        {
            results.Add(primary);
            results.AddRange(_matcher.FindSiblings(index.ToStoreIndex(), primary, MaxSiblingArtworkNamesPerRequest));
        }

        return results;
    }

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

    private enum MatchMode
    {
        CrcThenName,
        CrcOnly,
    }

    // NormalizedTitle is null for CrcOnly, whose result does not depend on the title.
    private readonly record struct MatchKey(string Platform, MatchMode Mode, string? NormalizedTitle);

    // Record is null for a confirmed miss on exactly one MatchKey; Degraded means the checksums failed, so it is never retained.
    private sealed record PlatformMatch(RdbRecord? Record, Lazy<IReadOnlyList<RdbRecord>> Siblings, bool Degraded = false, bool ByCrc = false);

    /// <summary>
    /// Per-ROM lookup state, replaced as a whole only when the file stamp changes. Checksum work and
    /// per-scope match results are shared by every caller and run under no caller's token.
    /// </summary>
    private sealed class RomMatchState
    {
        private readonly string _romPath;
        private readonly Func<string, IReadOnlyList<uint>> _computeCrcs;
        private readonly long _size;
        private readonly long _mtime;
        private readonly ConcurrentQueue<KeyValuePair<MatchKey, Lazy<Task<PlatformMatch>>>> _completed = new();
        private Lazy<Task<IReadOnlyList<uint>>> _crcs;

        internal RomMatchState(string romPath, long size, long mtime, Func<string, IReadOnlyList<uint>> computeCrcs)
        {
            _romPath = romPath;
            _size = size;
            _mtime = mtime;
            _computeCrcs = computeCrcs;
            _crcs = NewCrcs();
        }

        internal ConcurrentDictionary<MatchKey, Lazy<Task<PlatformMatch>>> Results { get; } = new();

        internal Lazy<Task<IReadOnlyList<uint>>> CurrentCrcs => Volatile.Read(ref _crcs);

        internal bool HasStamp(long size, long mtime) => _size == size && _mtime == mtime;

        internal Lazy<Task<PlatformMatch>> GetOrAddMatch(MatchKey key, Func<Task<PlatformMatch>> start) =>
            Results.GetOrAdd(key, k => NewMatch(k, start));

        private Lazy<Task<PlatformMatch>> NewMatch(MatchKey key, Func<Task<PlatformMatch>> start)
        {
            Lazy<Task<PlatformMatch>>? self = null;
            self = new Lazy<Task<PlatformMatch>>(
                () =>
                {
                    var task = start();
                    task.ContinueWith(
                        t => OnMatchCompleted(new KeyValuePair<MatchKey, Lazy<Task<PlatformMatch>>>(key, self!), t),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return task;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
            return self;
        }

        // Runs when the shared task finishes, even if every waiter has already cancelled.
        private void OnMatchCompleted(KeyValuePair<MatchKey, Lazy<Task<PlatformMatch>>> entry, Task<PlatformMatch> task)
        {
            if (!task.IsCompletedSuccessfully || task.Result.Degraded)
            {
                _ = task.Exception;
                Results.TryRemove(entry);
                return;
            }

            // Only completed entries are queued, so in-flight work is never evicted.
            _completed.Enqueue(entry);
            while (_completed.Count > MaxCompletedMatchesPerRom && _completed.TryDequeue(out var oldest))
            {
                Results.TryRemove(oldest);
            }
        }

        private Lazy<Task<IReadOnlyList<uint>>> NewCrcs()
        {
            Lazy<Task<IReadOnlyList<uint>>>? self = null;
            self = new Lazy<Task<IReadOnlyList<uint>>>(
                () =>
                {
                    var task = Task.Run(() => _computeCrcs(_romPath));
                    task.ContinueWith(
                        t =>
                        {
                            _ = t.Exception;
                            Interlocked.CompareExchange(ref _crcs, NewCrcs(), self!);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return task;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
            return self;
        }
    }
}

/// <summary>
/// The ordered artwork names and the libretro platform that matched the ROM. PrimaryIsCrcMatch says
/// the first name came from a checksum hit rather than a file or title name.
/// </summary>
public sealed record ArtworkNameResolution(IReadOnlyList<string> Names, string Platform)
{
    public bool PrimaryIsCrcMatch { get; init; }
}

/// <summary>Canonical-name lookup result, including pending index or checksum work.</summary>
internal sealed record ArtworkNameLookupResult(ArtworkNameResolution? Resolution, bool IsIndexPending)
{
    internal static ArtworkNameLookupResult Resolved(ArtworkNameResolution resolution, bool isIndexPending = false) => new(resolution, isIndexPending);

    internal static ArtworkNameLookupResult NoMatch() => new(null, false);

    internal static ArtworkNameLookupResult IndexPending() => new(null, true);
}
