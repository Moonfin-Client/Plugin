using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
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
        private readonly ILogger _logger;

        // Resolved lazily: Emby constructs IScheduledTask instances before ServerEntryPoint.Run()
        // initializes the service singletons, so this can't be read in the ctor.
        private SeasonalKeywordStoreService Store => Plugin.Instance?.SeasonalKeywords
            ?? throw new InvalidOperationException("SeasonalKeywordStoreService not initialized");

        public SeasonalKeywordsTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("MoonfinSeasonal");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            string? apiKey = null;
            try { apiKey = Plugin.Instance?.Configuration?.TmdbApiKey; }
            catch { /* configuration not ready yet (e.g. startup trigger before init) */ }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.Info("Seasonal keywords sync skipped: no server-wide TMDB API key configured", 0);
                return;
            }

            try { _ = Store; }
            catch (InvalidOperationException)
            {
                _logger.Warn("Seasonal keywords sync skipped: plugin services not initialized yet");
                return;
            }

            progress.Report(0);
            var movies = LoadMovies();
            var live = new HashSet<string>(movies.Select(m => m.ItemId), StringComparer.OrdinalIgnoreCase);
            var pruned = Store.RemoveMissing(live);

            var cutoff = DateTimeOffset.UtcNow - Refresh;
            var pending = movies.Where(m =>
            {
                var entry = Store.TryGet(m.ItemId);
                return entry == null || entry.FetchedAt < cutoff;
            }).ToList();
            _logger.Info("Seasonal keywords sync: " + pending.Count + " of " + movies.Count + " movies to look up, " + pruned + " gone from the library", 0);

            using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");
            var done = 0;
            foreach (var movie in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var keywords = await FetchKeywordsAsync(client, movie.TmdbId, apiKey!, cancellationToken).ConfigureAwait(false);
                    if (keywords != null) Store.Set(movie.ItemId, movie.TmdbId, keywords);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Warn("Could not fetch TMDB keywords for movie " + movie.TmdbId + ": " + ex.Message);
                }

                done++;
                if (done % FlushEveryN == 0) await Store.FlushAsync().ConfigureAwait(false);
                progress.Report((double)done / Math.Max(1, pending.Count) * 100);
                await Task.Delay(DelayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
            }

            await Store.FlushAsync().ConfigureAwait(false);
            _logger.Info("Seasonal keywords sync complete: " + Store.Count + " movies in the store", 0);
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
                    IncludeItemTypes = new[] { "Movie" },
                    IsVirtualItem = false,
                    Recursive = true,
                    StartIndex = start,
                    Limit = LibraryPageSize
                };

                var page = _libraryManager.GetItemsResult(query).Items;
                foreach (var item in page)
                {
                    string? tmdbId = null;
                    item.ProviderIds?.TryGetValue("Tmdb", out tmdbId);
                    if (long.TryParse(tmdbId, out var id)) movies.Add((item.Id.ToString("N"), id));
                }

                if (page.Length < LibraryPageSize) return movies;
                start += LibraryPageSize;
            }
        }

        /// <summary>The keyword ids, an empty list when TMDB has none, or null when the movie is unknown to TMDB.</summary>
        private static async Task<List<int>?> FetchKeywordsAsync(HttpClient client, long tmdbId, string apiKey, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/movie/" + tmdbId + "/keywords");
            MoonfinHttp.ApplyTmdbAuth(request, apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var ids = new List<int>();
            if (doc.RootElement.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array)
            {
                foreach (var keyword in keywords.EnumerateArray())
                {
                    if (keyword.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number) ids.Add(id.GetInt32());
                }
            }
            return ids;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
        }
    }
}
