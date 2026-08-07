using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Resolves, downloads, and caches the original libretro artwork files.
/// </summary>
internal sealed class GameArtworkStore
{
    private const int MaxMissEntries = 6096;
    private const int MaxFuzzyDirectorySuggestions = 3;
    private const int MaxTotalProbesPerRequest = RdbService.MaxSiblingArtworkNamesPerRequest + 3;
    private static readonly TimeSpan MissTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan OverallRequestBudget = TimeSpan.FromSeconds(3);
    private static readonly char[] ReservedChars = { '&', '*', '/', ':', '`', '<', '>', '?', '\\', '|', '"' };

    // Ceiling on a single artwork response body, mirroring GamesService.MaxExtractedRomBytes and
    // GamesController.MaxDatUploadBytes: an explicit, documented limit rather than HttpClient's
    // ~2 GB default response buffer. ProbeTimeout bounds how long a *stalled* upstream can hold a
    // request, not how many bytes a *fast* one can push: thumbnails.libretro.com is remote,
    // third-party, and reachable through a configurable mirror, so a compromised or misconfigured
    // origin could otherwise stream hundreds of megabytes into the large object heap well inside
    // six seconds, once per candidate name per request. Real libretro boxarts/snaps/titles are
    // tens to a few hundred KB; the largest in the set are a couple of MB, so this is generous for
    // genuine art while bounding worst-case memory and disk per probe.
    internal const long MaxArtworkBytes = 16L * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly RdbService _rdbService;
    private readonly string _cacheDir;
    private readonly long _maxArtworkBytes;
    private readonly ConcurrentDictionary<string, DateTime> _misses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<GameArtworkLookupResult>>> _inFlight = new(StringComparer.Ordinal);

    // maxArtworkBytes defaults to MaxArtworkBytes; the parameter exists so a focused test can drive
    // the cap with a few kilobytes instead of pushing a real 16 MB body through a fake handler
    // (mirroring ArcadeArchiveHasher.Read's maxEntryBytes and RdbReader.ReadAll's maxFileBytes seams).
    internal GameArtworkStore(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        RdbService rdbService,
        string cacheDir,
        long maxArtworkBytes = MaxArtworkBytes)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _rdbService = rdbService;
        _cacheDir = cacheDir;
        _maxArtworkBytes = maxArtworkBytes;
    }

    internal async Task<string?> GetThumbPathAsync(
        string core,
        bool coreWasDefaulted,
        string systemName,
        string romPath,
        string? title,
        GameThumbService.ThumbKind kind,
        CancellationToken cancellationToken)
    {
        var result = await LookupThumbAsync(core, coreWasDefaulted, systemName, romPath, title, kind, cancellationToken)
            .ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new ThumbLookupTimedOutException(romPath, OverallRequestBudget);
        }

        return result.Path;
    }

    internal async Task<GameArtworkLookupResult> LookupThumbAsync(
        string core,
        bool coreWasDefaulted,
        string systemName,
        string romPath,
        string? title,
        GameThumbService.ThumbKind kind,
        CancellationToken cancellationToken)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(OverallRequestBudget);

        try
        {
            return await GetThumbPathCoreAsync(core, coreWasDefaulted, systemName, romPath, title, kind, budgetCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Thumbnail lookup for {RomPath} exceeded its {BudgetSeconds}s overall budget; giving up",
                romPath,
                OverallRequestBudget.TotalSeconds);
            return GameArtworkLookupResult.TransientFailure(OverallRequestBudget, timedOut: true);
        }
    }

    private async Task<GameArtworkLookupResult> GetThumbPathCoreAsync(
        string core,
        bool coreWasDefaulted,
        string systemName,
        string romPath,
        string? title,
        GameThumbService.ThumbKind kind,
        CancellationToken cancellationToken)
    {
        RdbService.TryGetPlatform(core, out var primaryPlatform);
        var fuzzyDirectoryCores = GamesService.FuzzySuggestCores(systemName, MaxFuzzyDirectorySuggestions);
        var candidatePlatforms = RdbService.GetCandidatePlatforms(core, coreWasDefaulted, fuzzyDirectoryCores);

        var romFileName = Path.GetFileName(romPath);
        var candidates = new List<(string Name, string Platform)>(3 + RdbService.MaxSiblingArtworkNamesPerRequest);
        var resolved = await _rdbService
            .ResolveArtworkNameAsync(candidatePlatforms, romPath, title, cancellationToken)
            .ConfigureAwait(false);
        if (resolved is { } match)
        {
            foreach (var candidateName in match.Names)
            {
                if (string.IsNullOrWhiteSpace(candidateName))
                {
                    continue;
                }

                var name = LibretroThumbName(candidateName);
                if (name.Length > 0)
                {
                    candidates.Add((name, match.Platform));
                }
            }
        }

        if (!string.IsNullOrEmpty(primaryPlatform))
        {
            var fileNameCandidate = LibretroThumbName(romFileName);
            if (fileNameCandidate.Length > 0)
            {
                candidates.Add((fileNameCandidate, primaryPlatform));
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                var titleCandidate = LibretroThumbName(title);
                if (titleCandidate.Length > 0)
                {
                    candidates.Add((titleCandidate, primaryPlatform));
                }
            }
        }

        var probesRemaining = MaxTotalProbesPerRequest;
        var sawTransientFailure = false;
        TimeSpan? retryDelay = null;
        foreach (var (name, platform) in candidates.Distinct())
        {
            if (probesRemaining <= 0)
            {
                break;
            }

            probesRemaining--;
            var lookup = await GetThumbPathForNameAsync(platform, name, kind, cancellationToken).ConfigureAwait(false);
            if (lookup.Outcome == GameArtworkLookupOutcome.Found)
            {
                return lookup;
            }

            if (lookup.Outcome == GameArtworkLookupOutcome.TransientFailure)
            {
                sawTransientFailure = true;
                retryDelay ??= lookup.RetryDelay;
            }
        }

        return sawTransientFailure
            ? GameArtworkLookupResult.TransientFailure(retryDelay)
            : GameArtworkLookupResult.Missing();
    }

    private async Task<GameArtworkLookupResult> GetThumbPathForNameAsync(
        string platform,
        string name,
        GameThumbService.ThumbKind kind,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey(platform, kind, name);
        if (_misses.TryGetValue(cacheKey, out var missedAt) && DateTime.UtcNow - missedAt < MissTtl)
        {
            return GameArtworkLookupResult.Missing();
        }

        var localPath = Path.Combine(_cacheDir, cacheKey + ".png");
        if (File.Exists(localPath))
        {
            return GameArtworkLookupResult.Found(localPath);
        }

        // The download deliberately does NOT take the caller's token: it is shared by every waiter
        // on this cache key, and the caller's token carries a per-request 3s budget
        // (LookupThumbAsync) that is much shorter than the download's own ProbeTimeout. Cancelling
        // a shared download because one awaiter gave up would abandon the other waiters and make
        // the next request start the whole transfer again.
        var lazy = _inFlight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<GameArtworkLookupResult>>(
                () => DownloadAsync(platform, kind, name, cacheKey, localPath, CancellationToken.None)));

        // Drop the entry when the *download* finishes, not when this awaiter stops waiting. An
        // awaiter that hits its budget leaves the transfer running; removing the entry here would
        // let the next request start a second, concurrent download of the same URL. Mirrors
        // DerivedThumbnailService's completion-based removal.
        _ = lazy.Value.ContinueWith(
            _ => ((ICollection<KeyValuePair<string, Lazy<Task<GameArtworkLookupResult>>>>)_inFlight)
                .Remove(new KeyValuePair<string, Lazy<Task<GameArtworkLookupResult>>>(cacheKey, lazy)),
            TaskScheduler.Default);

        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GameArtworkLookupResult> DownloadAsync(
        string platform,
        GameThumbService.ThumbKind kind,
        string name,
        string cacheKey,
        string localPath,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(platform, kind, name);
        try
        {
            // ProbeTimeout as a token, not just as HttpClient.Timeout: with ResponseHeadersRead the
            // client timeout covers only the response headers, so without this the body read below
            // would have no deadline at all.
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(ProbeTimeout);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = ProbeTimeout;
            client.MaxResponseContentBufferSize = _maxArtworkBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AddMiss(cacheKey);
                return GameArtworkLookupResult.Missing();
            }

            if (!response.IsSuccessStatusCode)
            {
                return GameArtworkLookupResult.TransientFailure(RetryDelayFor(response));
            }

            if (response.Content.Headers.ContentLength > _maxArtworkBytes)
            {
                return OversizedArtwork(kind, name, response.Content.Headers.ContentLength);
            }

            return await SaveArtworkAsync(response, kind, name, localPath, downloadCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Downloading {Kind} art for {Name} failed", kind, name);
            return GameArtworkLookupResult.TransientFailure();
        }
    }

    /// <summary>
    /// Streams the response body to a per-attempt temp file and atomically promotes it to
    /// <paramref name="localPath"/>, rejecting anything past the size cap.
    /// </summary>
    private async Task<GameArtworkLookupResult> SaveArtworkAsync(
        HttpResponseMessage response,
        GameThumbService.ThumbKind kind,
        string name,
        string localPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDir);

        // Unique per attempt. A shared "<localPath>.tmp" lets two attempts at the same artwork
        // write the same file: one of them fails with an IOException (reported as a bogus transient
        // failure), or -- far worse -- a half-written PNG gets promoted by File.Move and then
        // served forever, because the File.Exists(localPath) fast path never revalidates it.
        var temp = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var promoted = false;
        try
        {
            var written = await CopyCappedAsync(response, temp, cancellationToken).ConfigureAwait(false);
            if (written < 0)
            {
                return OversizedArtwork(kind, name, null);
            }

            File.Move(temp, localPath, overwrite: true);
            promoted = true;
            return GameArtworkLookupResult.Found(localPath);
        }
        finally
        {
            if (!promoted)
            {
                TryDeleteTemp(temp);
            }
        }
    }

    /// <summary>
    /// Copies the response body into <paramref name="temp"/>, stopping as soon as it passes the
    /// cap. Returns the byte count written, or -1 when the body exceeded the cap.
    /// </summary>
    private async Task<long> CopyCappedAsync(HttpResponseMessage response, string temp, CancellationToken cancellationToken)
    {
        // A declared Content-Length is only a fast pre-check -- it can be absent, or simply lie --
        // so the real ceiling is enforced here against the bytes actually delivered.
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (destination.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > _maxArtworkBytes)
                    {
                        return -1;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                return total;
            }
        }
    }

    private GameArtworkLookupResult OversizedArtwork(GameThumbService.ThumbKind kind, string name, long? declaredLength)
    {
        // Deliberately transient rather than Missing: an oversized body says the upstream (or a
        // configured mirror) is broken or hostile, not that this game has no art, and a Missing
        // here would be negatively cached by clients that would then never ask again. Retry cost
        // is bounded by the cap itself.
        _logger.LogWarning(
            "{Kind} art for {Name} exceeds the {MaxBytes}-byte cap (declared length {DeclaredLength}); refusing it",
            kind,
            name,
            _maxArtworkBytes,
            declaredLength);
        return GameArtworkLookupResult.TransientFailure();
    }

    private void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Removing the partial artwork download at {Temp} failed", temp);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Removing the partial artwork download at {Temp} failed", temp);
        }
    }

    private static TimeSpan? RetryDelayFor(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
    }

    private void AddMiss(string cacheKey)
    {
        if (!_misses.ContainsKey(cacheKey) && _misses.Count >= MaxMissEntries)
        {
            _misses.Clear();
        }

        _misses[cacheKey] = DateTime.UtcNow;
    }

    private static string BuildUrl(string platform, GameThumbService.ThumbKind kind, string name) =>
        "https://thumbnails.libretro.com/"
        + Uri.EscapeDataString(platform) + "/" + FolderFor(kind) + "/"
        + Uri.EscapeDataString(name) + ".png";

    private static string FolderFor(GameThumbService.ThumbKind kind) => kind switch
    {
        GameThumbService.ThumbKind.Snap => "Named_Snaps",
        GameThumbService.ThumbKind.Title => "Named_Titles",
        _ => "Named_Boxarts"
    };

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

    private static string CacheKey(string platform, GameThumbService.ThumbKind kind, string name)
    {
        // DO NOT change this algorithm. It looks like a gratuitous inconsistency with
        // ArtworkCatalogKey.Hash (SHA-256) for the same job, but this value is the on-disk
        // filename: GetThumbPathForNameAsync reads and writes _cacheDir/<key>.png. Switching the
        // hash renames every cache entry, so every already-downloaded thumbnail on every deployed
        // server misses and re-downloads from libretro, and the old files are orphaned with no
        // record of their names to clean them up.
        //
        // It is not a security boundary either way -- a collision yields a wrong thumbnail -- so
        // there is no reason to pay that cost. Two algorithms is the correct answer here.
        var raw = platform + "|" + FolderFor(kind) + "|" + name;
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
