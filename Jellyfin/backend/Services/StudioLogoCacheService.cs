using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// File-backed cache for TMDB studio (production company) logos.
/// Logo images are stored as PNG files under studio_logos/, and a JSON index
/// tracks each company (name, TMDB logo path, whether a logo was found) plus a
/// per-item map so a request only needs the item's TMDB id and type.
/// The scheduled task and the on-demand endpoint both populate this cache.
/// </summary>
public class StudioLogoCacheService
{
    private readonly string _cacheDir;
    private readonly string _indexFilePath;
    private readonly ILogger<StudioLogoCacheService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private StudioIndex? _index;

    public StudioLogoCacheService(ILogger<StudioLogoCacheService> logger)
    {
        _logger = logger;
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        _cacheDir = Path.Combine(dataPath, "studio_logos");
        _indexFilePath = Path.Combine(_cacheDir, "index.json");

        if (!Directory.Exists(_cacheDir))
        {
            Directory.CreateDirectory(_cacheDir);
        }
    }

    /// <summary>Returns the item's cached companies when the item entry is still fresh, otherwise null.</summary>
    public List<StudioCompanyInfo>? TryGetItem(string type, string tmdbId, TimeSpan maxAge)
    {
        var index = EnsureLoaded();
        if (!index.Items.TryGetValue(ItemKey(type, tmdbId), out var item)) return null;
        if (DateTimeOffset.UtcNow - item.CachedAt >= maxAge) return null;

        var companies = new List<StudioCompanyInfo>();
        foreach (var id in item.CompanyIds)
        {
            if (index.Companies.TryGetValue(id.ToString(), out var c))
            {
                companies.Add(new StudioCompanyInfo { Id = c.Id, Name = c.Name, HasLogo = c.HasLogo });
            }
        }
        return companies;
    }

    /// <summary>Records the companies and which ones belong to this item. Does not persist images.</summary>
    public void SetItem(string type, string tmdbId, List<StudioCompanyEntry> companies)
    {
        var index = EnsureLoaded();
        foreach (var company in companies)
        {
            index.Companies[company.Id.ToString()] = company;
        }
        index.Items[ItemKey(type, tmdbId)] = new StudioItemEntry
        {
            CompanyIds = companies.Select(c => c.Id).ToList(),
            CachedAt = DateTimeOffset.UtcNow
        };
    }

    public HashSet<string> GetFreshItemKeys(TimeSpan maxAge)
    {
        var index = EnsureLoaded();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in index.Items)
        {
            if (entry.CachedAt >= cutoff) keys.Add(key);
        }
        return keys;
    }

    public bool HasImage(int companyId) => File.Exists(GetImagePath(companyId));

    /// <summary>Networks are cached under a negated id, written as n123.png so no
    /// file name starts with a dash.</summary>
    public static string ImageFileName(int companyId) =>
        companyId < 0 ? $"n{-companyId}.png" : $"{companyId}.png";

    public string GetImagePath(int companyId) => Path.Combine(_cacheDir, ImageFileName(companyId));

    /// <summary>The TMDB logo path recorded for a company, so a missing image file can be re-downloaded.</summary>
    public string? GetLogoPath(int companyId)
    {
        var index = EnsureLoaded();
        return index.Companies.TryGetValue(companyId.ToString(), out var c) ? c.LogoPath : null;
    }

    public async Task SaveImageAsync(int companyId, byte[] bytes)
    {
        await File.WriteAllBytesAsync(GetImagePath(companyId), bytes).ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        var index = _index;
        if (index == null) return;

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(_indexFilePath);
            await JsonSerializer.SerializeAsync(stream, index, JsonOptions).ConfigureAwait(false);
            _logger.LogDebug("Studio logo index flushed ({Companies} companies, {Items} items)", index.Companies.Count, index.Items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush studio logo index to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string ItemKey(string type, string tmdbId) => $"{type}:{tmdbId}";

    private StudioIndex EnsureLoaded()
    {
        if (_index != null) return _index;

        _fileLock.Wait();
        try
        {
            if (_index != null) return _index;

            if (File.Exists(_indexFilePath))
            {
                try
                {
                    using var stream = File.OpenRead(_indexFilePath);
                    var loaded = JsonSerializer.Deserialize<StudioIndex>(stream, JsonOptions) ?? new StudioIndex();
                    if (loaded.Version != StudioIndex.CurrentVersion)
                    {
                        _logger.LogInformation("Studio logo index is version {Found}, rebuilding at version {Current}", loaded.Version, StudioIndex.CurrentVersion);
                        loaded = new StudioIndex();
                    }
                    _index = loaded;
                    _logger.LogInformation("Studio logo index loaded ({Companies} companies, {Items} items)", _index.Companies.Count, _index.Items.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load studio logo index, starting fresh");
                    _index = new StudioIndex();
                }
            }
            else
            {
                _index = new StudioIndex();
            }
        }
        finally
        {
            _fileLock.Release();
        }

        return _index;
    }
}

/// <summary>Company data returned to callers of the cache.</summary>
public class StudioCompanyInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hasLogo")]
    public bool HasLogo { get; set; }
}

internal class StudioIndex
{
    /// <summary>Bumped when cached entries need rebuilding rather than reusing.
    /// Version 2 added TV networks, which entries written before it lack, and
    /// gave networks their own negated ids.</summary>
    public const int CurrentVersion = 2;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("companies")]
    public Dictionary<string, StudioCompanyEntry> Companies { get; set; } = new();

    [JsonPropertyName("items")]
    public Dictionary<string, StudioItemEntry> Items { get; set; } = new();
}

public class StudioCompanyEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("logoPath")]
    public string? LogoPath { get; set; }

    [JsonPropertyName("hasLogo")]
    public bool HasLogo { get; set; }
}

internal class StudioItemEntry
{
    [JsonPropertyName("companyIds")]
    public List<int> CompanyIds { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
