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
/// Pages through the library in chunks to avoid loading everything into memory.
/// </summary>
public class MdbListBatchTask : IScheduledTask
{
    public string Name => "Moonfin MDBList Ratings Sync";
    public string Key => "Moonfin.MdbList.BatchSync";
    public string Description => "Batch-fetches MDBList ratings for all movies and shows in the library. Only fetches items not already cached.";
    public string Category => "Moonfin";

    // The batch media-info endpoint accepts 100 ids even on non-supporter keys.
    private const int ApiBatchSize = 100;
    private const int LibraryPageSize = 2000;
    private const int DelayBetweenApiBatchesMs = 2000;
    private const int FlushEveryNBatches = 20;
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    // Entries older than this are never served (the read TTL is 7 days), they're just
    // dead weight from removed library items and one-off lookups.
    private static readonly TimeSpan PruneAge = TimeSpan.FromDays(14);

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

        // Validate the key and log quota state before spending requests on a full sync.
        var accountClient = _httpClientFactory.CreateClient();
        accountClient.Timeout = TimeSpan.FromSeconds(30);
        accountClient.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");
        var account = await MdbListApiHelper.GetAccountInfoAsync(accountClient, apiKey, _logger, cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            _logger.LogWarning("MDBList batch sync skipped: API key could not be validated");
            return;
        }

        var totalLibraryCount = GetLibraryCount();
        _logger.LogInformation("MDBList batch sync starting ({Total} library items)...", totalLibraryCount);
        progress.Report(0);

        var freshKeys = _cacheService.GetFreshKeys(CacheMaxAge);

        var uncachedItems = new List<LibraryItemInfo>();
        var totalScanned = 0;
        var startIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = GetLibraryItemsPage(startIndex, LibraryPageSize);
            if (page.Count == 0) break;

            foreach (var item in page)
            {
                if (!freshKeys.Contains(item.CacheKey))
                {
                    uncachedItems.Add(item);
                }
            }

            totalScanned += page.Count;
            startIndex += page.Count;

            if (page.Count < LibraryPageSize) break;
        }

        _logger.LogInformation("Scanned {Scanned} items: {Cached} cached, {Uncached} need fetching",
            totalScanned, totalScanned - uncachedItems.Count, uncachedItems.Count);

        if (uncachedItems.Count == 0)
        {
            _logger.LogInformation("MDBList batch sync complete: all items already cached");
            progress.Report(100);
            return;
        }

        // Multiple library versions of the same title share a TMDB id, so dedupe to
        // avoid spending two batch slots on one id.
        var movieItems = uncachedItems.Where(i => i.Type == "movie").DistinctBy(i => i.CacheKey).ToList();
        var showItems = uncachedItems.Where(i => i.Type == "show").DistinctBy(i => i.CacheKey).ToList();

        uncachedItems = null!;

        var totalItems = movieItems.Count + showItems.Count;
        var processedItems = 0;

        try
        {
            processedItems = await FetchBatchesAsync(movieItems, "movie", apiKey, processedItems, totalItems, progress, cancellationToken);
            processedItems = await FetchBatchesAsync(showItems, "show", apiKey, processedItems, totalItems, progress, cancellationToken);
        }
        catch (MdbListRateLimitException)
        {
            // Keep what was fetched. The daily trigger picks up the remainder once
            // the quota resets.
            await _cacheService.FlushAsync();
            _logger.LogWarning("MDBList batch sync aborted after rate limit: processed {Count}/{Total} items, will resume next run", processedItems, totalItems);
            progress.Report(100);
            return;
        }

        var pruned = _cacheService.PruneOlderThan(PruneAge);
        if (pruned > 0)
        {
            _logger.LogInformation("MDBList ratings cache: pruned {Count} entries older than {Days} days", pruned, PruneAge.TotalDays);
        }

        await _cacheService.FlushAsync();

        _logger.LogInformation("MDBList batch sync complete: processed {Count} items", processedItems);
        progress.Report(100);
    }

    /// <summary>Gets the total number of movies + series in the library (cheap count-only query).</summary>
    private int GetLibraryCount()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true,
            Limit = 0
        };

        return _libraryManager.GetCount(query);
    }

    /// <summary>Loads a single page of library items, extracting only the TMDB ID and type.</summary>
    private List<LibraryItemInfo> GetLibraryItemsPage(int startIndex, int limit)
    {
        var items = new List<LibraryItemInfo>();

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true,
            StartIndex = startIndex,
            Limit = limit
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

        var batches = items.Chunk(ApiBatchSize).ToList();
        _logger.LogInformation("Fetching {Count} {Type}s in {Batches} batch(es)", items.Count, type, batches.Count);

        var batchesSinceFlush = 0;

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
            catch (Exception ex) when (ex is not OperationCanceledException and not MdbListRateLimitException)
            {
                _logger.LogWarning(ex, "Batch fetch failed for {Type} batch, continuing...", type);
            }

            processedSoFar += batch.Length;
            batchesSinceFlush++;
            progress.Report((double)processedSoFar / totalItems * 100);

            if (batchesSinceFlush >= FlushEveryNBatches)
            {
                await _cacheService.FlushAsync();
                batchesSinceFlush = 0;
            }

            if (processedSoFar < totalItems)
            {
                await Task.Delay(DelayBetweenApiBatchesMs, cancellationToken);
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
        // Use the canonical trailing-slash path so a redirect can't turn the POST
        // into a GET. The endpoint expects integer ids.
        var url = $"{MdbListApiHelper.BaseUrl}/tmdb/{Uri.EscapeDataString(type)}/?apikey={Uri.EscapeDataString(apiKey)}";

        var numericIds = new List<long>(tmdbIds.Count);
        foreach (var id in tmdbIds)
        {
            if (long.TryParse(id, out var numeric))
            {
                numericIds.Add(numeric);
            }
        }

        if (numericIds.Count == 0) return null;

        var requestBody = new MdbListBatchRequest { Ids = numericIds };
        var jsonBody = JsonSerializer.Serialize(requestBody, MdbListController.JsonOptions);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 429)
        {
            throw new MdbListRateLimitException();
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
            // Prefer the nested ids object and fall back to the top-level id, which
            // batch items sometimes carry instead.
            var tmdbId = (item.Ids?.Tmdb ?? item.Id)?.ToString();
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
        public List<long> Ids { get; set; } = new();
    }

    private class MdbListBatchItem
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

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
