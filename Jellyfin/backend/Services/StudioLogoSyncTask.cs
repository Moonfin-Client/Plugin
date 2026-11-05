using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Scheduled task that pre-warms the studio-logo cache by fetching each library
/// item's TMDB production companies and downloading their logos. Only items not
/// already cached (within the max age) are fetched.
/// </summary>
public class StudioLogoSyncTask : IScheduledTask
{
    public string Name => "Moonfin Studio Images Sync";
    public string Key => "Moonfin.Tmdb.StudioImages";
    public string Description => "Fetches and caches TMDB studio logos for all movies and shows in the library so clients can show them on the details screen and the Studios home row.";
    public string Category => "Moonfin";

    private const int LibraryPageSize = 2000;
    private const int ItemsPerBatch = 20;
    private const int DelayBetweenBatchesMs = 1000;
    private const int FlushEveryNBatches = 10;

    private readonly ILibraryManager _libraryManager;
    private readonly StudioLogoCacheService _cacheService;
    private readonly StudioLogoFetchService _fetchService;
    private readonly ILogger<StudioLogoSyncTask> _logger;

    public StudioLogoSyncTask(
        ILibraryManager libraryManager,
        StudioLogoCacheService cacheService,
        StudioLogoFetchService fetchService,
        ILogger<StudioLogoSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _cacheService = cacheService;
        _fetchService = fetchService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (MoonfinPlugin.Instance?.Configuration?.StudioLogosEnabled != true)
        {
            _logger.LogInformation("Studio images sync skipped: disabled in configuration");
            return;
        }

        var apiKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation("Studio images sync skipped: no server-wide TMDB API key configured");
            return;
        }

        progress.Report(0);

        var cacheMaxAge = TimeSpan.FromDays(MoonfinPlugin.Instance?.Configuration?.StudioLogosMaxAgeDays ?? 30);
        var freshKeys = _cacheService.GetFreshItemKeys(cacheMaxAge);
        var uncached = new List<LibraryItemInfo>();
        var startIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = GetLibraryItemsPage(startIndex, LibraryPageSize);
            if (page.Count == 0) break;

            foreach (var item in page)
            {
                if (!freshKeys.Contains($"{item.Type}:{item.TmdbId}"))
                {
                    uncached.Add(item);
                }
            }

            startIndex += page.Count;
            if (page.Count < LibraryPageSize) break;
        }

        if (uncached.Count == 0)
        {
            _logger.LogInformation("Studio images sync complete: all items already cached");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Studio images sync starting: {Count} items to fetch", uncached.Count);

        var processed = 0;
        var batchesSinceFlush = 0;

        foreach (var batch in uncached.Chunk(ItemsPerBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in batch)
            {
                try
                {
                    await _fetchService.FetchAndCacheItemAsync(item.Type, item.TmdbId, apiKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Studio fetch failed for {Type}/{Id}, continuing", item.Type, item.TmdbId);
                }
            }

            processed += batch.Length;
            batchesSinceFlush++;
            progress.Report((double)processed / uncached.Count * 100);

            if (batchesSinceFlush >= FlushEveryNBatches)
            {
                await _cacheService.FlushAsync().ConfigureAwait(false);
                batchesSinceFlush = 0;
            }

            if (processed < uncached.Count)
            {
                await Task.Delay(DelayBetweenBatchesMs, cancellationToken).ConfigureAwait(false);
            }
        }

        await _cacheService.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("Studio images sync complete: processed {Count} items", processed);
        progress.Report(100);
    }

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

            items.Add(new LibraryItemInfo
            {
                TmdbId = tmdbId,
                Type = item is Movie ? "movie" : "tv"
            });
        }

        return items;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return TaskTriggers.Startup();

        yield return TaskTriggers.Daily(TimeSpan.FromHours(5));
    }

    private class LibraryItemInfo
    {
        public string TmdbId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
