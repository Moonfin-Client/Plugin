using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;

namespace Moonfin.Server.Services;

/// <summary>
/// Scheduled task that fetches MDBList official lists (the curated charts) and caches them
/// server-side using the admin's MDBList key. The controller then serves the cached catalog
/// and items to clients, so non-admins never need their own key.
/// Clones the structure of <see cref="MdbListBatchTask"/> (the ratings sync).
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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MdbListListsCacheService _cacheService;
    private readonly ILogger<MdbListListsTask> _logger;

    public MdbListListsTask(
        IHttpClientFactory httpClientFactory,
        MdbListListsCacheService cacheService,
        ILogger<MdbListListsTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = MoonfinPlugin.Instance?.Configuration;

        if (config != null && !config.MdblistOfficialListsEnabled)
        {
            _logger.LogInformation("MDBList official lists sync skipped: disabled in plugin configuration");
            return;
        }

        var apiKey = config?.MdblistApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation("MDBList official lists sync skipped: no server-wide API key configured");
            return;
        }

        // The sync tasks fire on server startup, so skip when the cache is still fresh.
        // Repeated boots shouldn't burst the MDBList API, and the daily trigger still
        // refreshes once the cache ages out.
        var catalogAge = _cacheService.GetCatalogAge();
        if (catalogAge.HasValue && catalogAge.Value < FreshnessSkipWindow)
        {
            _logger.LogInformation(
                "MDBList official lists sync skipped: cache is only {Age:F1}h old",
                catalogAge.Value.TotalHours);
            return;
        }

        var maxItemsPerList = config?.MdblistOfficialListsMaxItems ?? DefaultMaxItemsPerList;
        if (maxItemsPerList <= 0) maxItemsPerList = DefaultMaxItemsPerList;

        // Poster fallback is best-effort: only runs when a server TMDB key is set, and seeds
        // from the existing cache so it only calls TMDB for ids it has not resolved before.
        var tmdbKey = config?.TmdbApiKey;
        var knownPosters = _cacheService.GetKnownPosters();

        progress.Report(0);

        var accountClient = _httpClientFactory.CreateClient();
        accountClient.Timeout = TimeSpan.FromSeconds(30);
        accountClient.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");
        var account = await MdbListApiHelper.GetAccountInfoAsync(accountClient, apiKey, _logger, cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            _logger.LogWarning("MDBList official lists sync skipped: API key could not be validated");
            return;
        }

        var rawCatalog = await FetchCatalogAsync(apiKey, cancellationToken).ConfigureAwait(false);
        if (rawCatalog == null || rawCatalog.Count == 0)
        {
            _logger.LogWarning("MDBList official lists sync aborted: catalog fetch returned nothing, keeping last-good cache");
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

        _cacheService.SetCatalog(catalog);
        var pruned = _cacheService.PruneItemsNotIn(catalog.Select(c => c.Slug).ToList());
        if (pruned > 0)
        {
            _logger.LogInformation("MDBList official lists: pruned {Count} delisted list caches", pruned);
        }

        await _cacheService.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("MDBList official lists: cached catalog of {Count} lists", catalog.Count);

        var listsSinceFlush = 0;
        var processed = 0;

        foreach (var entry in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var items = await FetchListItemsAsync(entry.Slug, apiKey, maxItemsPerList, cancellationToken).ConfigureAwait(false);
                if (items != null)
                {
                    await EnrichPostersAsync(items, tmdbKey, knownPosters, cancellationToken).ConfigureAwait(false);
                    _cacheService.SetItems(entry.Slug, items);
                    _logger.LogDebug("MDBList official lists: cached {Count} items for {Slug}", items.Count, entry.Slug);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MDBList official lists: failed to fetch items for {Slug}, continuing...", entry.Slug);
            }

            processed++;
            listsSinceFlush++;
            progress.Report((double)processed / catalog.Count * 100);

            if (listsSinceFlush >= FlushEveryNLists)
            {
                await _cacheService.FlushAsync().ConfigureAwait(false);
                listsSinceFlush = 0;
            }

            if (processed < catalog.Count)
            {
                await Task.Delay(DelayBetweenApiBatchesMs, cancellationToken).ConfigureAwait(false);
            }
        }

        await _cacheService.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("MDBList official lists sync complete: {Count} lists cached", catalog.Count);
        progress.Report(100);
    }

    private async Task<List<RawOfficialList>?> FetchCatalogAsync(string apiKey, CancellationToken cancellationToken)
    {
        var url = $"https://api.mdblist.com/lists/official?apikey={Uri.EscapeDataString(apiKey)}";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 429)
        {
            _logger.LogWarning("MDBList rate limit hit fetching official lists catalog, will retry next run");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MDBList official lists catalog returned status {Status}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<RawOfficialList>>(json, MdbListController.JsonOptions);
    }

    private async Task<List<MdbListItem>?> FetchListItemsAsync(
        string slug,
        string apiKey,
        int maxItems,
        CancellationToken cancellationToken)
    {
        var collected = new List<MdbListItem>();
        string? cursor = null;
        var fetchedAnyPage = false;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

        while (collected.Count < maxItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"https://api.mdblist.com/lists/official/{Uri.EscapeDataString(slug)}/items" +
                      $"?apikey={Uri.EscapeDataString(apiKey)}&limit={PageLimit}&append_to_response=poster";
            if (!string.IsNullOrEmpty(cursor))
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                _logger.LogWarning("MDBList rate limit hit fetching items for {Slug}", slug);
                // Keep partial results, but when nothing was fetched yet fail hard so the
                // last-good cache isn't overwritten with an empty list.
                return fetchedAnyPage ? collected : null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MDBList items for {Slug} returned status {Status}", slug, (int)response.StatusCode);
                // Only treat as a hard failure (keep last-good cache) if we never got a page.
                return fetchedAnyPage ? collected : null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<RawItemsPage>(json, MdbListController.JsonOptions);
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
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }

            if (collected.Count < maxItems)
            {
                await Task.Delay(DelayBetweenApiBatchesMs, cancellationToken).ConfigureAwait(false);
            }
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
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(item.Poster)) continue;

            var tmdbId = item.ProviderIds?.Tmdb;
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
                client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");
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

    private async Task<string?> FetchTmdbPosterAsync(HttpClient client, string type, string tmdbId, string tmdbKey, CancellationToken cancellationToken)
    {
        try
        {
            var path = type == "show" ? "tv" : "movie";
            var url = $"https://api.themoviedb.org/3/{path}/{Uri.EscapeDataString(tmdbId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            TmdbRequestHelper.ApplyAuth(request, tmdbKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var details = JsonSerializer.Deserialize<TmdbDetails>(json, MdbListController.JsonOptions);
            return string.IsNullOrWhiteSpace(details?.PosterPath) ? null : details!.PosterPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDB poster fetch failed for {Type}/{TmdbId}", type, tmdbId);
            return null;
        }
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
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    private class TmdbDetails
    {
        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }
    }

    private class RawOfficialList
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("mediatype")]
        public string? Mediatype { get; set; }

        [JsonPropertyName("items")]
        public int? Items { get; set; }
    }

    private class RawItemsPage
    {
        [JsonPropertyName("movies")]
        public List<RawListItem>? Movies { get; set; }

        [JsonPropertyName("shows")]
        public List<RawListItem>? Shows { get; set; }

        [JsonPropertyName("pagination")]
        public RawPagination? Pagination { get; set; }
    }

    private class RawPagination
    {
        [JsonPropertyName("next_cursor")]
        public string? NextCursor { get; set; }
    }

    private class RawListItem
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("rank")]
        public int? Rank { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public long? TvdbId { get; set; }

        [JsonPropertyName("mediatype")]
        public string? Mediatype { get; set; }

        [JsonPropertyName("release_year")]
        public int? ReleaseYear { get; set; }

        [JsonPropertyName("poster")]
        public string? Poster { get; set; }

        [JsonPropertyName("ids")]
        public RawIds? Ids { get; set; }
    }

    private class RawIds
    {
        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }

        [JsonPropertyName("tmdb")]
        public long? Tmdb { get; set; }

        [JsonPropertyName("tvdb")]
        public long? Tvdb { get; set; }
    }
}
