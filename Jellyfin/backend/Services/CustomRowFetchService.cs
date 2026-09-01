using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Service responsible for resolving and fetching custom home rows from external providers
/// (MDBList, TMDB, Letterboxd, IMDb). Used by CustomRowController and CustomRowsSyncTask.
/// </summary>
public class CustomRowFetchService
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly ImdbListsCacheService _imdbCacheService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CustomRowFetchService> _logger;
    private readonly ILogger<ImdbListsTask> _taskLogger;

    private const int MdbListPageSize = 100;
    private const int MdbListMaxItems = 500;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImdbFetchGates =
        new(StringComparer.OrdinalIgnoreCase);

    public CustomRowFetchService(
        MoonfinSettingsService settingsService,
        ImdbListsCacheService imdbCacheService,
        IHttpClientFactory httpClientFactory,
        ILogger<CustomRowFetchService> logger,
        ILogger<ImdbListsTask> taskLogger)
    {
        _settingsService = settingsService;
        _imdbCacheService = imdbCacheService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _taskLogger = taskLogger;
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient("MoonfinHttpClient");

    public static string ComputeCacheKey(string source, string type, Dictionary<string, string> parsedParams)
    {
        var canonicalParams = JsonSerializer.Serialize(
            new SortedDictionary<string, string>(parsedParams, StringComparer.Ordinal));
        var paramHash = GetStringSha256Hash(canonicalParams);
        return $"{source.ToLowerInvariant()}:{type.ToLowerInvariant()}:{paramHash}";
    }

    public static string GetStringSha256Hash(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        using var sha = SHA256.Create();
        var textData = Encoding.UTF8.GetBytes(text);
        var hashData = sha.ComputeHash(textData);
        return Convert.ToHexString(hashData);
    }

    public async Task<List<CustomRowItem>> FetchCustomRowAsync(
        string source,
        string type,
        Dictionary<string, string> parsedParams,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        source = source.Trim().ToLowerInvariant();
        type = type.Trim().ToLowerInvariant();

        if (source == "imdb")
        {
            return await FetchImdbList(type, cancellationToken);
        }

        return source switch
        {
            "mdblist" => await FetchMdbList(type, parsedParams, userId, cancellationToken),
            "tmdb" => await FetchTmdb(type, parsedParams, userId, cancellationToken),
            "tmdb_chart" => await FetchTmdbChart(type, userId, cancellationToken),
            "letterboxd" => await FetchLetterboxd(type, parsedParams, userId, cancellationToken),
            _ => throw new ArgumentException($"Unsupported custom row source: {source}")
        };
    }

    private async Task<string?> ResolveTmdbApiKey(Guid? userId)
    {
        if (userId.HasValue)
        {
            var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, "global");
            if (!string.IsNullOrWhiteSpace(resolved?.TmdbApiKey))
            {
                return resolved.TmdbApiKey;
            }
        }

        return MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
    }

    private async Task<string?> ResolveMdblistApiKey(Guid? userId)
    {
        if (userId.HasValue)
        {
            var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, "global");
            if (!string.IsNullOrWhiteSpace(resolved?.MdblistApiKey))
            {
                return resolved.MdblistApiKey;
            }
        }

        return MoonfinPlugin.Instance?.Configuration?.MdblistApiKey;
    }

    private async Task<List<CustomRowItem>> FetchMdbList(
        string type,
        Dictionary<string, string> paramsMap,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveMdblistApiKey(userId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("MDBList API key is not configured.");
        }

        paramsMap.TryGetValue("username", out var username);
        paramsMap.TryGetValue("listname", out var listname);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(listname))
        {
            throw new ArgumentException("MDBList requires both username and listname parameters.");
        }

        var baseUrl = $"https://api.mdblist.com/lists/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(listname)}/items?apikey={Uri.EscapeDataString(apiKey)}&limit={MdbListPageSize}&append_to_response=poster";
        var client = CreateClient();

        var items = new List<CustomRowItem>();
        string? cursor = null;

        while (items.Count < MdbListMaxItems)
        {
            var url = cursor == null ? baseUrl : $"{baseUrl}&cursor={Uri.EscapeDataString(cursor)}";

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
            {
                throw new Exception("MDBList rate limit reached. Try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"MDBList API returned status {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var countBefore = items.Count;

            if (root.ValueKind == JsonValueKind.Array)
            {
                AppendMdbListItems(root, items);
                break;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            if (root.TryGetProperty("movies", out var moviesProp) && moviesProp.ValueKind == JsonValueKind.Array)
            {
                AppendMdbListItems(moviesProp, items);
            }

            if (root.TryGetProperty("shows", out var showsProp) && showsProp.ValueKind == JsonValueKind.Array)
            {
                AppendMdbListItems(showsProp, items);
            }

            cursor = null;
            if (root.TryGetProperty("pagination", out var paginationProp)
                && paginationProp.ValueKind == JsonValueKind.Object
                && paginationProp.TryGetProperty("next_cursor", out var cursorProp)
                && cursorProp.ValueKind == JsonValueKind.String)
            {
                cursor = cursorProp.GetString();
            }

            if (string.IsNullOrEmpty(cursor) || items.Count == countBefore)
            {
                break;
            }
        }

        items = items
            .OrderBy(i => i.Rank ?? int.MaxValue)
            .Take(MdbListMaxItems)
            .ToList();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Rank = i + 1;
        }

        var tmdbKey = await ResolveTmdbApiKey(userId);
        if (!string.IsNullOrWhiteSpace(tmdbKey))
        {
            using var tmdbSemaphore = new SemaphoreSlim(15, 15);
            var movieTasks = items.Where(i => i.Id.HasValue).Select(async rowItem =>
            {
                await tmdbSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var isShow = rowItem.Type == "Series";
                    var tmdbUrl = isShow 
                        ? $"https://api.themoviedb.org/3/tv/{rowItem.Id}"
                        : $"https://api.themoviedb.org/3/movie/{rowItem.Id}";

                    using var req = new HttpRequestMessage(HttpMethod.Get, tmdbUrl);
                    TmdbRequestHelper.ApplyAuth(req, tmdbKey);
                    using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var detailsJson = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(detailsJson);
                        var detailsRoot = doc.RootElement;
                        
                        if (string.IsNullOrEmpty(rowItem.PosterUrl) &&
                            detailsRoot.TryGetProperty("poster_path", out var pProp) &&
                            pProp.ValueKind == JsonValueKind.String)
                        {
                            rowItem.PosterUrl = pProp.GetString();
                        }
                        if (string.IsNullOrEmpty(rowItem.BackdropUrl) &&
                            detailsRoot.TryGetProperty("backdrop_path", out var bProp) &&
                            bProp.ValueKind == JsonValueKind.String)
                        {
                            rowItem.BackdropUrl = bProp.GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch TMDb details for {Type} ID: {Id}", rowItem.Type, rowItem.Id);
                }
                finally
                {
                    tmdbSemaphore.Release();
                }
            });
            await Task.WhenAll(movieTasks).ConfigureAwait(false);
        }

        return items;
    }

    private static void AppendMdbListItems(JsonElement itemsArray, List<CustomRowItem> items)
    {
        foreach (var item in itemsArray.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt64()
                : (long?)null;
            var rank = item.TryGetProperty("rank", out var rankProp) && rankProp.ValueKind == JsonValueKind.Number
                ? rankProp.GetInt32()
                : (int?)null;
            var title = item.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString() ?? string.Empty
                : string.Empty;
            var mediatype = item.TryGetProperty("mediatype", out var mediaProp) && mediaProp.ValueKind == JsonValueKind.String
                ? mediaProp.GetString() ?? "movie"
                : "movie";
            var year = item.TryGetProperty("release_year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number
                ? yearProp.GetInt32()
                : (int?)null;
            var imdbId = item.TryGetProperty("imdbid", out var imdbProp) && imdbProp.ValueKind == JsonValueKind.String
                ? imdbProp.GetString()
                : null;
            var tmdbId = item.TryGetProperty("tmdbid", out var tmdbProp) && tmdbProp.ValueKind == JsonValueKind.Number
                ? tmdbProp.GetInt64().ToString()
                : null;
            var posterUrl = item.TryGetProperty("poster", out var posterProp) && posterProp.ValueKind == JsonValueKind.String
                ? posterProp.GetString()
                : null;

            var finalType = mediatype.Equals("show", StringComparison.OrdinalIgnoreCase) ||
                            mediatype.Equals("tv", StringComparison.OrdinalIgnoreCase)
                ? "Series"
                : "Movie";

            items.Add(new CustomRowItem
            {
                Id = id,
                Name = title,
                Type = finalType,
                ProductionYear = year,
                Rank = rank,
                ProviderIds = new CustomRowItemProviderIds
                {
                    Imdb = imdbId,
                    Tmdb = tmdbId
                },
                PosterUrl = posterUrl
            });
        }
    }

    private async Task<List<CustomRowItem>> FetchTmdb(
        string type,
        Dictionary<string, string> paramsMap,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var tmdbKey = await ResolveTmdbApiKey(userId);
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            throw new InvalidOperationException("TMDB API key is not configured.");
        }

        paramsMap.TryGetValue("id", out var listId);
        if (string.IsNullOrWhiteSpace(listId))
        {
            throw new ArgumentException("TMDB requires id parameter.");
        }

        var client = CreateClient();
        var items = new List<CustomRowItem>();

        if (type == "movie_collection")
        {
            var url = $"https://api.themoviedb.org/3/collection/{Uri.EscapeDataString(listId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            TmdbRequestHelper.ApplyAuth(req, tmdbKey);
            using var response = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"TMDB Collection API returned status {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("parts", out var partsProp) && partsProp.ValueKind == JsonValueKind.Array)
            {
                int rank = 1;
                foreach (var part in partsProp.EnumerateArray())
                {
                    var partId = part.TryGetProperty("id", out var pId) ? pId.GetInt64().ToString() : string.Empty;
                    var title = part.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
                    var releaseDate = part.TryGetProperty("release_date", out var rdProp) ? rdProp.GetString() : null;
                    int? year = null;
                    if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate.Substring(0, 4), out var y))
                    {
                        year = y;
                    }
                    var posterPath = part.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                    var backdropPath = part.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;

                    items.Add(new CustomRowItem
                    {
                        Id = long.TryParse(partId, out var parsedId) ? parsedId : null,
                        Name = title,
                        Type = "Movie",
                        ProductionYear = year,
                        Rank = rank++,
                        ProviderIds = new CustomRowItemProviderIds
                        {
                            Tmdb = partId
                        },
                        PosterUrl = posterPath,
                        BackdropUrl = backdropPath
                    });
                }
            }
        }
        else
        {
            var url = $"https://api.themoviedb.org/3/list/{Uri.EscapeDataString(listId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            TmdbRequestHelper.ApplyAuth(req, tmdbKey);
            using var response = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"TMDB List API returned status {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
            {
                int rank = 1;
                foreach (var item in itemsProp.EnumerateArray())
                {
                    var itemId = item.TryGetProperty("id", out var iId) ? iId.GetInt64().ToString() : string.Empty;
                    var title = item.TryGetProperty("title", out var tProp)
                        ? tProp.GetString()
                        : (item.TryGetProperty("name", out var nProp) ? nProp.GetString() : string.Empty);
                    var mediaType = item.TryGetProperty("media_type", out var mProp) ? mProp.GetString() ?? "movie" : "movie";
                    var releaseDate = item.TryGetProperty("release_date", out var rdProp)
                        ? rdProp.GetString()
                        : (item.TryGetProperty("first_air_date", out var fadProp) ? fadProp.GetString() : null);
                    int? year = null;
                    if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate.Substring(0, 4), out var y))
                    {
                        year = y;
                    }
                    var finalType = mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie";
                    var posterPath = item.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                    var backdropPath = item.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;

                    items.Add(new CustomRowItem
                    {
                        Id = long.TryParse(itemId, out var parsedId) ? parsedId : null,
                        Name = title ?? string.Empty,
                        Type = finalType,
                        ProductionYear = year,
                        Rank = rank++,
                        ProviderIds = new CustomRowItemProviderIds
                        {
                            Tmdb = itemId
                        },
                        PosterUrl = posterPath,
                        BackdropUrl = backdropPath
                    });
                }
            }
        }

        return items;
    }

    private async Task<List<CustomRowItem>> FetchTmdbChart(
        string type,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var tmdbKey = await ResolveTmdbApiKey(userId);
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            throw new InvalidOperationException("TMDB API key is not configured.");
        }

        var endpoint = type switch
        {
            "trending_movies" => "trending/movie/week",
            "trending_shows" => "trending/tv/week",
            "top_rated_movies" => "movie/top_rated",
            "top_rated_shows" => "tv/top_rated",
            "popular_movies" => "movie/popular",
            "popular_shows" => "tv/popular",
            _ => throw new ArgumentException($"Unsupported TMDB chart type: {type}")
        };

        var url = $"https://api.themoviedb.org/3/{endpoint}";
        var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        TmdbRequestHelper.ApplyAuth(req, tmdbKey);
        using var response = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"TMDB API returned status {(int)response.StatusCode} for {endpoint}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var items = new List<CustomRowItem>();

        if (doc.RootElement.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
        {
            int rank = 1;
            foreach (var item in resultsProp.EnumerateArray())
            {
                var itemId = item.TryGetProperty("id", out var iId) ? iId.GetInt64().ToString() : string.Empty;
                var title = item.TryGetProperty("title", out var tProp)
                    ? tProp.GetString()
                    : (item.TryGetProperty("name", out var nProp) ? nProp.GetString() : string.Empty);
                var mediaType = item.TryGetProperty("media_type", out var mProp)
                    ? mProp.GetString()
                    : (type.Contains("show") ? "tv" : "movie");
                var releaseDate = item.TryGetProperty("release_date", out var rdProp)
                    ? rdProp.GetString()
                    : (item.TryGetProperty("first_air_date", out var fadProp) ? fadProp.GetString() : null);
                int? year = null;
                if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && int.TryParse(releaseDate.Substring(0, 4), out var y))
                {
                    year = y;
                }
                var finalType = (mediaType ?? "movie").Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Series" : "Movie";
                var posterPath = item.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                var backdropPath = item.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;

                items.Add(new CustomRowItem
                {
                    Id = long.TryParse(itemId, out var parsedId) ? parsedId : null,
                    Name = title ?? string.Empty,
                    Type = finalType,
                    ProductionYear = year,
                    Rank = rank++,
                    ProviderIds = new CustomRowItemProviderIds
                    {
                        Tmdb = itemId
                    },
                    PosterUrl = posterPath,
                    BackdropUrl = backdropPath
                });
            }
        }

        return items;
    }

    private async Task<List<CustomRowItem>> FetchLetterboxd(
        string type,
        Dictionary<string, string> paramsMap,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        paramsMap.TryGetValue("user", out var username);

        if (type == "user_list" || type == "watchlist" || type == "films")
        {
            throw new ArgumentException("Direct HTML scraping of Letterboxd watchlists and lists is disabled due to their Terms of Service. Please import your list into MDBList and use the MDBList custom row source instead.");
        }

        if (username != null)
        {
            username = username.ToLowerInvariant().Trim();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Letterboxd requires user parameter.");
        }

        var baseUrl = $"https://letterboxd.com/{Uri.EscapeDataString(username)}/rss/";
        var client = CreateClient();

        using var response = await client.GetAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Letterboxd returned status {(int)response.StatusCode} for {baseUrl}");
        }

        var xmlContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xmlContent);
        var rssItems = document.Descendants("item");

        var parsedFeedItems = new List<LetterboxdFeedItem>();

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
                var slug = slugMatch.Groups[1].Value;
                parsedFeedItems.Add(new LetterboxdFeedItem
                {
                    Title = filmTitle,
                    Year = year,
                    Rating = rating,
                    Slug = slug,
                    TmdbId = resolvedTmdbId
                });
            }
        }

        var tmdbKey = await ResolveTmdbApiKey(userId);
        if (!string.IsNullOrWhiteSpace(tmdbKey))
        {
            using var tmdbSemaphore = new SemaphoreSlim(15, 15);
            var movieTasks = parsedFeedItems.Where(i => !string.IsNullOrEmpty(i.TmdbId)).Select(async pItem =>
            {
                await tmdbSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var tmdbUrl = $"https://api.themoviedb.org/3/movie/{pItem.TmdbId}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, tmdbUrl);
                    TmdbRequestHelper.ApplyAuth(req, tmdbKey);
                    using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var detailsJson = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(detailsJson);
                        var detailsRoot = doc.RootElement;
                        if (detailsRoot.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
                        {
                            pItem.Title = titleProp.GetString() ?? pItem.Title;
                        }
                        if (detailsRoot.TryGetProperty("release_date", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
                        {
                            var dateStr = rdProp.GetString();
                            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var yr))
                            {
                                pItem.Year = yr;
                            }
                        }
                        if (detailsRoot.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                        {
                            pItem.PosterUrl = pProp.GetString();
                        }
                        if (detailsRoot.TryGetProperty("backdrop_path", out var bProp) && bProp.ValueKind == JsonValueKind.String)
                        {
                            pItem.BackdropUrl = bProp.GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch TMDb details for movie ID: {Id}", pItem.TmdbId);
                }
                finally
                {
                    tmdbSemaphore.Release();
                }
            });
            await Task.WhenAll(movieTasks).ConfigureAwait(false);
        }

        int rank = 1;
        var items = new List<CustomRowItem>();
        foreach (var pItem in parsedFeedItems)
        {
            if (string.IsNullOrEmpty(pItem.TmdbId)) continue;

            items.Add(new CustomRowItem
            {
                Id = long.TryParse(pItem.TmdbId, out var id) ? id : null,
                Name = pItem.Title,
                Type = "Movie",
                ProductionYear = pItem.Year,
                Rank = rank++,
                ProviderIds = new CustomRowItemProviderIds
                {
                    Tmdb = pItem.TmdbId
                },
                UserRating = pItem.Rating.HasValue ? pItem.Rating.Value.ToString("0.#") : null,
                PosterUrl = pItem.PosterUrl,
                BackdropUrl = pItem.BackdropUrl
            });
        }

        return items;
    }

    private async Task<List<CustomRowItem>> FetchImdbList(string type, CancellationToken cancellationToken)
    {
        var cached = _imdbCacheService.TryGetItems(type, TimeSpan.FromDays(1));
        if (cached != null && cached.Count > 0)
        {
            return cached;
        }

        var gate = ImdbFetchGates.GetOrAdd(type, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = _imdbCacheService.TryGetItems(type, TimeSpan.FromDays(1));
            if (refreshed != null && refreshed.Count > 0)
            {
                return refreshed;
            }

            _logger.LogInformation("IMDb chart {Type} cache miss or expired, fetching on-demand", type);
            try
            {
                var task = new ImdbListsTask(_httpClientFactory, _imdbCacheService, _taskLogger);
                var items = await task.FetchChartAsync(type, cancellationToken);
                if (items != null && items.Count > 0)
                {
                    _imdbCacheService.SetItems(type, items);
                    await _imdbCacheService.FlushAsync();
                    return items;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch IMDb chart {Type} on-demand", type);
            }

            return _imdbCacheService.TryGetItems(type, TimeSpan.FromDays(30)) ?? new List<CustomRowItem>();
        }
        finally
        {
            gate.Release();
        }
    }
}

internal class LetterboxdFeedItem
{
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public double? Rating { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string? TmdbId { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
}
