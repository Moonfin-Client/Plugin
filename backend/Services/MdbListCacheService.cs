using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for MDBList ratings.
/// Stores all ratings unfiltered, keyed by "movie:tmdbId" or "show:tmdbId".
/// The batch task populates this cache; the controller reads from it.
/// </summary>
public class MdbListCacheService
{
    private readonly string _cacheFilePath;
    private readonly ILogger<MdbListCacheService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ConcurrentDictionary<string, MdbListCacheEntry>? _cache;

    public MdbListCacheService(ILogger<MdbListCacheService> logger)
    {
        _logger = logger;
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        if (!Directory.Exists(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        _cacheFilePath = Path.Combine(dataPath, "mdblist_cache.json");
    }

    public List<MdbListRating>? TryGet(string cacheKey, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(cacheKey, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Ratings;
        }
        return null;
    }

    public void Set(string cacheKey, List<MdbListRating> ratings)
    {
        var cache = EnsureLoaded();
        cache[cacheKey] = new MdbListCacheEntry
        {
            Ratings = ratings,
            CachedAt = DateTimeOffset.UtcNow
        };
    }

    public void SetMany(Dictionary<string, List<MdbListRating>> items)
    {
        var cache = EnsureLoaded();
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, ratings) in items)
        {
            cache[key] = new MdbListCacheEntry
            {
                Ratings = ratings,
                CachedAt = now
            };
        }
    }

    public HashSet<string> GetFreshKeys(TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in cache)
        {
            if (entry.CachedAt >= cutoff)
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    public async Task FlushAsync()
    {
        var cache = _cache;
        if (cache == null) return;

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(cache, JsonOptions);
            await File.WriteAllTextAsync(_cacheFilePath, json).ConfigureAwait(false);
            _logger.LogDebug("MDBList cache flushed to disk ({Count} entries)", cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush MDBList cache to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private ConcurrentDictionary<string, MdbListCacheEntry> EnsureLoaded()
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
                    var json = File.ReadAllText(_cacheFilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, MdbListCacheEntry>>(json, JsonOptions);
                    _cache = loaded != null
                        ? new ConcurrentDictionary<string, MdbListCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                        : new ConcurrentDictionary<string, MdbListCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation("MDBList cache loaded from disk ({Count} entries)", _cache.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load MDBList cache from disk, starting fresh");
                    _cache = new ConcurrentDictionary<string, MdbListCacheEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                _cache = new ConcurrentDictionary<string, MdbListCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _fileLock.Release();
        }

        return _cache;
    }
}

internal class MdbListCacheEntry
{
    [JsonPropertyName("ratings")]
    public List<MdbListRating> Ratings { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
