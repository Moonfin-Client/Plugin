using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for IMDb official lists.
/// The lists sync task populates this cache; the custom rows controller reads from it.
/// </summary>
public class ImdbListsCacheService
{
    private readonly string _cacheFilePath;
    private readonly ILogger<ImdbListsCacheService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ConcurrentDictionary<string, ImdbListsCacheEntry>? _cache;

    public ImdbListsCacheService(ILogger<ImdbListsCacheService> logger)
    {
        _logger = logger;
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        if (!Directory.Exists(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        _cacheFilePath = Path.Combine(dataPath, "imdb_lists_cache.json");
    }

    public List<CustomRowItem>? TryGetItems(string chartType, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(chartType, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Items;
        }
        return null;
    }

    public void SetItems(string chartType, List<CustomRowItem> items)
    {
        var cache = EnsureLoaded();
        cache[chartType] = new ImdbListsCacheEntry
        {
            Items = items,
            CachedAt = DateTimeOffset.UtcNow
        };
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
            _logger.LogDebug("IMDb lists cache flushed to disk ({Count} entries)", cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush IMDb lists cache to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private ConcurrentDictionary<string, ImdbListsCacheEntry> EnsureLoaded()
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
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, ImdbListsCacheEntry>>(stream, JsonOptions);
                    _cache = loaded != null
                        ? new ConcurrentDictionary<string, ImdbListsCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                        : new ConcurrentDictionary<string, ImdbListsCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation("IMDb lists cache loaded from disk ({Count} entries)", _cache.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load IMDb lists cache from disk, starting fresh");
                    _cache = new ConcurrentDictionary<string, ImdbListsCacheEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                _cache = new ConcurrentDictionary<string, ImdbListsCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _fileLock.Release();
        }

        return _cache;
    }
}

public class ImdbListsCacheEntry
{
    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
