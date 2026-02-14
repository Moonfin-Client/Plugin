using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;

namespace Moonfin.Server.Services;

/// <summary>
/// Scheduled task that batch-fetches MDBList ratings for all library items.
/// Runs on startup and daily. Only fetches items not already cached.
/// </summary>
public class MdbListBatchTask : IScheduledTask
{
    public string Name => "Moonfin MDBList Ratings Sync";
    public string Key => "Moonfin.MdbList.BatchSync";
    public string Description => "Batch-fetches MDBList ratings for all movies and shows in the library. Only fetches items not already cached.";
    public string Category => "Moonfin";

    private const int BatchSize = 100;
    private const int DelayBetweenBatchesMs = 2000;
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MdbListCacheService _cacheService;
    private readonly ILogger<MdbListBatchTask> _logger;

    public MdbListBatchTask(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        MdbListCacheService cacheService,
        ILogger<MdbListBatchTask> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var apiKey = MoonfinPlugin.Instance?.Configuration?.MdblistApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation("MDBList batch sync skipped: no server-wide API key configured");
            return;
        }

        _logger.LogInformation("MDBList batch sync starting...");
        progress.Report(0);

        var itemsToFetch = GetLibraryItems();
        _logger.LogInformation("Found {Total} library items with TMDb IDs", itemsToFetch.Count);

        if (itemsToFetch.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var freshKeys = _cacheService.GetFreshKeys(CacheMaxAge);
        var uncachedItems = itemsToFetch
            .Where(item => !freshKeys.Contains(item.CacheKey))
            .ToList();

        _logger.LogInformation("{Cached} items already cached, {Uncached} items need fetching",
            itemsToFetch.Count - uncachedItems.Count, uncachedItems.Count);

        if (uncachedItems.Count == 0)
        {
            _logger.LogInformation("MDBList batch sync complete: all items already cached");
            progress.Report(100);
            return;
        }

        // Group by type (the batch endpoint requires separate calls per type)
        var movieItems = uncachedItems.Where(i => i.Type == "movie").ToList();
        var showItems = uncachedItems.Where(i => i.Type == "show").ToList();

        var totalItems = uncachedItems.Count;
        var processedItems = 0;

        processedItems = await FetchBatchesAsync(movieItems, "movie", apiKey, processedItems, totalItems, progress, cancellationToken);
        processedItems = await FetchBatchesAsync(showItems, "show", apiKey, processedItems, totalItems, progress, cancellationToken);

        await _cacheService.FlushAsync();

        _logger.LogInformation("MDBList batch sync complete: processed {Count} items", processedItems);
        progress.Report(100);
    }

    private List<LibraryItemInfo> GetLibraryItems()
    {
        var items = new List<LibraryItemInfo>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true
        };

        var results = _libraryManager.GetItemsResult(query);

        foreach (var item in results.Items)
        {
            if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbId) || string.IsNullOrEmpty(tmdbId))
                continue;

            var type = item is Movie ? "movie" : "show";
            items.Add(new LibraryItemInfo
            {
                TmdbId = tmdbId,
                Type = type,
                CacheKey = $"{type}:{tmdbId}"
            });
        }

        return items;
    }

    private async Task<int> FetchBatchesAsync(
        List<LibraryItemInfo> items,
        string type,
        string apiKey,
        int processedSoFar,
        int totalItems,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) return processedSoFar;

        var batches = items.Chunk(BatchSize).ToList();
        _logger.LogInformation("Fetching {Count} {Type}s in {Batches} batch(es)", items.Count, type, batches.Count);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var tmdbIds = batch.Select(i => i.TmdbId).ToList();
                var ratings = await FetchBatchFromApiAsync(type, tmdbIds, apiKey, cancellationToken);

                if (ratings != null)
                {
                    _cacheService.SetMany(ratings);
                    _logger.LogDebug("Cached {Count} {Type} ratings from batch", ratings.Count, type);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Batch fetch failed for {Type} batch, continuing...", type);
            }

            processedSoFar += batch.Length;
            progress.Report((double)processedSoFar / totalItems * 100);

            if (processedSoFar < totalItems)
            {
                await Task.Delay(DelayBetweenBatchesMs, cancellationToken);
            }
        }

        return processedSoFar;
    }

    private async Task<Dictionary<string, List<MdbListRating>>?> FetchBatchFromApiAsync(
        string type,
        List<string> tmdbIds,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.mdblist.com/tmdb/{Uri.EscapeDataString(type)}?apikey={Uri.EscapeDataString(apiKey)}";

        var requestBody = new MdbListBatchRequest { Ids = tmdbIds };
        var jsonBody = JsonSerializer.Serialize(requestBody, MdbListController.JsonOptions);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 429)
        {
            _logger.LogWarning("MDBList rate limit hit during batch fetch, will retry next run");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MDBList batch returned status {Status}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var batchResponse = JsonSerializer.Deserialize<List<MdbListBatchItem>>(json, MdbListController.JsonOptions);

        if (batchResponse == null) return null;

        var result = new Dictionary<string, List<MdbListRating>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in batchResponse)
        {
            var tmdbId = item.Ids?.Tmdb?.ToString();
            if (string.IsNullOrEmpty(tmdbId)) continue;

            var cacheKey = $"{type}:{tmdbId}";
            result[cacheKey] = item.Ratings ?? new List<MdbListRating>();
        }

        return result;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerStartup
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    private class LibraryItemInfo
    {
        public string TmdbId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string CacheKey { get; set; } = string.Empty;
    }

    private class MdbListBatchRequest
    {
        [JsonPropertyName("ids")]
        public List<string> Ids { get; set; } = new();
    }

    private class MdbListBatchItem
    {
        [JsonPropertyName("ids")]
        public MdbListBatchIds? Ids { get; set; }

        [JsonPropertyName("ratings")]
        public List<MdbListRating>? Ratings { get; set; }
    }

    private class MdbListBatchIds
    {
        [JsonPropertyName("tmdb")]
        public long? Tmdb { get; set; }
    }
}
