using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Shared scaffold for the file-backed JSON caches: data-path resolution, serializer
/// options, flush-to-disk, and the lazy double-checked load. Subclasses add their own
/// typed accessors over the dictionary returned by <see cref="EnsureLoaded"/>.
/// </summary>
/// <typeparam name="TEntry">The cache entry type persisted as the dictionary value.</typeparam>
public abstract class FileBackedCacheService<TEntry>
    where TEntry : class
{
    private readonly string _cacheFilePath;
    private readonly string _displayName;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ConcurrentDictionary<string, TEntry>? _cache;

    protected FileBackedCacheService(ILogger logger, string cacheFileName, string displayName)
    {
        _logger = logger;
        _displayName = displayName;
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        if (!Directory.Exists(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        _cacheFilePath = Path.Combine(dataPath, cacheFileName);
    }

    public async Task FlushAsync()
    {
        var cache = _cache;
        if (cache == null) return;

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(_cacheFilePath);
            await JsonSerializer.SerializeAsync(stream, cache, JsonOptions).ConfigureAwait(false);
            _logger.LogDebug("{CacheName} cache flushed to disk ({Count} entries)", _displayName, cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush {CacheName} cache to disk", _displayName);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Empties the cache and writes the cleared state to disk so it survives a restart.
    /// Returns how many entries were dropped.
    /// </summary>
    public async Task<int> ClearAsync()
    {
        var cache = EnsureLoaded();
        var removed = cache.Count;
        cache.Clear();
        await FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("{CacheName} cache cleared ({Count} entries removed)", _displayName, removed);
        return removed;
    }

    protected ConcurrentDictionary<string, TEntry> EnsureLoaded()
    {
        if (_cache != null) return _cache;

        _fileLock.Wait();
        try
        {
            if (_cache != null) return _cache;

            if (File.Exists(_cacheFilePath))
            {
                try
                {
                    using var stream = File.OpenRead(_cacheFilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, TEntry>>(stream, JsonOptions);
                    _cache = loaded != null
                        ? new ConcurrentDictionary<string, TEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                        : new ConcurrentDictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation("{CacheName} cache loaded from disk ({Count} entries)", _displayName, _cache.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load {CacheName} cache from disk, starting fresh", _displayName);
                    _cache = new ConcurrentDictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                _cache = new ConcurrentDictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _fileLock.Release();
        }

        return _cache;
    }
}
