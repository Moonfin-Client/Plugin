using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches MDBList official lists (curated charts like IMDb Top 250) using the admin's
    /// MDBList key and caches them server-side so clients can show them as home rows without
    /// their own key. Clones the structure of <see cref="MdbListBatchTask"/>.
    /// </summary>
    public class MdbListListsTask : IScheduledTask
    {
        public string Name => "Moonfin MDBList Official Lists Sync";
        public string Key => "Moonfin.MdbList.ListsSync";
        public string Description => "Fetches and caches MDBList official lists (curated charts like IMDb Top 250) so clients can show them as home rows.";
        public string Category => "Moonfin";

        private const int PageLimit = 100;
        private const int DelayBetweenApiBatchesMs = 2000;
        private const int FlushEveryNLists = 10;
        private const int DefaultMaxItemsPerList = 250;
        private static readonly TimeSpan FreshnessSkipWindow = TimeSpan.FromHours(6);

        private readonly ILogger _logger;

        private static readonly JsonSerializerOptions JsonOpts = MdbListApiHelper.JsonOptions;

        // Resolved lazily: Emby constructs IScheduledTask instances at plugin-load time, before
        // ServerEntryPoint.Run() initializes the service singletons, so this cannot be read in the ctor.
        private MdbListListsCacheService CacheService => Plugin.Instance?.MdbListListsCache
            ?? throw new InvalidOperationException("MdbListListsCacheService not initialized");

        public MdbListListsTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("MoonfinMdbListLists");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            PluginConfiguration? config = null;
            try { config = Plugin.Instance?.Configuration; }
            catch { /* configuration not ready yet (e.g. startup trigger before init) */ }

            if (config != null && !config.MdblistOfficialListsEnabled)
            {
                _logger.Info("MDBList official lists sync skipped: disabled in plugin configuration", 0);
                return;
            }

            var apiKey = config?.MdblistApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.Info("MDBList official lists sync skipped: no server-wide API key configured", 0);
                return;
            }

            // The startup trigger can fire before ServerEntryPoint.Run() initializes the
            // service singletons, so skip the run instead of throwing.
            try { _ = CacheService; }
            catch (InvalidOperationException)
            {
                _logger.Warn("MDBList official lists sync skipped: plugin services not initialized yet");
                return;
            }

            // The sync tasks fire on server startup, so skip when the cache is still fresh.
            // Repeated boots shouldn't burst the MDBList API, and the daily trigger still
            // refreshes once the cache ages out.
            var catalogAge = CacheService.GetCatalogAge();
            if (catalogAge.HasValue && catalogAge.Value < FreshnessSkipWindow)
            {
                _logger.Info("MDBList official lists sync skipped: cache is only " + catalogAge.Value.TotalHours.ToString("F1") + "h old", 0);
                return;
            }

            var maxItemsPerList = config?.MdblistOfficialListsMaxItems ?? DefaultMaxItemsPerList;
            if (maxItemsPerList <= 0) maxItemsPerList = DefaultMaxItemsPerList;

            // Poster fallback is best-effort: only runs when a server TMDB key is set, and seeds
            // from the existing cache so it only calls TMDB for ids it has not resolved before.
            var tmdbKey = config?.TmdbApiKey;
            var knownPosters = CacheService.GetKnownPosters();

            progress.Report(0);

            // Validate the key and log quota state before spending requests on a full sync.
            using (var accountClient = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(30), "Moonfin/1.0"))
            {
                var account = await MdbListApiHelper.GetAccountInfoAsync(accountClient, apiKey!, _logger, cancellationToken).ConfigureAwait(false);
                if (account == null)
                {
                    _logger.Warn("MDBList official lists sync skipped: API key could not be validated");
                    return;
                }
            }

            var rawCatalog = await FetchCatalogAsync(apiKey!, cancellationToken).ConfigureAwait(false);
            if (rawCatalog == null || rawCatalog.Count == 0)
            {
                _logger.Warn("MDBList official lists sync aborted: catalog fetch returned nothing, keeping last-good cache");
                return;
            }

            var catalog = rawCatalog
                .Where(l => !string.IsNullOrWhiteSpace(l.Slug))
                .Select(l => new MdbListCatalogEntry
                {
                    Slug = l.Slug!,
                    Name = string.IsNullOrWhiteSpace(l.Name) ? l.Slug! : l.Name!,
                    Mediatype = l.Mediatype,
                    Count = l.Items ?? 0
                })
                .ToList();

            CacheService.SetCatalog(catalog);
            var pruned = CacheService.PruneItemsNotIn(catalog.Select(c => c.Slug).ToList());
            if (pruned > 0)
                _logger.Info("MDBList official lists: pruned " + pruned + " delisted list caches", 0);

            await CacheService.FlushAsync().ConfigureAwait(false);
            _logger.Info("MDBList official lists: cached catalog of " + catalog.Count + " lists", 0);

            var listsSinceFlush = 0;
            var processed = 0;

            foreach (var entry in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var items = await FetchListItemsAsync(entry.Slug, apiKey!, maxItemsPerList, cancellationToken).ConfigureAwait(false);
                    if (items != null)
                    {
                        await EnrichPostersAsync(items, tmdbKey, knownPosters, cancellationToken).ConfigureAwait(false);
                        CacheService.SetItems(entry.Slug, items);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Warn("MDBList official lists: failed to fetch items for " + entry.Slug + ", continuing: " + ex.Message);
                }

                processed++;
                listsSinceFlush++;
                progress.Report((double)processed / catalog.Count * 100);

                if (listsSinceFlush >= FlushEveryNLists)
                {
                    await CacheService.FlushAsync().ConfigureAwait(false);
                    listsSinceFlush = 0;
                }

                if (processed < catalog.Count)
                    await Task.Delay(DelayBetweenApiBatchesMs, cancellationToken).ConfigureAwait(false);
            }

            await CacheService.FlushAsync().ConfigureAwait(false);
            _logger.Info("MDBList official lists sync complete: " + catalog.Count + " lists cached", 0);
            progress.Report(100);
        }

        private async Task<List<RawOfficialList>?> FetchCatalogAsync(string apiKey, CancellationToken cancellationToken)
        {
            var url = $"https://api.mdblist.com/lists/official?apikey={Uri.EscapeDataString(apiKey)}";

            using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(60), "Moonfin/1.0");

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                _logger.Warn("MDBList rate limit hit fetching official lists catalog, will retry next run");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn("MDBList official lists catalog returned status " + (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<RawOfficialList>>(json, JsonOpts);
        }

        private async Task<List<MdbListItem>?> FetchListItemsAsync(string slug, string apiKey, int maxItems, CancellationToken cancellationToken)
        {
            var collected = new List<MdbListItem>();
            string? cursor = null;
            var fetchedAnyPage = false;

            using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(60), "Moonfin/1.0");

            while (collected.Count < maxItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url = $"https://api.mdblist.com/lists/official/{Uri.EscapeDataString(slug)}/items" +
                          $"?apikey={Uri.EscapeDataString(apiKey)}&limit={PageLimit}&append_to_response=poster";
                if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor!);

                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    _logger.Warn("MDBList rate limit hit fetching items for " + slug);
                    // Keep partial results, but when nothing was fetched yet fail hard so the
                    // last-good cache isn't overwritten with an empty list.
                    return fetchedAnyPage ? collected : null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn("MDBList items for " + slug + " returned status " + (int)response.StatusCode);
                    // Only treat as a hard failure (keep last-good cache) if we never got a page.
                    return fetchedAnyPage ? collected : null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var page = JsonSerializer.Deserialize<RawItemsPage>(json, JsonOpts);
                fetchedAnyPage = true;

                if (page == null) break;

                // Append full pages (no per-bucket cap): movies and shows arrive in separate
                // arrays, so capping mid-page would drop shows disproportionately on combined
                // lists. The rank-sorted truncation below enforces maxItems fairly.
                AppendItems(collected, page.Movies, "movie");
                AppendItems(collected, page.Shows, "show");

                // Follow pagination.next_cursor until it's absent, that's the only
                // continuation signal the MDBList API guarantees.
                cursor = page.Pagination?.NextCursor;
                if (string.IsNullOrEmpty(cursor)) break;

                if (collected.Count < maxItems)
                    await Task.Delay(DelayBetweenApiBatchesMs, cancellationToken).ConfigureAwait(false);
            }

            if (collected.Count > maxItems)
            {
                collected = collected
                    .OrderBy(i => i.Rank ?? int.MaxValue)
                    .Take(maxItems)
                    .ToList();
            }

            return collected;
        }

        private static void AppendItems(List<MdbListItem> target, List<RawListItem>? source, string bucketType)
        {
            if (source == null) return;

            foreach (var raw in source)
            {
                var tmdb = raw.Ids?.Tmdb ?? raw.Id;
                var imdb = raw.Ids?.Imdb ?? raw.ImdbId;
                var tvdb = raw.Ids?.Tvdb ?? raw.TvdbId;

                target.Add(new MdbListItem
                {
                    Id = tmdb,
                    Name = raw.Title ?? string.Empty,
                    Type = string.IsNullOrWhiteSpace(raw.Mediatype) ? bucketType : raw.Mediatype!,
                    ProductionYear = raw.ReleaseYear,
                    Rank = raw.Rank,
                    Poster = MdbListApiHelper.NormalizeTmdbImagePath(raw.Poster),
                    ProviderIds = new MdbListItemProviderIds
                    {
                        Imdb = string.IsNullOrWhiteSpace(imdb) ? null : imdb,
                        Tmdb = tmdb?.ToString(),
                        Tvdb = tvdb?.ToString()
                    }
                });
            }
        }

        /// <summary>
        /// Fallback poster resolution for items MDBList returned without a poster
        /// (append_to_response=poster covers the vast majority). Reuses already-resolved
        /// posters and only calls TMDB for unseen ids. No-op without a server TMDB key.
        /// </summary>
        private async Task EnrichPostersAsync(List<MdbListItem> items, string? tmdbKey, Dictionary<string, string> knownPosters, CancellationToken cancellationToken)
        {
            HttpClient? client = null;
            try
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.IsNullOrEmpty(item.Poster)) continue;

                    var tmdbId = item.ProviderIds.Tmdb;
                    if (string.IsNullOrEmpty(tmdbId)) continue;

                    var key = item.Type + ":" + tmdbId;
                    if (knownPosters.TryGetValue(key, out var known))
                    {
                        item.Poster = known;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(tmdbKey)) continue;

                    if (client == null)
                    {
                        client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");
                    }

                    var poster = await FetchTmdbPosterAsync(client, item.Type, tmdbId!, tmdbKey!, cancellationToken).ConfigureAwait(false);

                    // Fallback lookups are rare once MDBList supplies posters, but still keep
                    // a light touch on the TMDB API between misses.
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(poster)) continue;

                    item.Poster = poster;
                    knownPosters[key] = poster!;
                }
            }
            finally
            {
                client?.Dispose();
            }
        }

        private async Task<string?> FetchTmdbPosterAsync(HttpClient client, string type, string tmdbId, string tmdbKey, CancellationToken cancellationToken)
        {
            try
            {
                var path = type == "show" ? "tv" : "movie";
                var url = $"https://api.themoviedb.org/3/{path}/{Uri.EscapeDataString(tmdbId)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                MoonfinHttp.ApplyTmdbAuth(request, tmdbKey);

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var details = JsonSerializer.Deserialize<TmdbDetails>(json, JsonOpts);
                return string.IsNullOrWhiteSpace(details?.PosterPath) ? null : details!.PosterPath;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
        }

        private class TmdbDetails
        {
            [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
        }

        private class RawOfficialList
        {
            [JsonPropertyName("slug")] public string? Slug { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("mediatype")] public string? Mediatype { get; set; }
            [JsonPropertyName("items")] public int? Items { get; set; }
        }

        private class RawItemsPage
        {
            [JsonPropertyName("movies")] public List<RawListItem>? Movies { get; set; }
            [JsonPropertyName("shows")] public List<RawListItem>? Shows { get; set; }
            [JsonPropertyName("pagination")] public RawPagination? Pagination { get; set; }
        }

        private class RawPagination
        {
            [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
        }

        private class RawListItem
        {
            [JsonPropertyName("id")] public long? Id { get; set; }
            [JsonPropertyName("rank")] public int? Rank { get; set; }
            [JsonPropertyName("title")] public string? Title { get; set; }
            [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
            [JsonPropertyName("tvdb_id")] public long? TvdbId { get; set; }
            [JsonPropertyName("mediatype")] public string? Mediatype { get; set; }
            [JsonPropertyName("release_year")] public int? ReleaseYear { get; set; }
            [JsonPropertyName("poster")] public string? Poster { get; set; }
            [JsonPropertyName("ids")] public RawIds? Ids { get; set; }
        }

        private class RawIds
        {
            [JsonPropertyName("imdb")] public string? Imdb { get; set; }
            [JsonPropertyName("tmdb")] public long? Tmdb { get; set; }
            [JsonPropertyName("tvdb")] public long? Tvdb { get; set; }
        }
    }
}
