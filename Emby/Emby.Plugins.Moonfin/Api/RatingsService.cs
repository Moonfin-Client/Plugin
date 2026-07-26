using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class RatingsService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan TmdbCacheTtl = TimeSpan.FromHours(24);

        private static readonly ConcurrentDictionary<string, (object Response, DateTimeOffset CachedAt)> _tmdbSeasonCache = new ConcurrentDictionary<string, (object, DateTimeOffset)>();
        private static readonly ConcurrentDictionary<string, (object Response, DateTimeOffset CachedAt)> _tmdbEpisodeCache = new ConcurrentDictionary<string, (object, DateTimeOffset)>();

        // Remember failed upstream lookups briefly so a burst of requests during a bad-key
        // or rate-limit spell doesn't hammer api.mdblist.com.
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string Error)> _negativeCache =
            new ConcurrentDictionary<string, (DateTimeOffset, string)>();

        // Debounced disk flush for on-demand cache writes (the batch task also flushes).
        private static readonly TimeSpan FlushDebounce = TimeSpan.FromSeconds(30);
        private static DateTimeOffset _lastFlushRequest = DateTimeOffset.MinValue;

        private static readonly string[] DefaultRatingSources = { "imdb", "tmdb", "tomatoes", "metacritic" };

        private static readonly JsonSerializerOptions JsonOpts = MdbListApiHelper.JsonOptions;

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private MoonfinSettingsService Settings => Plugin.Instance?.SettingsService
            ?? throw new InvalidOperationException("MoonfinSettingsService not initialized");

        private MdbListCacheService MdbListCache => Plugin.Instance?.MdbListCache
            ?? throw new InvalidOperationException("MdbListCacheService not initialized");

        private StudioLogoCacheService StudioLogoCache => Plugin.Instance?.StudioLogoCache
            ?? throw new InvalidOperationException("StudioLogoCacheService not initialized");

        private StudioLogoFetchService StudioLogoFetch => Plugin.Instance?.StudioLogoFetch
            ?? throw new InvalidOperationException("StudioLogoFetchService not initialized");

        private static TimeSpan StudioCacheMaxAge =>
            TimeSpan.FromDays(Plugin.Instance?.Configuration?.StudioLogosMaxAgeDays ?? 30);

        public RatingsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public async Task<object?> Get(GetMdbListRatingsRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.TmdbId))
                return Json(400, new { error = "Missing required parameters: type, tmdbId" });

            var type = request.Type!.Trim().ToLowerInvariant();
            if (type != "movie" && type != "show")
                return Json(400, new { error = "Invalid type. Expected: movie or show" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            var resolved = await Settings.GetResolvedProfileAsync(userId.Value, "global").ConfigureAwait(false);
            var apiKey = resolved?.MdblistApiKey;
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Plugin.Instance?.Configuration?.MdblistApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(new { success = false, error = "No MDBList API key configured.", ratings = Array.Empty<object>() });

            var cacheKey = $"{type}:{request.TmdbId!.Trim()}";
            var allRatings = MdbListCache.TryGet(cacheKey, CacheTtl);

            if (allRatings == null)
            {
                if (_negativeCache.TryGetValue(cacheKey, out var negative))
                {
                    if (DateTimeOffset.UtcNow - negative.At < NegativeCacheTtl)
                        return Json(new { success = false, error = negative.Error, ratings = Array.Empty<object>() });

                    _negativeCache.TryRemove(cacheKey, out _);
                }

                try
                {
                    // The canonical path has a trailing slash, without it a redirect can drop the query.
                    var url = $"{MdbListApiHelper.BaseUrl}/tmdb/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(request.TmdbId.Trim())}/?apikey={Uri.EscapeDataString(apiKey!)}";
                    using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");

                    var response = await client.GetAsync(url).ConfigureAwait(false);
                    if ((int)response.StatusCode == 429) return NegativeResult(cacheKey, "MDBList rate limit reached. Try again later.");
                    if (!response.IsSuccessStatusCode) return NegativeResult(cacheKey, $"MDBList returned status {(int)response.StatusCode}");

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var data = JsonSerializer.Deserialize<MdbListApiResponse>(json, JsonOpts);
                    allRatings = data?.Ratings ?? new List<MdbListRating>();
                    MdbListCache.Set(cacheKey, allRatings);
                    RequestFlush();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return NegativeResult(cacheKey, "Failed to fetch from MDBList: " + ex.Message);
                }
            }

            var filtered = FilterAndOrderRatings(allRatings, resolved?.MdblistRatingSources);
            return Json(new { success = true, ratings = filtered });
        }

        private object NegativeResult(string cacheKey, string error)
        {
            _negativeCache[cacheKey] = (DateTimeOffset.UtcNow, error);
            return Json(new { success = false, error, ratings = Array.Empty<object>() });
        }

        private void RequestFlush()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastFlushRequest < FlushDebounce) return;

            _lastFlushRequest = now;
            var cache = Plugin.Instance?.MdbListCache;
            if (cache != null)
                _ = Task.Run(() => cache.FlushAsync());
        }

        /// <summary>
        /// Validates an MDBList API key by proxying GET /user and returning plan/quota info.
        /// Admin-only, used by the "Test key" button on the plugin config page.
        /// Pass Key to test an unsaved key, otherwise the server-wide key is used.
        /// </summary>
        public async Task<object?> Get(GetMdbListKeyInfoRequest request)
        {
            var apiKey = string.IsNullOrWhiteSpace(request.Key)
                ? Plugin.Instance?.Configuration?.MdblistApiKey
                : request.Key!.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(new { success = false, error = "No MDBList API key configured." });

            try
            {
                var url = $"{MdbListApiHelper.BaseUrl}/user?apikey={Uri.EscapeDataString(apiKey!)}";
                using var client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");

                using var response = await client.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var reason = (int)response.StatusCode == 429
                        ? "MDBList rate limit reached. Try again later."
                        : $"MDBList rejected the key (status {(int)response.StatusCode}).";
                    return Json(new { success = false, error = reason });
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var info = JsonSerializer.Deserialize<MdbListAccountInfo>(json, JsonOpts);
                if (info == null || string.IsNullOrEmpty(info.Username))
                    return Json(new { success = false, error = "Unexpected response from MDBList." });

                return Json(new
                {
                    success = true,
                    username = info.Username,
                    plan = info.Plan,
                    isSupporter = info.IsSupporter,
                    apiRequests = info.ApiRequests,
                    apiRequestsCount = info.ApiRequestsCount,
                    rateLimitRemaining = info.RateLimitRemaining
                });
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Json(new { success = false, error = "Failed to reach MDBList: " + ex.Message });
            }
        }

        /// <summary>
        /// Drops every cached MDBList rating so lookups refetch from the API. Entries are only
        /// judged on age, so this is the one way to retire ratings that are cached but wrong.
        /// </summary>
        public async Task<object?> Post(ClearMdbListCacheRequest request)
        {
            _negativeCache.Clear();
            var cache = Plugin.Instance?.MdbListCache;
            var removed = cache != null ? await cache.ClearAsync().ConfigureAwait(false) : 0;
            return Json(new { success = true, removed });
        }

        private static List<MdbListRating> FilterAndOrderRatings(List<MdbListRating> allRatings, List<string>? selectedSources)
        {
            var sources = (selectedSources != null && selectedSources.Count > 0)
                ? (IReadOnlyList<string>)selectedSources : DefaultRatingSources;

            var bySource = new Dictionary<string, MdbListRating>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in allRatings)
                if (!string.IsNullOrEmpty(r.Source)) bySource[r.Source] = r;

            var result = new List<MdbListRating>();
            foreach (var src in sources)
            {
                // MDBList's raw source names are imdb, metacritic, metacriticuser, trakt,
                // tomatoes, popcorn, tmdb, letterboxd, rogerebert, and myanimelist. RT
                // critics arrive as "tomatoes" and RT audience as "popcorn". Callers ask
                // for popcorn as rtAudience or tomatoes_audience, so look it up there but
                // return it under the key they asked for.
                var lookupSource = src;
                if (string.Equals(src, "rtAudience", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(src, "tomatoes_audience", StringComparison.OrdinalIgnoreCase))
                {
                    lookupSource = "popcorn";
                }

                if (bySource.TryGetValue(lookupSource, out var r))
                {
                    var ratingClone = new MdbListRating
                    {
                        Source = src,
                        Value = r.Value,
                        Score = r.Score,
                        Votes = r.Votes,
                        Url = r.Url
                    };

                    // Letterboxd comes from MDBList on an ambiguous 0-10 scale. Normalize it to
                    // the native 0-5 scale so clients render the stars correctly.
                    if (string.Equals(ratingClone.Source, "letterboxd", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ratingClone.Score.HasValue)
                        {
                            ratingClone.Value = Math.Round(ratingClone.Score.Value / 20.0, 1);
                        }
                        else if (ratingClone.Value.HasValue)
                        {
                            var val = ratingClone.Value.Value;
                            ratingClone.Value = val > 5.0 ? Math.Round(val / 2.0, 1) : val;
                        }
                    }

                    result.Add(ratingClone);
                }
            }
            return result;
        }

        public async Task<object?> Get(GetTmdbEpisodeRatingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TmdbId))
                return Json(400, new { success = false, error = "Missing required parameter: tmdbId" });

            var apiKey = await GetUserTmdbApiKey().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(new { success = false, error = "No TMDB API key configured." });

            var cacheKey = $"{request.TmdbId.Trim()}:{request.Season}:{request.Episode}";
            if (_tmdbEpisodeCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < TmdbCacheTtl)
                return Json(cached.Response);

            try
            {
                var url = $"https://api.themoviedb.org/3/tv/{Uri.EscapeDataString(request.TmdbId.Trim())}/season/{request.Season}/episode/{request.Episode}";
                using var client = CreateTmdbClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                MoonfinHttp.ApplyTmdbAuth(req, apiKey!);
                using var response = await client.SendAsync(req).ConfigureAwait(false);

                if ((int)response.StatusCode == 429) return Json(new { success = false, error = "TMDB rate limit reached." });
                if (!response.IsSuccessStatusCode) return Json(new { success = false, error = $"TMDB returned status {(int)response.StatusCode}" });

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<TmdbEpisodeApiResponse>(json, JsonOpts);

                var result = new
                {
                    success = true, voteAverage = data?.VoteAverage, voteCount = data?.VoteCount,
                    name = data?.Name, airDate = data?.AirDate, seasonNumber = data?.SeasonNumber,
                    episodeNumber = data?.EpisodeNumber, stillPath = data?.StillPath
                };
                _tmdbEpisodeCache[cacheKey] = (result, DateTimeOffset.UtcNow);
                return Json(result);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Json(new { success = false, error = "Failed to fetch from TMDB: " + ex.Message });
            }
        }

        public async Task<object?> Get(GetTmdbSeasonRatingsRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TmdbId))
                return Json(400, new { success = false, error = "Missing required parameter: tmdbId" });

            var apiKey = await GetUserTmdbApiKey().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(new { success = false, error = "No TMDB API key configured." });

            var cacheKey = $"{request.TmdbId.Trim()}:{request.Season}";
            if (_tmdbSeasonCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < TmdbCacheTtl)
                return Json(cached.Response);

            try
            {
                var url = $"https://api.themoviedb.org/3/tv/{Uri.EscapeDataString(request.TmdbId.Trim())}/season/{request.Season}";
                using var client = CreateTmdbClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                MoonfinHttp.ApplyTmdbAuth(req, apiKey!);
                using var response = await client.SendAsync(req).ConfigureAwait(false);

                if ((int)response.StatusCode == 429) return Json(new { success = false, error = "TMDB rate limit reached." });
                if (!response.IsSuccessStatusCode) return Json(new { success = false, error = $"TMDB returned status {(int)response.StatusCode}" });

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<TmdbSeasonApiResponse>(json, JsonOpts);

                var episodes = new List<object>();
                if (data?.Episodes != null)
                    foreach (var ep in data.Episodes)
                    {
                        var epResult = new { success = true, voteAverage = ep.VoteAverage, voteCount = ep.VoteCount, name = ep.Name, airDate = ep.AirDate, seasonNumber = ep.SeasonNumber, episodeNumber = ep.EpisodeNumber, stillPath = ep.StillPath };
                        episodes.Add(epResult);
                        if (ep.EpisodeNumber.HasValue)
                        {
                            var epKey = $"{request.TmdbId.Trim()}:{request.Season}:{ep.EpisodeNumber.Value}";
                            _tmdbEpisodeCache[epKey] = (epResult, DateTimeOffset.UtcNow);
                        }
                    }

                var result = new { success = true, seasonName = data?.Name, episodes };
                _tmdbSeasonCache[cacheKey] = (result, DateTimeOffset.UtcNow);
                return Json(result);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Json(new { success = false, error = "Failed to fetch from TMDB: " + ex.Message });
            }
        }

        public async Task<object?> Get(GetProductionCompaniesRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TmdbId))
                return Json(400, new { error = "Missing required parameter: tmdbId" });

            var mediaType = string.Equals(request.Type?.Trim(), "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";

            var cached = StudioLogoCache.TryGetItem(mediaType, request.TmdbId!.Trim(), StudioCacheMaxAge);
            if (cached != null)
                return Json(new { success = true, companies = cached });

            var apiKey = await GetUserTmdbApiKey().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(new { success = false, error = "No TMDB API key configured." });

            try
            {
                var companies = await StudioLogoFetch.FetchAndCacheItemAsync(mediaType, request.TmdbId.Trim(), apiKey!, System.Threading.CancellationToken.None).ConfigureAwait(false);
                if (companies == null)
                    return Json(new { success = false, error = "Failed to fetch from TMDB." });

                await StudioLogoCache.FlushAsync().ConfigureAwait(false);
                return Json(new { success = true, companies });
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return Json(new { success = false, error = "Failed to fetch from TMDB: " + ex.Message });
            }
        }

        public async Task<object?> Get(GetStudioImageRequest request)
        {
            var ensured = await StudioLogoFetch.EnsureImageAsync(request.CompanyId, System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (!ensured)
            {
                Request.Response.StatusCode = 404;
                return null!;
            }

            var path = StudioLogoCache.GetImagePath(request.CompanyId);
            if (!System.IO.File.Exists(path))
            {
                Request.Response.StatusCode = 404;
                return null!;
            }

            var bytes = System.IO.File.ReadAllBytes(path);
            var headers = new Dictionary<string, string>
            {
                ["Cache-Control"] = "public,max-age=31536000,immutable",
                ["X-Content-Type-Options"] = "nosniff"
            };
            return ResultFactory.GetResult(Request, new System.IO.MemoryStream(bytes), "image/png", headers);
        }

        private async Task<string?> GetUserTmdbApiKey()
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return null;
            var resolved = await Settings.GetResolvedProfileAsync(userId.Value, "global").ConfigureAwait(false);
            var key = resolved?.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(key)) key = Plugin.Instance?.Configuration?.TmdbApiKey;
            return key;
        }

        private static HttpClient CreateTmdbClient() => MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");
    }

    internal class MdbListApiResponse
    {
        [JsonPropertyName("ratings")] public List<MdbListRating>? Ratings { get; set; }
    }

    internal class TmdbEpisodeApiResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("vote_average")] public float? VoteAverage { get; set; }
        [JsonPropertyName("vote_count")] public int? VoteCount { get; set; }
        [JsonPropertyName("season_number")] public int? SeasonNumber { get; set; }
        [JsonPropertyName("episode_number")] public int? EpisodeNumber { get; set; }
        [JsonPropertyName("air_date")] public string? AirDate { get; set; }
        [JsonPropertyName("still_path")] public string? StillPath { get; set; }
    }

    internal class TmdbSeasonApiResponse
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("episodes")] public List<TmdbEpisodeApiResponse>? Episodes { get; set; }
    }
}
