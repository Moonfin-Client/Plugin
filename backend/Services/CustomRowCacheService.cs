using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for fully custom home rows.
/// Keyed by source:type:paramHash.
/// </summary>
public class CustomRowCacheService
{
    private readonly string _cacheFilePath;
    private readonly ILogger<CustomRowCacheService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private ConcurrentDictionary<string, CustomRowCacheEntry>? _cache;

    public CustomRowCacheService(ILogger<CustomRowCacheService> logger)
    {
        _logger = logger;
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        if (!Directory.Exists(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        _cacheFilePath = Path.Combine(dataPath, "custom_rows_cache.json");
    }

    public List<CustomRowItem>? TryGet(string cacheKey, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(cacheKey, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Items;
        }
        return null;
    }

    public void Set(string cacheKey, List<CustomRowItem> items)
    {
        var cache = EnsureLoaded();
        cache[cacheKey] = new CustomRowCacheEntry
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
            _logger.LogDebug("Custom rows cache flushed to disk ({Count} entries)", cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush Custom rows cache to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private ConcurrentDictionary<string, CustomRowCacheEntry> EnsureLoaded()
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
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, CustomRowCacheEntry>>(stream, JsonOptions);
                    _cache = loaded != null
                        ? new ConcurrentDictionary<string, CustomRowCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                        : new ConcurrentDictionary<string, CustomRowCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    _logger.LogInformation("Custom rows cache loaded from disk ({Count} entries)", _cache.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Custom rows cache from disk, starting fresh");
                    _cache = new ConcurrentDictionary<string, CustomRowCacheEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                _cache = new ConcurrentDictionary<string, CustomRowCacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _fileLock.Release();
        }

        return _cache;
    }
}

public class CustomRowCacheEntry
{
    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}

public class CustomRowItem
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("providerIds")]
    public CustomRowItemProviderIds ProviderIds { get; set; } = new();

    [JsonPropertyName("userRating")]
    public string? UserRating { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("backdropUrl")]
    public string? BackdropUrl { get; set; }
}

public class CustomRowItemProviderIds
{
    [JsonPropertyName("Imdb")]
    public string? Imdb { get; set; }

    [JsonPropertyName("Tmdb")]
    public string? Tmdb { get; set; }

    [JsonPropertyName("Tvdb")]
    public string? Tvdb { get; set; }
}
