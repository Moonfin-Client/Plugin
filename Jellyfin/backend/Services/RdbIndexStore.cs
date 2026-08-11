using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Owns local RDB files, asynchronous download lifecycle, and per-platform parsed indexes.
/// </summary>
internal sealed class RdbIndexStore
{
    private static readonly TimeSpan DownloadRetryBackoff = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RdbService> _logger;
    private readonly string? _dataFolderPathOverride;
    private readonly ConcurrentDictionary<string, RdbPlatformIndex> _indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexBuildGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _downloadRetryAfterUtc = new(StringComparer.Ordinal);

    // Backoff for a file that exists locally but fails to *parse* (corrupt/truncated .rdb, or one
    // exceeding RdbReader.MaxRdbFileBytes) -- kept separate from _downloadRetryAfterUtc, which only
    // gates re-downloading a MISSING file. Deliberately narrow: only FormatException/
    // RdbTooLargeException (genuine format problems) set this. A transient I/O failure, such as
    // the file being briefly locked by a concurrent download, must NOT set it -- the very next call
    // has to retry immediately once the lock clears (see
    // GetIndexAsync_RecoversAfterATransientBuildFailure), otherwise a real "just finished
    // downloading" race would be needlessly punished with the same 5-minute wait as a truly
    // corrupt file.
    private readonly ConcurrentDictionary<string, DateTime> _parseRetryAfterUtc = new(StringComparer.Ordinal);
    private int _buildIndexCallCount;

    internal RdbIndexStore(IHttpClientFactory httpClientFactory, ILogger<RdbService> logger, string? dataFolderPathOverride)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _dataFolderPathOverride = dataFolderPathOverride;
    }

    internal int BuildIndexCallCountForTests => _buildIndexCallCount;

    internal Action? BuildIndexAboutToRunForTests { get; set; }

    internal event Action<string>? IndexAvailable;

    internal RdbPlatformIndex? GetIndexOrStartBuild(string platform, PluginConfiguration config)
    {
        if (_indexes.TryGetValue(platform, out var existing))
        {
            return existing;
        }

        _ = ObserveIndexBuildAsync(platform, config);
        return null;
    }

    internal async Task<RdbPlatformIndex?> GetIndexAsync(string platform, PluginConfiguration config)
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

        // A previous parse attempt on this exact file failed with a genuine format problem
        // (corrupt/truncated/oversized). Skip re-parsing it until the backoff window elapses,
        // rather than hot-looping BuildIndex on every lookup for a busy library.
        if (_parseRetryAfterUtc.TryGetValue(platform, out var parseRetryAfter) && DateTime.UtcNow < parseRetryAfter)
        {
            return null;
        }

        var gate = _indexBuildGates.GetOrAdd(platform, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_indexes.TryGetValue(platform, out existing))
            {
                return existing;
            }

            RdbPlatformIndex built;
            try
            {
                built = await Task.Run(() =>
                {
                    BuildIndexAboutToRunForTests?.Invoke();
                    return BuildIndexTracked(localPath);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FormatException or RdbTooLargeException)
            {
                _parseRetryAfterUtc[platform] = DateTime.UtcNow + DownloadRetryBackoff;
                _logger.LogDebug(ex, "Parsing game metadata index failed for {Platform}", platform);
                return null;
            }

            _indexes[platform] = built;
            _parseRetryAfterUtc.TryRemove(platform, out _);
            IndexAvailable?.Invoke(platform);
            return built;
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task EnsureDownloadedAsync(string platform, PluginConfiguration config)
    {
        var localPath = LocalPath(platform);
        if (localPath == null || File.Exists(localPath))
        {
            return Task.CompletedTask;
        }

        if (_downloadRetryAfterUtc.TryGetValue(platform, out var retryAfter) && DateTime.UtcNow < retryAfter)
        {
            return Task.CompletedTask;
        }

        var lazy = _downloads.GetOrAdd(
            platform,
            _ => new Lazy<Task>(() => DownloadAsync(platform, localPath, config)));
        return AwaitDownloadAsync(platform, lazy);
    }

    private async Task ObserveIndexBuildAsync(string platform, PluginConfiguration config)
    {
        try
        {
            await GetIndexAsync(platform, config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Building game metadata index failed for {Platform}", platform);
        }
    }

    private RdbPlatformIndex BuildIndexTracked(string path)
    {
        Interlocked.Increment(ref _buildIndexCallCount);
        return BuildIndex(path);
    }

    private static RdbPlatformIndex BuildIndex(string path)
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
                byName[RdbMatcher.NormalizeName(Path.GetFileNameWithoutExtension(rom))] = record;
            }

            if (record.Name is { } name)
            {
                byName[RdbMatcher.NormalizeName(name)] = record;
            }
        }

        return new RdbPlatformIndex(byCrc, byName);
    }

    private void EnsureDownloaded(string platform, PluginConfiguration config) =>
        _ = EnsureDownloadedAsync(platform, config);

    private async Task AwaitDownloadAsync(string platform, Lazy<Task> download)
    {
        try
        {
            await download.Value.ConfigureAwait(false);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task>>>)_downloads)
                .Remove(new KeyValuePair<string, Lazy<Task>>(platform, download));
        }
    }

    private async Task DownloadAsync(string platform, string localPath, PluginConfiguration config)
    {
        var baseLocation = config.GamesMetadataDbUrlBase;
        if (string.IsNullOrWhiteSpace(baseLocation))
        {
            return;
        }

        try
        {
            if (!baseLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var source = Path.Combine(baseLocation, platform + ".rdb");
                if (File.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    File.Copy(source, localPath, overwrite: true);
                    await GetIndexAsync(platform, config).ConfigureAwait(false);
                }

                _downloadRetryAfterUtc.TryRemove(platform, out _);
                return;
            }

            var url = baseLocation.TrimEnd('/') + "/" + Uri.EscapeDataString(platform) + ".rdb";
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            // GamesMetadataDbUrlBase is an admin-configurable mirror URL, so a misconfigured or
            // hostile mirror is in scope: GetByteArrayAsync alone has no size cap, unlike
            // RdbReader.ReadAll's local-file ceiling. Content-Length is only a fast pre-check (the
            // mirror can omit or understate it); CopyToLimitedAsync below enforces the real ceiling
            // against actual bytes received, mirroring GamesService.CopyToLimited's role for ROM
            // extraction.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > RdbReader.MaxRdbFileBytes)
            {
                throw new RdbTooLargeException(contentLength, RdbReader.MaxRdbFileBytes);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var downloaded = new MemoryStream();
            await CopyToLimitedAsync(responseStream, downloaded, RdbReader.MaxRdbFileBytes).ConfigureAwait(false);
            var data = downloaded.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            var temp = localPath + ".tmp";
            await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
            File.Move(temp, localPath, overwrite: true);
            _downloadRetryAfterUtc.TryRemove(platform, out _);
            _logger.LogInformation("Downloaded game metadata for {Platform} ({Bytes} bytes)", platform, data.Length);
            await GetIndexAsync(platform, config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _downloadRetryAfterUtc[platform] = DateTime.UtcNow + DownloadRetryBackoff;
            _logger.LogDebug(ex, "Downloading .rdb for {Platform} failed", platform);
        }
    }

    // Async counterpart to GamesService.CopyToLimited: copies at most maxBytes from source to
    // destination, throwing RdbTooLargeException the moment more data appears than the ceiling
    // allows. This is the real enforcement point for a download -- it bounds memory even when the
    // response understates (or omits) Content-Length, which the pre-check above alone would not
    // catch.
    private static async Task CopyToLimitedAsync(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new RdbTooLargeException(total, maxBytes);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private string? LocalPath(string platform)
    {
        var dataFolder = _dataFolderPathOverride ?? MoonfinPlugin.Instance?.DataFolderPath;
        return string.IsNullOrWhiteSpace(dataFolder)
            ? null
            : Path.Combine(dataFolder, "gamemeta", platform + ".rdb");
    }
}
