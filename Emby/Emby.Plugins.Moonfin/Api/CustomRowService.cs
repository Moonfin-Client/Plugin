using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Resolves fully custom home rows (imdb/mdblist/tmdb/tmdb_chart/letterboxd sources).
    /// Wire contract for the Flutter client: imdb hard-fails with 500 on error, other
    /// sources return 200 {success:false,error} on fetch failure.
    /// </summary>
    public class CustomRowService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        // Page through MDBList user lists 100 at a time and stop at 500 so a huge
        // list can't burn the whole API quota.
        private const int MdbListPageSize = 100;
        private const int MdbListMaxItems = 500;

        // One gate per chart so a burst of clients on an expired chart triggers a single fetch.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImdbFetchGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private ImdbChartFetcher ImdbFetcher => Plugin.Instance?.ImdbChartFetcher
            ?? throw new InvalidOperationException("ImdbChartFetcher not initialized");

        private MoonfinSettingsService Settings => Plugin.Instance?.SettingsService
            ?? throw new InvalidOperationException("MoonfinSettingsService not initialized");

        private CustomRowCacheService Cache => Plugin.Instance?.CustomRowCache
            ?? throw new InvalidOperationException("CustomRowCacheService not initialized");

        private ImdbListsCacheService ImdbCache => Plugin.Instance?.ImdbListsCache
            ?? throw new InvalidOperationException("ImdbListsCacheService not initialized");

        public CustomRowService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public async Task<object?> Get(GetCustomRowItemsRequest request)
        {
            var source = request.Source;
            var type = request.Type;
            var paramsJson = request.Params;

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(paramsJson))
                return Json(400, new { error = "Missing required parameters: source, type, params" });

            source = source!.Trim().ToLowerInvariant();
            type = type!.Trim().ToLowerInvariant();

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            if (source == "imdb")
            {
                try
                {
                    var imdbItems = await FetchImdbList(type).ConfigureAwait(false);
                    return Json(new { success = true, items = imdbItems });
                }
                catch (Exception ex)
                {
                    return Json(500, new { error = ex.Message });
                }
            }

            Dictionary<string, string> parsedParams;
            try
            {
                parsedParams = JsonSerializer.Deserialize<Dictionary<string, string>>(paramsJson!) ?? new Dictionary<string, string>();
            }
            catch (JsonException)
            {
                return Json(400, new { error = "params must be a JSON object of string values" });
            }

            // Older clients force a refresh by putting a "_nocache" timestamp in params.
            // Treat that as refresh=true and strip underscore-prefixed keys so the same
            // list always maps to the same cache entry.
            var refresh = request.Refresh;
            if (parsedParams.Keys.Any(k => k.StartsWith("_", StringComparison.Ordinal)))
            {
                refresh = true;
                parsedParams = parsedParams.Where(kv => !kv.Key.StartsWith("_", StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var canonicalParams = JsonSerializer.Serialize(
                new SortedDictionary<string, string>(parsedParams, StringComparer.Ordinal));
            var cacheKey = source + ":" + type + ":" + Sha256Hex(canonicalParams);

            if (!refresh)
            {
                var cached = Cache.TryGet(cacheKey, CacheTtl);
                if (cached != null)
                    return Json(new { success = true, items = cached });
            }

            using var client = CreateClient();
            try
            {
                List<CustomRowItem> items;

                switch (source)
                {
                    case "mdblist":
                        items = await FetchMdbList(type, parsedParams, userId.Value, client).ConfigureAwait(false);
                        break;
                    case "tmdb":
                        items = await FetchTmdb(type, parsedParams, userId.Value, client).ConfigureAwait(false);
                        break;
                    case "tmdb_chart":
                        items = await FetchTmdbChart(type, userId.Value, client).ConfigureAwait(false);
                        break;
                    case "letterboxd":
                        items = await FetchLetterboxd(type, parsedParams, userId.Value, client).ConfigureAwait(false);
                        break;
                    default:
                        return Json(400, new { error = "Unsupported custom row source: " + source });
                }

                // Don't cache empty results. An empty row cached for 24h looks like a
                // broken list when the real cause was an upstream hiccup.
                if (items.Count > 0)
                {
                    Cache.Set(cacheKey, items);
                    Cache.PruneOlderThan(TimeSpan.FromDays(7));
                    await Cache.FlushAsync().ConfigureAwait(false);
                }

                return Json(new { success = true, items });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        private async Task<List<CustomRowItem>> FetchMdbList(string type, Dictionary<string, string> paramsMap, Guid userId, HttpClient client)
        {
            var apiKey = await ResolveMdblistKey(userId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("MDBList API key is not configured.");

            paramsMap.TryGetValue("username", out var username);
            paramsMap.TryGetValue("listname", out var listname);

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(listname))
                throw new ArgumentException("MDBList requires both username and listname parameters.");

            var baseUrl = $"https://api.mdblist.com/lists/{Uri.EscapeDataString(username!)}/{Uri.EscapeDataString(listname!)}/items?apikey={Uri.EscapeDataString(apiKey!)}&limit={MdbListPageSize}&append_to_response=poster";

            var items = new List<CustomRowItem>();
            string? cursor = null;

            // Follow pagination.next_cursor until it's absent, that's the only
            // continuation signal the MDBList API guarantees.
            while (items.Count < MdbListMaxItems)
            {
                var url = cursor == null ? baseUrl : baseUrl + "&cursor=" + Uri.EscapeDataString(cursor);

                using var response = await client.GetAsync(url).ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                    throw new Exception("MDBList rate limit reached. Try again later.");
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"MDBList API returned status {(int)response.StatusCode}");

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var countBefore = items.Count;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    // A flat array is a single page with no pagination object.
                    AppendMdbListItems(root, items);
                    break;
                }

                if (root.ValueKind != JsonValueKind.Object) break;

                if (root.TryGetProperty("movies", out var moviesProp) && moviesProp.ValueKind == JsonValueKind.Array)
                    AppendMdbListItems(moviesProp, items);

                if (root.TryGetProperty("shows", out var showsProp) && showsProp.ValueKind == JsonValueKind.Array)
                    AppendMdbListItems(showsProp, items);

                cursor = null;
                if (root.TryGetProperty("pagination", out var paginationProp)
                    && paginationProp.ValueKind == JsonValueKind.Object
                    && paginationProp.TryGetProperty("next_cursor", out var cursorProp)
                    && cursorProp.ValueKind == JsonValueKind.String)
                {
                    cursor = cursorProp.GetString();
                }

                if (string.IsNullOrEmpty(cursor) || items.Count == countBefore) break;
            }

            // Movies and shows arrive as separate arrays per page, so restore true list order.
            items = items
                .OrderBy(i => i.Rank ?? int.MaxValue)
                .Take(MdbListMaxItems)
                .ToList();
            for (var i = 0; i < items.Count; i++)
                items[i].Rank = i + 1;

            // Fetch backdrops (and any posters MDBList didn't provide) from TMDb in parallel.
            var tmdbKey = await ResolveTmdbKey(userId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tmdbKey))
            {
                using var tmdbSemaphore = new SemaphoreSlim(15, 15);
                var tasks = items.Where(i => i.Id.HasValue).Select(async rowItem =>
                {
                    await tmdbSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var isShow = rowItem.Type == "Series";
                        var tmdbUrl = isShow
                            ? $"https://api.themoviedb.org/3/tv/{rowItem.Id}"
                            : $"https://api.themoviedb.org/3/movie/{rowItem.Id}";

                        using var req = new HttpRequestMessage(HttpMethod.Get, tmdbUrl);
                        MoonfinHttp.ApplyTmdbAuth(req, tmdbKey!);
                        using var resp = await client.SendAsync(req).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            var detailsJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(detailsJson);
                            var detailsRoot = doc.RootElement;
                            if (string.IsNullOrEmpty(rowItem.PosterUrl)
                                && detailsRoot.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                                rowItem.PosterUrl = pProp.GetString();
                            if (detailsRoot.TryGetProperty("backdrop_path", out var bProp) && bProp.ValueKind == JsonValueKind.String)
                                rowItem.BackdropUrl = bProp.GetString();
                        }
                    }
                    catch { /* best-effort enrichment */ }
                    finally { tmdbSemaphore.Release(); }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return items;
        }

        /// <summary>
        /// Parses one MDBList list-items array (movies or shows) into <paramref name="items"/>.
        /// Keeps each item's own MDBList rank so movies and shows can be re-merged in list order.
        /// </summary>
        private static void AppendMdbListItems(JsonElement itemsArray, List<CustomRowItem> items)
        {
            foreach (var item in itemsArray.EnumerateArray())
            {
                string? imdbId = null;
                string? tmdbId = null;

                if (item.TryGetProperty("ids", out var idsObj) && idsObj.ValueKind == JsonValueKind.Object)
                {
                    if (idsObj.TryGetProperty("imdb", out var imdbProp) && imdbProp.ValueKind == JsonValueKind.String)
                        imdbId = imdbProp.GetString();

                    if (idsObj.TryGetProperty("tmdb", out var tmdbProp))
                    {
                        tmdbId = tmdbProp.ValueKind == JsonValueKind.Number
                            ? tmdbProp.GetInt64().ToString()
                            : tmdbProp.ValueKind == JsonValueKind.String ? tmdbProp.GetString() : null;
                    }
                }

                if (string.IsNullOrWhiteSpace(imdbId) && item.TryGetProperty("imdb_id", out var imdbIdProp) && imdbIdProp.ValueKind == JsonValueKind.String)
                    imdbId = imdbIdProp.GetString();

                if (string.IsNullOrWhiteSpace(tmdbId) && item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    tmdbId = idProp.GetInt64().ToString();

                var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Unknown" : "Unknown";

                int? year = null;
                if (item.TryGetProperty("release_year", out var yrProp) && yrProp.ValueKind == JsonValueKind.Number)
                    year = yrProp.GetInt32();

                var mediaType = item.TryGetProperty("mediatype", out var mediaProp) ? mediaProp.GetString()?.ToLowerInvariant() : null;
                var finalType = (mediaType == "show" || mediaType == "shows" || mediaType == "series" || mediaType == "tv") ? "Series" : "Movie";

                int? rank = null;
                if (item.TryGetProperty("rank", out var rankProp) && rankProp.ValueKind == JsonValueKind.Number)
                    rank = rankProp.GetInt32();

                string? posterUrl = null;
                if (item.TryGetProperty("poster", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                    posterUrl = MdbListApiHelper.NormalizeTmdbImagePath(pProp.GetString());

                items.Add(new CustomRowItem
                {
                    Id = long.TryParse(tmdbId, out var parsedTmdbId) ? parsedTmdbId : (long?)null,
                    Name = title,
                    Type = finalType,
                    ProductionYear = year,
                    Rank = rank,
                    ProviderIds = new CustomRowItemProviderIds { Imdb = imdbId, Tmdb = tmdbId },
                    PosterUrl = posterUrl
                });
            }
        }

        private async Task<List<CustomRowItem>> FetchTmdb(string type, Dictionary<string, string> paramsMap, Guid userId, HttpClient client)
        {
            var apiKey = await ResolveTmdbKey(userId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("TMDB API Key is not configured.");

            paramsMap.TryGetValue("id", out var listId);
            if (string.IsNullOrWhiteSpace(listId))
                throw new ArgumentException("TMDB requires id parameter.");

            var isCollection = type == "movie_collection";
            var url = isCollection
                ? $"https://api.themoviedb.org/3/collection/{Uri.EscapeDataString(listId!)}"
                : $"https://api.themoviedb.org/3/list/{Uri.EscapeDataString(listId!)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            MoonfinHttp.ApplyTmdbAuth(request, apiKey!);

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"TMDB API returned status {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var items = new List<CustomRowItem>();
            int rank = 1;

            if (isCollection)
            {
                if (root.TryGetProperty("parts", out var partsProp) && partsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in partsProp.EnumerateArray())
                    {
                        var partId = part.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
                        var title = part.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Unknown" : "Unknown";
                        var dateStr = part.TryGetProperty("release_date", out var rdProp) ? rdProp.GetString() : null;
                        int? year = ParseYear(dateStr);

                        if (!string.IsNullOrEmpty(partId))
                        {
                            var posterPath = part.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                            var backdropPath = part.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;
                            items.Add(new CustomRowItem
                            {
                                Id = long.TryParse(partId, out var lbTmdbId) ? lbTmdbId : (long?)null,
                                Name = title,
                                Type = "Movie",
                                ProductionYear = year,
                                Rank = rank++,
                                ProviderIds = new CustomRowItemProviderIds { Tmdb = partId },
                                PosterUrl = posterPath,
                                BackdropUrl = backdropPath
                            });
                        }
                    }
                }
            }
            else
            {
                if (root.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsProp.EnumerateArray())
                    {
                        var itemId = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
                        var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                        if (string.IsNullOrEmpty(title) && item.TryGetProperty("name", out var nameProp))
                            title = nameProp.GetString();
                        title = title ?? "Unknown";

                        var dateStr = item.TryGetProperty("release_date", out var rdProp) ? rdProp.GetString() : null;
                        if (string.IsNullOrEmpty(dateStr) && item.TryGetProperty("first_air_date", out var fadProp))
                            dateStr = fadProp.GetString();

                        int? year = ParseYear(dateStr);

                        var mediaType = item.TryGetProperty("media_type", out var mtProp) ? mtProp.GetString() : null;
                        var finalType = mediaType == "tv" ? "Series" : "Movie";

                        if (!string.IsNullOrEmpty(itemId))
                        {
                            var posterPath = item.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                            var backdropPath = item.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;
                            items.Add(new CustomRowItem
                            {
                                Id = long.TryParse(itemId, out var lbTmdbId) ? lbTmdbId : (long?)null,
                                Name = title,
                                Type = finalType,
                                ProductionYear = year,
                                Rank = rank++,
                                ProviderIds = new CustomRowItemProviderIds { Tmdb = itemId },
                                PosterUrl = posterPath,
                                BackdropUrl = backdropPath
                            });
                        }
                    }
                }
            }

            return items;
        }

        private async Task<List<CustomRowItem>> FetchTmdbChart(string type, Guid userId, HttpClient client)
        {
            var apiKey = await ResolveTmdbKey(userId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("TMDB API Key is not configured.");

            // type is the API path, e.g. movie/popular, trending/movie/day, etc.
            var url = $"https://api.themoviedb.org/3/{type}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            MoonfinHttp.ApplyTmdbAuth(request, apiKey!);

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"TMDB API returned status {(int)response.StatusCode} for chart {type}");

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var items = new List<CustomRowItem>();
            int rank = 1;

            if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultsProp.EnumerateArray())
                {
                    var itemId = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64().ToString() : null;
                    var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                    if (string.IsNullOrEmpty(title) && item.TryGetProperty("name", out var nameProp))
                        title = nameProp.GetString();
                    title = title ?? "Unknown";

                    var dateStr = item.TryGetProperty("release_date", out var rdProp) ? rdProp.GetString() : null;
                    if (string.IsNullOrEmpty(dateStr) && item.TryGetProperty("first_air_date", out var fadProp))
                        dateStr = fadProp.GetString();

                    int? year = ParseYear(dateStr);

                    var mediaType = item.TryGetProperty("media_type", out var mtProp) ? mtProp.GetString() : null;
                    if (string.IsNullOrEmpty(mediaType))
                        mediaType = type.Contains("tv") ? "tv" : "movie";
                    var finalType = mediaType == "tv" ? "Series" : "Movie";

                    if (!string.IsNullOrEmpty(itemId))
                    {
                        var posterPath = item.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                        var backdropPath = item.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;
                        items.Add(new CustomRowItem
                        {
                            Id = long.TryParse(itemId, out var lbTmdbId) ? lbTmdbId : (long?)null,
                            Name = title,
                            Type = finalType,
                            ProductionYear = year,
                            Rank = rank++,
                            ProviderIds = new CustomRowItemProviderIds { Tmdb = itemId },
                            PosterUrl = posterPath,
                            BackdropUrl = backdropPath
                        });
                    }
                }
            }

            return items;
        }

        private async Task<List<CustomRowItem>> FetchLetterboxd(string type, Dictionary<string, string> paramsMap, Guid userId, HttpClient client)
        {
            paramsMap.TryGetValue("user", out var username);

            if (type == "user_list" || type == "watchlist" || type == "films")
                throw new ArgumentException("Direct HTML scraping of Letterboxd watchlists and lists is disabled due to their Terms of Service. Please import your list into MDBList and use the MDBList custom row source instead.");

            if (username != null) username = username.ToLowerInvariant().Trim();
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Letterboxd requires user parameter.");

            var baseUrl = $"https://letterboxd.com/{Uri.EscapeDataString(username!)}/rss/";

            var parsedFeedItems = new List<LetterboxdFeedItem>();

            using var response = await client.GetAsync(baseUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Letterboxd returned status {(int)response.StatusCode} for {baseUrl}");

            var xmlContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var document = XDocument.Parse(xmlContent);
            var rssItems = document.Descendants("item");

            foreach (var rssItem in rssItems)
            {
                var title = rssItem.Element("title")?.Value ?? "Unknown";
                var link = rssItem.Element("link")?.Value ?? "";

                var filmTitle = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "filmTitle")?.Value ?? title;
                var filmYearStr = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "filmYear")?.Value;
                var memberRatingStr = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "memberRating")?.Value;
                var resolvedTmdbId = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "movieId")?.Value;

                int? year = null;
                if (int.TryParse(filmYearStr, out var y)) year = y;

                double? rating = null;
                if (double.TryParse(memberRatingStr, out var r)) rating = r;

                var slugMatch = Regex.Match(link, @"film/([^/]+)/?");
                if (slugMatch.Success)
                {
                    parsedFeedItems.Add(new LetterboxdFeedItem
                    {
                        Title = filmTitle,
                        Year = year,
                        Rating = rating,
                        TmdbId = resolvedTmdbId
                    });
                }
            }

            var tmdbKey = await ResolveTmdbKey(userId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(tmdbKey))
            {
                using var tmdbSemaphore = new SemaphoreSlim(15, 15);
                var tasks = parsedFeedItems.Where(i => !string.IsNullOrEmpty(i.TmdbId)).Select(async pItem =>
                {
                    await tmdbSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var tmdbUrl = $"https://api.themoviedb.org/3/movie/{pItem.TmdbId}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, tmdbUrl);
                        MoonfinHttp.ApplyTmdbAuth(req, tmdbKey!);
                        using var resp = await client.SendAsync(req).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            var detailsJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(detailsJson);
                            var detailsRoot = doc.RootElement;
                            if (detailsRoot.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                                pItem.Title = titleProp.GetString() ?? pItem.Title;
                            if (detailsRoot.TryGetProperty("release_date", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
                            {
                                var y = ParseYear(rdProp.GetString());
                                if (y.HasValue) pItem.Year = y;
                            }
                            if (detailsRoot.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                                pItem.PosterUrl = pProp.GetString();
                            if (detailsRoot.TryGetProperty("backdrop_path", out var bProp) && bProp.ValueKind == JsonValueKind.String)
                                pItem.BackdropUrl = bProp.GetString();
                        }
                    }
                    catch { /* best-effort enrichment */ }
                    finally { tmdbSemaphore.Release(); }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var items = new List<CustomRowItem>();
            int rank = 1;
            foreach (var pItem in parsedFeedItems)
            {
                if (!string.IsNullOrEmpty(pItem.TmdbId))
                {
                    var stars = pItem.Rating.HasValue ? FormatRatingToStars(pItem.Rating.Value) : null;
                    items.Add(new CustomRowItem
                    {
                        Id = long.TryParse(pItem.TmdbId, out var lbTmdbId) ? lbTmdbId : (long?)null,
                        Name = pItem.Title,
                        Type = "Movie",
                        ProductionYear = pItem.Year,
                        Rank = rank++,
                        ProviderIds = new CustomRowItemProviderIds { Tmdb = pItem.TmdbId },
                        UserRating = stars,
                        Rating = pItem.Rating,
                        PosterUrl = pItem.PosterUrl,
                        BackdropUrl = pItem.BackdropUrl
                    });
                }
            }

            return items;
        }

        private async Task<List<CustomRowItem>> FetchImdbList(string type)
        {
            var cached = ImdbCache.TryGetItems(type, TimeSpan.FromDays(1));
            if (cached != null && cached.Count > 0) return cached;

            var gate = ImdbFetchGates.GetOrAdd(type, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var refreshed = ImdbCache.TryGetItems(type, TimeSpan.FromDays(1));
                if (refreshed != null && refreshed.Count > 0) return refreshed;

                try
                {
                    var items = await ImdbFetcher.FetchChartAsync(type, CancellationToken.None).ConfigureAwait(false);
                    if (items != null && items.Count > 0)
                    {
                        ImdbCache.SetItems(type, items);
                        await ImdbCache.FlushAsync().ConfigureAwait(false);
                        return items;
                    }
                }
                catch { /* fall through to stale cache */ }

                return ImdbCache.TryGetItems(type, TimeSpan.FromDays(30)) ?? new List<CustomRowItem>();
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<string?> ResolveMdblistKey(Guid userId)
        {
            var resolved = await Settings.GetResolvedProfileAsync(userId, "global").ConfigureAwait(false);
            var key = resolved?.MdblistApiKey;
            if (string.IsNullOrWhiteSpace(key)) key = Plugin.Instance?.Configuration?.MdblistApiKey;
            return key;
        }

        private async Task<string?> ResolveTmdbKey(Guid userId)
        {
            var resolved = await Settings.GetResolvedProfileAsync(userId, "global").ConfigureAwait(false);
            var key = resolved?.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(key)) key = Plugin.Instance?.Configuration?.TmdbApiKey;
            return key;
        }

        private static int? ParseYear(string? dateStr)
        {
            if (!string.IsNullOrEmpty(dateStr) && dateStr!.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var yr))
                return yr;
            return null;
        }

        private static string FormatRatingToStars(double rating)
        {
            int fullStars = (int)Math.Floor(rating);
            bool halfStar = (rating - fullStars) >= 0.25;
            var stars = new string('★', fullStars);
            if (halfStar) stars += "½";
            return stars;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }

        private static string Sha256Hex(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(hash).Replace("-", "");
        }
    }

    internal class LetterboxdFeedItem
    {
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public double? Rating { get; set; }
        public string? TmdbId { get; set; }
        public string? PosterUrl { get; set; }
        public string? BackdropUrl { get; set; }
    }
}
