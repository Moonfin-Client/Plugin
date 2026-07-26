using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Pre-warms the studio-logo cache by fetching each library item's TMDB production companies
    /// and downloading their logos. Only items not already cached (within the max age) are fetched.
    /// Clones the structure of <see cref="MdbListBatchTask"/>.
    /// </summary>
    public class StudioLogoSyncTask : IScheduledTask
    {
        public string Name => "Moonfin Studio Images Sync";
        public string Key => "Moonfin.Tmdb.StudioImages";
        public string Description => "Fetches and caches TMDB studio logos for all movies and shows in the library so clients can show them on the details screen.";
        public string Category => "Moonfin";

        private const int LibraryPageSize = 2000;
        private const int ItemsPerBatch = 20;
        private const int DelayBetweenBatchesMs = 1000;
        private const int FlushEveryNBatches = 10;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        private StudioLogoCacheService CacheService => Plugin.Instance?.StudioLogoCache
            ?? throw new InvalidOperationException("StudioLogoCacheService not initialized");

        private StudioLogoFetchService FetchService => Plugin.Instance?.StudioLogoFetch
            ?? throw new InvalidOperationException("StudioLogoFetchService not initialized");

        public StudioLogoSyncTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("MoonfinStudioLogos");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            PluginConfiguration? config = null;
            try { config = Plugin.Instance?.Configuration; }
            catch { /* configuration not ready yet (e.g. startup trigger before init) */ }

            if (config != null && !config.StudioLogosEnabled)
            {
                _logger.Info("Studio images sync skipped: disabled in configuration", 0);
                return;
            }

            var apiKey = config?.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.Info("Studio images sync skipped: no server-wide TMDB API key configured", 0);
                return;
            }

            // The startup trigger can fire before ServerEntryPoint.Run() initializes the
            // service singletons, so skip the run instead of throwing.
            try { _ = CacheService; _ = FetchService; }
            catch (InvalidOperationException)
            {
                _logger.Warn("Studio images sync skipped: plugin services not initialized yet");
                return;
            }

            progress.Report(0);

            var cacheMaxAge = TimeSpan.FromDays(config?.StudioLogosMaxAgeDays ?? 30);
            var freshKeys = CacheService.GetFreshItemKeys(cacheMaxAge);
            var uncached = new List<LibraryItemInfo>();
            var startIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = GetLibraryItemsPage(startIndex, LibraryPageSize);
                if (page.Count == 0) break;

                foreach (var item in page)
                    if (!freshKeys.Contains(item.Type + ":" + item.TmdbId))
                        uncached.Add(item);

                startIndex += page.Count;
                if (page.Count < LibraryPageSize) break;
            }

            if (uncached.Count == 0)
            {
                _logger.Info("Studio images sync complete: all items already cached", 0);
                progress.Report(100);
                return;
            }

            _logger.Info("Studio images sync starting: " + uncached.Count + " items to fetch", 0);

            var processed = 0;
            var batchesSinceFlush = 0;

            foreach (var batch in ChunkList(uncached, ItemsPerBatch))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var item in batch)
                {
                    try
                    {
                        await FetchService.FetchAndCacheItemAsync(item.Type, item.TmdbId, apiKey!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        _logger.Warn("Studio fetch failed for " + item.Type + "/" + item.TmdbId + ", continuing: " + ex.Message);
                    }
                }

                processed += batch.Count;
                batchesSinceFlush++;
                progress.Report((double)processed / uncached.Count * 100);

                if (batchesSinceFlush >= FlushEveryNBatches)
                {
                    await CacheService.FlushAsync().ConfigureAwait(false);
                    batchesSinceFlush = 0;
                }

                if (processed < uncached.Count)
                    await Task.Delay(DelayBetweenBatchesMs, cancellationToken).ConfigureAwait(false);
            }

            await CacheService.FlushAsync().ConfigureAwait(false);
            _logger.Info("Studio images sync complete: processed " + processed + " items", 0);
            progress.Report(100);
        }

        private List<LibraryItemInfo> GetLibraryItemsPage(int startIndex, int limit)
        {
            var items = new List<LibraryItemInfo>();

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Series" },
                IsVirtualItem = false,
                Recursive = true,
                StartIndex = startIndex,
                Limit = limit
            };

            var results = _libraryManager.GetItemsResult(query);
            foreach (var item in results.Items)
            {
                string? tmdbId = null;
                item.ProviderIds?.TryGetValue("Tmdb", out tmdbId);
                if (string.IsNullOrEmpty(tmdbId)) continue;

                items.Add(new LibraryItemInfo
                {
                    TmdbId = tmdbId!,
                    Type = item.GetType().Name == "Movie" ? "movie" : "tv"
                });
            }

            return items;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks };
        }

        private static List<List<T>> ChunkList<T>(List<T> source, int chunkSize)
        {
            var chunks = new List<List<T>>();
            for (int i = 0; i < source.Count; i += chunkSize)
                chunks.Add(source.GetRange(i, Math.Min(chunkSize, source.Count - i)));
            return chunks;
        }

        private class LibraryItemInfo
        {
            public string TmdbId { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }
    }
}
