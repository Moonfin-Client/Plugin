using System.Net;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Looks up each library movie's TMDB keywords once and keeps them for the seasonal row. After
/// the first run it only fetches movies that are new or whose entry has gone stale.
/// </summary>
public class SeasonalKeywordsTask : IScheduledTask
{
    public string Name => "Moonfin Seasonal Keywords Sync";
    public string Key => "Moonfin.Seasonal.KeywordsSync";
    public string Description => "Looks up each movie's TMDB keywords so the seasonal Home row can find holiday titles in the library.";
    public string Category => "Moonfin";

    private const int LibraryPageSize = 1000;
    private const int DelayBetweenRequestsMs = 50;
    private const int FlushEveryN = 200;
    private static readonly TimeSpan Refresh = TimeSpan.FromDays(180);

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SeasonalKeywordStoreService _store;
    private readonly ILogger<SeasonalKeywordsTask> _logger;

    public SeasonalKeywordsTask(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        SeasonalKeywordStoreService store,
        ILogger<SeasonalKeywordsTask> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _store = store;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var apiKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation("Seasonal keywords sync skipped: no server-wide TMDB API key configured");
            return;
        }

        progress.Report(0);
        var movies = LoadMovies();
        var live = new HashSet<string>(movies.Select(m => m.ItemId), StringComparer.OrdinalIgnoreCase);
        var pruned = _store.RemoveMissing(live);

        var cutoff = DateTimeOffset.UtcNow - Refresh;
        var pending = movies.Where(m => _store.TryGet(m.ItemId) is not { } entry || entry.FetchedAt < cutoff).ToList();
        _logger.LogInformation("Seasonal keywords sync: {Pending} of {Total} movies to look up, {Pruned} gone from the library", pending.Count, movies.Count, pruned);

        var client = _httpClientFactory.CreateClient("MoonfinHttpClient");
        var done = 0;
        foreach (var movie in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var keywords = await FetchKeywordsAsync(client, movie.TmdbId, apiKey, cancellationToken).ConfigureAwait(false);
                if (keywords != null)
                {
                    _store.Set(movie.ItemId, movie.TmdbId, keywords);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch TMDB keywords for movie {TmdbId}", movie.TmdbId);
            }

            done++;
            if (done % FlushEveryN == 0)
            {
                await _store.FlushAsync().ConfigureAwait(false);
            }

            progress.Report((double)done / Math.Max(1, pending.Count) * 100);
            await Task.Delay(DelayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
        }

        await _store.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("Seasonal keywords sync complete: {Count} movies in the store", _store.Count);
        progress.Report(100);
    }

    private List<(string ItemId, long TmdbId)> LoadMovies()
    {
        var movies = new List<(string, long)>();
        var start = 0;
        while (true)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                IsVirtualItem = false,
                Recursive = true,
                StartIndex = start,
                Limit = LibraryPageSize,
                EnableTotalRecordCount = false
            };

            var page = _libraryManager.GetItemsResult(query).Items;
            foreach (var item in page)
            {
                if (item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbId) && long.TryParse(tmdbId, out var id))
                {
                    movies.Add((item.Id.ToString("N"), id));
                }
            }

            if (page.Count < LibraryPageSize)
            {
                return movies;
            }

            start += LibraryPageSize;
        }
    }

    /// <summary>The keyword ids, an empty list when TMDB has none, or null when the movie is unknown to TMDB.</summary>
    private static async Task<List<int>?> FetchKeywordsAsync(HttpClient client, long tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/movie/{tmdbId}/keywords");
        TmdbRequestHelper.ApplyAuth(request, apiKey);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var ids = new List<int>();
        if (doc.RootElement.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array)
        {
            foreach (var keyword in keywords.EnumerateArray())
            {
                if (keyword.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                {
                    ids.Add(id.GetInt32());
                }
            }
        }

        return ids;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return TaskTriggers.Startup();
        yield return TaskTriggers.Daily(TimeSpan.FromHours(4));
    }
}
