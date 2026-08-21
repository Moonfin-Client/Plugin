using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Moonfin.Server.Services;

/// <summary>
/// Validates arcade ZIP contents against locally pinned FBNeo and MAME DAT files. The service is
/// deliberately offline: a client never chooses a core from an external metadata service, and a
/// changed DAT is picked up automatically without requiring a plugin restart.
/// </summary>
public sealed class ArcadeCompatibilityService
{
    public const string FbneoCore = "arcade";
    public const string MameCore = "mame";

    // Cache enough resolved arcade archives for a large library without retaining an unbounded
    // path-to-hash index for the server lifetime. Clearing on capacity is deliberately cheap:
    // a miss recomputes only when that game's detail page is opened.
    private const int MaxResolutionCacheEntries = 4096;

    private readonly string _fbneoDatPath;
    private readonly string _mameDatPath;
    private readonly object _indexLock = new();
    // Ordinal (not OrdinalIgnoreCase): keys are filesystem paths, and Jellyfin predominantly runs
    // on Linux where paths are case-sensitive, so two ROMs differing only in case must not share
    // a cache entry. Unbounded in theory but bounded in practice: one entry per arcade archive
    // ever resolved in the library. Entries are capped by CacheResolution below.
    private readonly ConcurrentDictionary<string, CachedResolution> _resolutionCache = new(StringComparer.Ordinal);
    private readonly DatIndexSlot _fbneo = new();
    private readonly DatIndexSlot _mame = new();
    private readonly ConcurrentDictionary<string, CachedFileHash> _datHashes = new(StringComparer.Ordinal);

    // Test-only counter: how many ArcadeDatSet candidates ArcadeDatIndex.Matches ran the full
    // multiset comparison against, summed across every resolve this instance has done. Lets a
    // test assert the hash-probe design stays O(1) in DAT size without depending on wall-clock
    // timing. Never read by production code.
    private int _candidateComparisonCount;

    /// <summary>Test seam: total DAT-set candidates compared across all resolves.</summary>
    internal int CandidateComparisonCountForTests => _candidateComparisonCount;

    /// <summary>Creates the service using a plugin data directory supplied by DI.</summary>
    public ArcadeCompatibilityService(string pluginDataPath)
        : this(
            Path.Combine(pluginDataPath, "arcade-dats", "fbneo.dat"),
            Path.Combine(pluginDataPath, "arcade-dats", "mame.dat"))
    {
    }

    // Internal so focused tests can use tiny fixture DATs without a plugin instance.
    internal ArcadeCompatibilityService(string? fbneoDatPath, string? mameDatPath)
    {
        ArgumentNullException.ThrowIfNull(fbneoDatPath);
        ArgumentNullException.ThrowIfNull(mameDatPath);
        _fbneoDatPath = fbneoDatPath;
        _mameDatPath = mameDatPath;
    }

    /// <summary>
    /// Synchronous compatibility wrapper for <see cref="ResolveAsync"/>. Test-only convenience;
    /// production request paths must use the asynchronous method so cold DAT parsing never blocks
    /// a server worker.
    /// </summary>
    internal ArcadeCoreResolution Resolve(string archivePath, string fallbackCore, CancellationToken cancellationToken = default) =>
        ResolveAsync(archivePath, fallbackCore, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously resolves an arcade archive. DAT parsing and waiting for another cold parse
    /// do not block ASP.NET request threads. <paramref name="cancellationToken"/> is checked
    /// during DAT parsing and for every archive-hash buffer, so cancelled requests can abandon
    /// substantial work instead of always running to completion.
    /// </summary>
    public async Task<ArcadeCoreResolution> ResolveAsync(
        string archivePath,
        string fallbackCore,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stamp = FileStamp.From(archivePath);
        if (_resolutionCache.TryGetValue(archivePath, out var cached) && cached.Stamp == stamp)
        {
            return cached.Resolution;
        }

        var indexes = await Task.WhenAll(
            GetIndexAsync(_fbneoDatPath, _fbneo, cancellationToken),
            GetIndexAsync(_mameDatPath, _mame, cancellationToken)).ConfigureAwait(false);
        var fbneoResult = indexes[0];
        var mameResult = indexes[1];
        var fbneo = fbneoResult.Index;
        var mame = mameResult.Index;
        if (fbneo == null && mame == null)
        {
            var unavailable = new ArcadeCoreResolution(
                fallbackCore,
                [fallbackCore],
                "No arcade compatibility DAT is installed; using the system default.",
                FallbackContentKey(archivePath),
                false);

            // Only cache "no DAT file exists", a stable state. A Stale result means the load
            // raced with an admin replacing a pinned snapshot (or the wait was cancelled); that
            // is transient, so this request gets the fallback but the next request retries the
            // load instead of being stuck with a cached wrong answer.
            if (!fbneoResult.Stale && !mameResult.Stale)
            {
                CacheResolution(archivePath, new CachedResolution(stamp, unavailable));
            }

            return unavailable;
        }

        var contents = await Task.Run(
            () => ArcadeArchiveHasher.Read(archivePath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var available = new List<string>(2);
        var compared = 0;
        if (fbneo != null)
        {
            if (fbneo.Matches(contents, out var fbneoCompared))
            {
                available.Add(FbneoCore);
            }

            compared += fbneoCompared;
        }

        if (mame != null)
        {
            if (mame.Matches(contents, out var mameCompared))
            {
                available.Add(MameCore);
            }

            compared += mameCompared;
        }

        Interlocked.Add(ref _candidateComparisonCount, compared);

        // FBNeo is intentionally preferred when both pinned DATs validate the exact archive.
        var recommended = available.Contains(FbneoCore, StringComparer.Ordinal)
            ? FbneoCore
            : available.FirstOrDefault() ?? fallbackCore;
        var reason = available.Count switch
        {
            2 => "Validated against both installed FBNeo and MAME DATs; FBNeo is preferred.",
            1 when recommended == FbneoCore => "Validated against the installed FBNeo DAT.",
            1 => "Validated against the installed MAME DAT.",
            _ => "This archive does not match an installed FBNeo or MAME DAT."
        };
        var resolution = new ArcadeCoreResolution(recommended, available, reason, contents.ContentKey, true);
        CacheResolution(archivePath, new CachedResolution(stamp, resolution));
        return resolution;
    }

    /// <summary>
    /// Consults only the in-memory resolution cache for a previously computed result, keyed by
    /// archive path and validated against the archive's current <see cref="FileStamp"/>. Never
    /// reads or hashes the archive itself (no <see cref="ArcadeArchiveHasher.Read"/> call): a caller on
    /// a cost-sensitive path (e.g. a thumbnail request fired once per poster on the browse screen)
    /// can use this to reuse a prior <see cref="Resolve"/> result without risking a fresh archive
    /// hash. Returns <see langword="false"/> (with <paramref name="resolution"/> as
    /// <c>default!</c>) on a cache miss or when the cached entry's stamp no longer matches the
    /// file on disk (changed size/write time, or the file no longer exists).
    /// </summary>
    public bool TryGetCached(string archivePath, out ArcadeCoreResolution resolution)
    {
        var stamp = FileStamp.From(archivePath);
        if (_resolutionCache.TryGetValue(archivePath, out var cached) && cached.Stamp == stamp)
        {
            resolution = cached.Resolution;
            return true;
        }

        resolution = default!;
        return false;
    }

    /// <summary>Reports which explicitly provisioned, local DAT snapshots are active.</summary>
    public ArcadeCompatibilityStatus GetStatus()
    {
        var fbneoStamp = FileStamp.From(_fbneoDatPath);
        var mameStamp = FileStamp.From(_mameDatPath);
        return new(
            fbneoStamp.Exists,
            mameStamp.Exists,
            _fbneoDatPath,
            _mameDatPath,
            GetCachedFileSha256(_fbneoDatPath, fbneoStamp),
            GetCachedFileSha256(_mameDatPath, mameStamp));
    }

    /// <summary>
    /// Installs one administrator-supplied DAT snapshot. No network fetching is performed; the
    /// caller controls the exact core version and source revision represented by this file.
    /// </summary>
    public async Task InstallDatAsync(string core, Stream source, CancellationToken cancellationToken)
    {
        var path = core switch
        {
            FbneoCore => _fbneoDatPath,
            MameCore => _mameDatPath,
            _ => throw new ArgumentException("Only arcade (FBNeo) and mame DATs are supported.", nameof(core))
        };

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            // Reject malformed/empty uploads before replacing a working pinned index. Parsing
            // successfully as XML is not sufficient: a document with no <game>/<machine>
            // elements, or one whose elements carry no usable sha1/crc, parses cleanly but
            // yields zero sets and would otherwise silently disable arcade validation.
            var candidate = await Task.Run(
                () => ArcadeDatIndexLoader.Load(temporary, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (candidate.SetCount == 0)
            {
                throw new EmptyArcadeDatException(
                    "The uploaded DAT contains no usable game or machine sets.", nameof(source));
            }

            File.Move(temporary, path, overwrite: true);
            lock (_indexLock)
            {
                _fbneo.Reset();
                _mame.Reset();
                _resolutionCache.Clear();
            }
            _datHashes.TryRemove(path, out _);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<IndexLoadResult> GetIndexAsync(string path, DatIndexSlot slot, CancellationToken cancellationToken)
    {
        var stamp = FileStamp.From(path);
        lock (_indexLock)
        {
            if (stamp == slot.Stamp)
            {
                return new IndexLoadResult(slot.Index, false);
            }
        }

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after waiting: a concurrent request may already have published a complete
            // index for this exact DAT revision.
            stamp = FileStamp.From(path);
            lock (_indexLock)
            {
                if (stamp == slot.Stamp)
                {
                    return new IndexLoadResult(slot.Index, false);
                }
            }

            var loaded = stamp.Exists
                ? await Task.Run(() => ArcadeDatIndexLoader.Load(path, cancellationToken), cancellationToken).ConfigureAwait(false)
                : null;
            lock (_indexLock)
            {
                // Do not publish an index read from a DAT that changed while the parse ran (for
                // example, an administrator replacing a pinned snapshot). The next request sees
                // the current stamp and loads that complete revision instead. Report this as
                // Stale so ResolveAsync's caller does not cache a spurious "no DAT installed".
                if (FileStamp.From(path) != stamp)
                {
                    return new IndexLoadResult(null, true);
                }

                slot.Stamp = stamp;
                slot.Index = loaded;
                // A stamp mismatch reaching here means the DAT itself changed, so previously
                // resolved archives may now match a different core.
                _resolutionCache.Clear();
                return new IndexLoadResult(loaded, false);
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private static string FallbackContentKey(string archivePath)
    {
        // Do not hash every archive on the library-list path before a DAT is installed. Core
        // overrides are rejected until validation is active, so this is diagnostic-only.
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(archivePath)))).ToLowerInvariant();
    }

    private void CacheResolution(string archivePath, CachedResolution resolution)
    {
        if (!_resolutionCache.ContainsKey(archivePath) &&
            _resolutionCache.Count >= MaxResolutionCacheEntries)
        {
            _resolutionCache.Clear();
        }

        _resolutionCache[archivePath] = resolution;
    }

    private string? GetCachedFileSha256(string path, FileStamp stamp)
    {
        if (_datHashes.TryGetValue(path, out var cached) && cached.Stamp == stamp)
        {
            return cached.Hash;
        }

        string? hash;
        try
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            hash = null;
        }

        _datHashes[path] = new CachedFileHash(stamp, hash);
        return hash;
    }

    private sealed record CachedResolution(FileStamp Stamp, ArcadeCoreResolution Resolution);
    private sealed record CachedFileHash(FileStamp Stamp, string? Hash);

    private sealed class DatIndexSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ArcadeDatIndex? Index { get; set; }
        public FileStamp Stamp { get; set; }

        public void Reset()
        {
            Index = null;
            Stamp = default;
        }
    }

    // Stale means the load raced with the DAT file changing mid-parse (or was otherwise not
    // published); the caller must not treat that as a stable "no DAT installed" answer.
    private readonly record struct IndexLoadResult(ArcadeDatIndex? Index, bool Stale);

    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteUtc)
    {
        public static FileStamp From(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileStamp(true, info.Length, info.LastWriteTimeUtc)
                    : default;
            }
            catch
            {
                return default;
            }
        }
    }
}

/// <summary>
/// Thrown when an uploaded DAT parses as well-formed XML but contains no usable game/machine
/// sets. Kept as a distinct type (rather than a bare <see cref="ArgumentException"/>) so callers,
/// such as <c>GamesController</c>, can map it to a 400 response with a message different from
/// both "not valid XML" (<see cref="System.Xml.XmlException"/>) and "unknown core name" (a plain
/// <see cref="ArgumentException"/> thrown earlier in <see cref="ArcadeCompatibilityService.InstallDatAsync"/>).
/// </summary>
public sealed class EmptyArcadeDatException : ArgumentException
{
    public EmptyArcadeDatException(string message, string? paramName)
        : base(message, paramName)
    {
    }
}

/// <summary>Server-side arcade core choice plus a stable archive-content key for user preferences.</summary>
public sealed record ArcadeCoreResolution(
    string RecommendedCore,
    IReadOnlyList<string> AvailableCores,
    string Reason,
    string ContentKey,
    bool IsValidated);

/// <summary>Administrative status for the two local, pinned compatibility sources.</summary>
public sealed record ArcadeCompatibilityStatus(
    bool FbneoDatInstalled,
    bool MameDatInstalled,
    string FbneoDatPath,
    string MameDatPath,
    string? FbneoDatSha256,
    string? MameDatSha256);
