using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

[ApiController]
[Route("Moonfin/CustomRows")]
public class CustomRowController : ControllerBase
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly CustomRowCacheService _cacheService;
    private readonly ImdbListsCacheService _imdbCacheService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CustomRowController> _logger;
    private readonly ILogger<ImdbListsTask> _taskLogger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public CustomRowController(
        MoonfinSettingsService settingsService,
        CustomRowCacheService cacheService,
        ImdbListsCacheService imdbCacheService,
        IHttpClientFactory httpClientFactory,
        ILogger<CustomRowController> logger,
        ILogger<ImdbListsTask> taskLogger)
    {
        _settingsService = settingsService;
        _cacheService = cacheService;
        _imdbCacheService = imdbCacheService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _taskLogger = taskLogger;
    }

    [HttpGet("Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CustomRowResponse>> GetCustomRowItems(
        [FromQuery] string source,
        [FromQuery] string type,
        [FromQuery] string @params,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(@params))
        {
            return BadRequest(new { Error = "Missing required parameters: source, type, params" });
        }

        source = source.Trim().ToLowerInvariant();
        type = type.Trim().ToLowerInvariant();

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        if (source == "imdb")
        {
            try
            {
                var imdbItems = await FetchImdbList(type, cancellationToken);
                return Ok(new CustomRowResponse
                {
                    Success = true,
                    Items = imdbItems
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve IMDb custom row for type: {Type}", type);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
            }
        }

        var paramHash = GetStringSha256Hash(@params);
        var cacheKey = $"{source}:{type}:{paramHash}";

        var cachedItems = _cacheService.TryGet(cacheKey, CacheTtl);
        if (cachedItems != null)
        {
            return Ok(new CustomRowResponse
            {
                Success = true,
                Items = cachedItems
            });
        }

        try
        {
            var parsedParams = JsonSerializer.Deserialize<Dictionary<string, string>>(@params) ?? new();
            List<CustomRowItem> items = new();

            switch (source)
            {
                case "mdblist":
                    items = await FetchMdbList(type, parsedParams, userId.Value, cancellationToken);
                    break;
                case "tmdb":
                    items = await FetchTmdb(type, parsedParams, userId.Value, cancellationToken);
                    break;
                case "tmdb_chart":
                    items = await FetchTmdbChart(type, userId.Value, cancellationToken);
                    break;
                case "letterboxd":
                    items = await FetchLetterboxd(type, parsedParams, userId.Value, cancellationToken);
                    break;
                default:
                    return BadRequest(new { Error = $"Unsupported custom row source: {source}" });
            }

            _cacheService.Set(cacheKey, items);
            await _cacheService.FlushAsync();

            return Ok(new CustomRowResponse
            {
                Success = true,
                Items = items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve custom row for source: {Source}, type: {Type}", source, type);
            return Ok(new CustomRowResponse
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    private async Task<List<CustomRowItem>> FetchMdbList(
        string type,
        Dictionary<string, string> paramsMap,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var resolved = await _settingsService.GetResolvedProfileAsync(userId, "global");
        var apiKey = resolved?.MdblistApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = MoonfinPlugin.Instance?.Configuration?.MdblistApiKey;
        }

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

        var url = $"https://api.mdblist.com/lists/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(listname)}/items?apikey={Uri.EscapeDataString(apiKey)}&limit=250";
        var client = CreateClient();

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"MDBList API returned status {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var items = new List<CustomRowItem>();
        JsonElement itemsArray;

        if (root.ValueKind == JsonValueKind.Array)
        {
            itemsArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("movies", out var moviesProp) && moviesProp.ValueKind == JsonValueKind.Array)
        {
            itemsArray = moviesProp;
        }
        else
        {
            return items;
        }

        int rank = 1;
        foreach (var item in itemsArray.EnumerateArray())
        {
            string? imdbId = null;
            string? tmdbId = null;

            if (item.TryGetProperty("ids", out var idsObj))
            {
                if (idsObj.TryGetProperty("imdb", out var imdbProp) && imdbProp.ValueKind == JsonValueKind.String)
                {
                    imdbId = imdbProp.GetString();
                }
                if (idsObj.TryGetProperty("tmdb", out var tmdbProp))
                {
                    tmdbId = tmdbProp.ValueKind == JsonValueKind.Number 
                        ? tmdbProp.GetInt64().ToString() 
                        : tmdbProp.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(imdbId) && item.TryGetProperty("imdb_id", out var imdbIdProp) && imdbIdProp.ValueKind == JsonValueKind.String)
            {
                imdbId = imdbIdProp.GetString();
            }

            var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Unknown" : "Unknown";
            
            int? year = null;
            if (item.TryGetProperty("release_year", out var yrProp) && yrProp.ValueKind == JsonValueKind.Number)
            {
                year = yrProp.GetInt32();
            }

            var mediaType = item.TryGetProperty("mediatype", out var mediaProp) ? mediaProp.GetString()?.ToLowerInvariant() : null;
            var finalType = (mediaType == "show" || mediaType == "shows" || mediaType == "series" || mediaType == "tv") ? "Series" : "Movie";

            string? posterUrl = null;
            if (item.TryGetProperty("poster", out var pProp) && pProp.ValueKind == JsonValueKind.String)
            {
                posterUrl = pProp.GetString();
            }
            else if (item.TryGetProperty("ids", out var idsVal) && idsVal.TryGetProperty("poster", out var idpProp) && idpProp.ValueKind == JsonValueKind.String)
            {
                posterUrl = idpProp.GetString();
            }

            items.Add(new CustomRowItem
            {
                Id = long.TryParse(tmdbId, out var lbTmdbId) ? lbTmdbId : null,
                Name = title,
                Type = finalType,
                ProductionYear = year,
                Rank = rank++,
                ProviderIds = new CustomRowItemProviderIds
                {
                    Imdb = imdbId,
                    Tmdb = tmdbId
                },
                PosterUrl = posterUrl
            });
        }

        // Fetch posters and backdrops from TMDb API in parallel to support high-res and backdrops
        var resolvedProfile = await _settingsService.GetResolvedProfileAsync(userId, "global");
        var tmdbKey = resolvedProfile?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            tmdbKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        }

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
                    ApplyTmdbAuth(req, tmdbKey);
                    using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var detailsJson = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(detailsJson);
                        var detailsRoot = doc.RootElement;
                        
                        if (detailsRoot.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                        {
                            rowItem.PosterUrl = pProp.GetString();
                        }
                        if (detailsRoot.TryGetProperty("backdrop_path", out var bProp) && bProp.ValueKind == JsonValueKind.String)
                        {
                            rowItem.BackdropUrl = bProp.GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch TMDb details for MDBList item ID: {Id}", rowItem.Id);
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

    private async Task<List<CustomRowItem>> FetchTmdb(
        string type,
        Dictionary<string, string> paramsMap,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var resolved = await _settingsService.GetResolvedProfileAsync(userId, "global");
        var apiKey = resolved?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("TMDB API Key is not configured.");
        }

        paramsMap.TryGetValue("id", out var listId);
        if (string.IsNullOrWhiteSpace(listId))
        {
            throw new ArgumentException("TMDB requires id parameter.");
        }

        var isCollection = type == "movie_collection";
        var url = isCollection
            ? $"https://api.themoviedb.org/3/collection/{Uri.EscapeDataString(listId)}"
            : $"https://api.themoviedb.org/3/list/{Uri.EscapeDataString(listId)}";

        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyTmdbAuth(request, apiKey);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"TMDB API returned status {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
                    int? year = null;
                    if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var yr))
                    {
                        year = yr;
                    }

                    if (!string.IsNullOrEmpty(partId))
                    {
                        var posterPath = part.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                        var backdropPath = part.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;
                        items.Add(new CustomRowItem
                        {
                            Id = long.TryParse(partId, out var lbTmdbId) ? lbTmdbId : null,
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
                    {
                        title = nameProp.GetString();
                    }
                    title ??= "Unknown";

                    var dateStr = item.TryGetProperty("release_date", out var rdProp) ? rdProp.GetString() : null;
                    if (string.IsNullOrEmpty(dateStr) && item.TryGetProperty("first_air_date", out var fadProp))
                    {
                        dateStr = fadProp.GetString();
                    }

                    int? year = null;
                    if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var yr))
                    {
                        year = yr;
                    }

                    var mediaType = item.TryGetProperty("media_type", out var mtProp) ? mtProp.GetString() : null;
                    var finalType = mediaType == "tv" ? "Series" : "Movie";

                    if (!string.IsNullOrEmpty(itemId))
                    {
                        var posterPath = item.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                        var backdropPath = item.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;
                        items.Add(new CustomRowItem
                        {
                            Id = long.TryParse(itemId, out var lbTmdbId) ? lbTmdbId : null,
                            Name = title,
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
        }

        return items;
    }

    private async Task<List<CustomRowItem>> FetchTmdbChart(
        string type,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var resolved = await _settingsService.GetResolvedProfileAsync(userId, "global");
        var apiKey = resolved?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("TMDB API Key is not configured.");
        }

        // type is the API path, e.g. movie/popular, trending/movie/day, etc.
        var url = $"https://api.themoviedb.org/3/{type}";
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyTmdbAuth(request, apiKey);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"TMDB API returned status {(int)response.StatusCode} for chart {type}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
                {
                    title = nameProp.GetString();
                }
                title ??= "Unknown";

                var dateStr = item.TryGetProperty("release_date", out var rdProp) ? rdProp.GetString() : null;
                if (string.IsNullOrEmpty(dateStr) && item.TryGetProperty("first_air_date", out var fadProp))
                {
                    dateStr = fadProp.GetString();
                }

                int? year = null;
                if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var yr))
                {
                    year = yr;
                }

                var mediaType = item.TryGetProperty("media_type", out var mtProp) ? mtProp.GetString() : null;
                if (string.IsNullOrEmpty(mediaType))
                {
                    mediaType = type.Contains("tv") ? "tv" : "movie";
                }
                var finalType = mediaType == "tv" ? "Series" : "Movie";

                if (!string.IsNullOrEmpty(itemId))
                {
                    var posterPath = item.TryGetProperty("poster_path", out var pProp) ? pProp.GetString() : null;
                    var backdropPath = item.TryGetProperty("backdrop_path", out var bProp) ? bProp.GetString() : null;
                    items.Add(new CustomRowItem
                    {
                        Id = long.TryParse(itemId, out var lbTmdbId) ? lbTmdbId : null,
                        Name = title,
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

    private async Task<List<CustomRowItem>> FetchLetterboxd(
        string type,
        Dictionary<string, string> paramsMap,
        Guid userId,
        CancellationToken cancellationToken)
    {
        paramsMap.TryGetValue("user", out var username);
        paramsMap.TryGetValue("name", out var listname);
        paramsMap.TryGetValue("url", out var fullUrl);

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
        var items = new List<CustomRowItem>();

        var parsedFeedItems = new List<LetterboxdFeedItem>();

        using var response = await client.GetAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Letterboxd returned status {(int)response.StatusCode} for {baseUrl}");
        }
        var xmlContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

        // Fetch full details/posters from TMDb API in parallel (max 15 concurrent requests)
        var resolvedProfile = await _settingsService.GetResolvedProfileAsync(userId, "global");
        var tmdbKey = resolvedProfile?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            tmdbKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        }

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
                    ApplyTmdbAuth(req, tmdbKey);
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

        // 3. For successfully resolved TMDB IDs, build CustomRowItem lists
        int rank = 1;
        foreach (var pItem in parsedFeedItems)
        {
            if (!string.IsNullOrEmpty(pItem.TmdbId))
            {
                var stars = pItem.Rating.HasValue ? FormatRatingToStars(pItem.Rating.Value) : null;
                items.Add(new CustomRowItem
                {
                    Id = long.TryParse(pItem.TmdbId, out var lbTmdbId) ? lbTmdbId : null,
                    Name = pItem.Title,
                    Type = "Movie", // Letterboxd is strictly movies
                    ProductionYear = pItem.Year,
                    Rank = rank++,
                    ProviderIds = new CustomRowItemProviderIds
                    {
                        Tmdb = pItem.TmdbId
                    },
                    UserRating = stars,
                    Rating = pItem.Rating,
                    PosterUrl = pItem.PosterUrl,
                    BackdropUrl = pItem.BackdropUrl
                });
            }
        }

        return items;
    }

    private static string FormatRatingToStars(double rating)
    {
        int fullStars = (int)Math.Floor(rating);
        bool halfStar = (rating - fullStars) >= 0.25;
        var stars = new string('★', fullStars);
        if (halfStar) stars += "½";
        return stars;
    }



    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    private static void ApplyTmdbAuth(HttpRequestMessage request, string apiKey)
    {
        if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            var uriBuilder = new UriBuilder(request.RequestUri!);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            query["api_key"] = apiKey;
            uriBuilder.Query = query.ToString();
            request.RequestUri = uriBuilder.Uri;
        }
    }

    // One gate per chart so a burst of clients on an expired chart triggers a single scrape
    // instead of one per request. Only the handful of chart types ever get added, so the map
    // doesn't grow unbounded.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImdbFetchGates =
        new(StringComparer.OrdinalIgnoreCase);

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
            // Another request may have refilled the cache while we waited on the gate.
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

    private static string GetStringSha256Hash(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var textData = System.Text.Encoding.UTF8.GetBytes(text);
        var hashData = sha.ComputeHash(textData);
        return Convert.ToHexString(hashData);
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

public class CustomRowResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();
}
