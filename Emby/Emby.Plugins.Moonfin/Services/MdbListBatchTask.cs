using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    public class MdbListBatchTask : IScheduledTask
    {
        public string Name => "Moonfin MDBList Ratings Sync";
        public string Key => "Moonfin.MdbList.BatchSync";
        public string Description => "Batch-fetches MDBList ratings for all movies and shows in the library.";
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
        private readonly ILogger _logger;

        // Resolved lazily: Emby constructs IScheduledTask instances at plugin-load time, before
        // ServerEntryPoint.Run() initializes the service singletons, so this cannot be read in the ctor.
        private MdbListCacheService CacheService => Plugin.Instance?.MdbListCache
            ?? throw new InvalidOperationException("MdbListCacheService not initialized");

        public MdbListBatchTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("MoonfinMdbListBatch");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            string? apiKey = null;
            try { apiKey = Plugin.Instance?.Configuration?.MdblistApiKey; }
            catch { /* configuration not ready yet (e.g. startup trigger before init) */ }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.Info("MDBList batch sync skipped: no server-wide API key configured", 0);
                return;
            }

            // The startup trigger can fire before ServerEntryPoint.Run() initializes the
            // service singletons, so skip the run instead of throwing.
            try { _ = CacheService; }
            catch (InvalidOperationException)
            {
                _logger.Warn("MDBList batch sync skipped: plugin services not initialized yet");
                return;
            }

            // Validate the key and log quota state before spending requests on a full sync.
            using (var accountClient = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(30), "Moonfin/1.0"))
            {
                var account = await MdbListApiHelper.GetAccountInfoAsync(accountClient, apiKey!, _logger, cancellationToken).ConfigureAwait(false);
                if (account == null)
                {
                    _logger.Warn("MDBList batch sync skipped: API key could not be validated");
                    return;
                }
            }

            _logger.Info("MDBList batch sync starting...", 0);
            progress.Report(0);

            var freshKeys = CacheService.GetFreshKeys(CacheMaxAge);
            var uncachedItems = new List<LibraryItemInfo>();
            var startIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = GetLibraryItemsPage(startIndex, LibraryPageSize);
                if (page.Count == 0) break;

                foreach (var item in page)
                    if (!freshKeys.Contains(item.CacheKey))
                        uncachedItems.Add(item);

                startIndex += page.Count;
                if (page.Count < LibraryPageSize) break;
            }

            if (uncachedItems.Count == 0)
            {
                _logger.Info("MDBList batch sync complete: all items already cached", 0);
                progress.Report(100);
                return;
            }

            // Multiple library versions of the same title share a TMDB id, so dedupe to
            // avoid spending two batch slots on one id.
            var movieItems = uncachedItems.Where(i => i.Type == "movie")
                .GroupBy(i => i.CacheKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var showItems = uncachedItems.Where(i => i.Type == "show")
                .GroupBy(i => i.CacheKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

            var totalItems = movieItems.Count + showItems.Count;
            var processedItems = 0;

            try
            {
                processedItems = await FetchBatchesAsync(movieItems, "movie", apiKey!, processedItems, totalItems, progress, cancellationToken).ConfigureAwait(false);
                processedItems = await FetchBatchesAsync(showItems, "show", apiKey!, processedItems, totalItems, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (MdbListRateLimitException)
            {
                // Keep what was fetched. The daily trigger picks up the remainder once
                // the quota resets.
                await CacheService.FlushAsync().ConfigureAwait(false);
                _logger.Warn("MDBList batch sync aborted after rate limit: processed " + processedItems + "/" + totalItems + " items, will resume next run");
                progress.Report(100);
                return;
            }

            var pruned = CacheService.PruneOlderThan(PruneAge);
            if (pruned > 0)
                _logger.Info("MDBList ratings cache: pruned " + pruned + " entries older than " + PruneAge.TotalDays + " days", 0);

            await CacheService.FlushAsync().ConfigureAwait(false);
            _logger.Info("MDBList batch sync complete: processed " + processedItems + " items", 0);
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

                var type = item.GetType().Name == "Movie" ? "movie" : "show";
                items.Add(new LibraryItemInfo
                {
                    TmdbId = tmdbId,
                    Type = type,
                    CacheKey = $"{type}:{tmdbId}"
                });
            }
            return items;
        }

        private async Task<int> FetchBatchesAsync(List<LibraryItemInfo> items, string type, string apiKey,
            int processedSoFar, int totalItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (items.Count == 0) return processedSoFar;

            var batches = ChunkList(items, ApiBatchSize);
            var batchesSinceFlush = 0;

            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var tmdbIds = batch.Select(i => i.TmdbId).ToList();
                    var ratings = await FetchBatchFromApiAsync(type, tmdbIds, apiKey, cancellationToken).ConfigureAwait(false);
                    if (ratings != null) CacheService.SetMany(ratings);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is MdbListRateLimitException))
                {
                    _logger.Warn("Batch fetch failed for " + type + " batch, continuing: " + ex.Message);
                }

                processedSoFar += batch.Count;
                batchesSinceFlush++;
                if (totalItems > 0) progress.Report((double)processedSoFar / totalItems * 100);

                if (batchesSinceFlush >= FlushEveryNBatches)
                {
                    await CacheService.FlushAsync().ConfigureAwait(false);
                    batchesSinceFlush = 0;
                }

                if (processedSoFar < totalItems)
                    await Task.Delay(DelayBetweenApiBatchesMs, cancellationToken).ConfigureAwait(false);
            }

            return processedSoFar;
        }

        private async Task<Dictionary<string, List<MdbListRating>>?> FetchBatchFromApiAsync(
            string type, List<string> tmdbIds, string apiKey, CancellationToken cancellationToken)
        {
            // Use the canonical trailing-slash path so a redirect can't turn the POST
            // into a GET. The endpoint expects integer ids.
            var url = $"{MdbListApiHelper.BaseUrl}/tmdb/{Uri.EscapeDataString(type)}/?apikey={Uri.EscapeDataString(apiKey)}";

            var numericIds = new List<long>(tmdbIds.Count);
            foreach (var id in tmdbIds)
                if (long.TryParse(id, out var numeric)) numericIds.Add(numeric);

            if (numericIds.Count == 0) return null;

            var requestBody = JsonSerializer.Serialize(new MdbListBatchRequest { Ids = numericIds }, MdbListApiHelper.JsonOptions);

            using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(60), "Moonfin/1.0");

            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                throw new MdbListRateLimitException();
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn("MDBList batch returned status " + (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var batchResponse = JsonSerializer.Deserialize<List<MdbListBatchItem>>(json, MdbListApiHelper.JsonOptions);
            if (batchResponse == null) return null;

            var result = new Dictionary<string, List<MdbListRating>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in batchResponse)
            {
                // Prefer the nested ids object and fall back to the top-level id, which
                // batch items sometimes carry instead.
                var tmdbId = (item.Ids?.Tmdb ?? item.Id)?.ToString();
                if (string.IsNullOrEmpty(tmdbId)) continue;
                result[$"{type}:{tmdbId}"] = item.Ratings ?? new List<MdbListRating>();
            }
            return result;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks };
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
            public string CacheKey { get; set; } = string.Empty;
        }

        private class MdbListBatchRequest
        {
            [JsonPropertyName("ids")] public List<long> Ids { get; set; } = new List<long>();
        }

        private class MdbListBatchItem
        {
            [JsonPropertyName("id")] public long? Id { get; set; }
            [JsonPropertyName("ids")] public MdbListBatchIds? Ids { get; set; }
            [JsonPropertyName("ratings")] public List<MdbListRating>? Ratings { get; set; }
        }

        private class MdbListBatchIds
        {
            [JsonPropertyName("tmdb")] public long? Tmdb { get; set; }
        }
    }
}
