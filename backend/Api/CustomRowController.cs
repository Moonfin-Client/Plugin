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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CustomRowController> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    
    // Persistent file mapping Letterboxd slugs to TMDb IDs
    private readonly string _letterboxdSlugsFilePath;
    private static ConcurrentDictionary<string, string>? _letterboxdSlugMap;
    private static readonly SemaphoreSlim _slugFileLock = new(1, 1);

    public CustomRowController(
        MoonfinSettingsService settingsService,
        CustomRowCacheService cacheService,
        IHttpClientFactory httpClientFactory,
        ILogger<CustomRowController> logger)
    {
        _settingsService = settingsService;
        _cacheService = cacheService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        if (!Directory.Exists(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        _letterboxdSlugsFilePath = Path.Combine(dataPath, "letterboxd_slug_to_tmdb.json");
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
                Id = string.IsNullOrWhiteSpace(tmdbId) ? null : long.Parse(tmdbId),
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
                        items.Add(new CustomRowItem
                        {
                            Id = long.Parse(partId),
                            Name = title,
                            Type = "Movie",
                            ProductionYear = year,
                            Rank = rank++,
                            ProviderIds = new CustomRowItemProviderIds
                            {
                                Tmdb = partId
                            },
                            PosterUrl = posterPath
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
                        items.Add(new CustomRowItem
                        {
                            Id = long.Parse(itemId),
                            Name = title,
                            Type = finalType,
                            ProductionYear = year,
                            Rank = rank++,
                            ProviderIds = new CustomRowItemProviderIds
                            {
                                Tmdb = itemId
                            },
                            PosterUrl = posterPath
                        });
                    }
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

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Letterboxd requires user parameter.");
        }

        var url = type switch
        {
            "watchlist" => $"https://letterboxd.com/{Uri.EscapeDataString(username)}/watchlist/rss/",
            "films" => $"https://letterboxd.com/{Uri.EscapeDataString(username)}/films/rss/",
            _ => $"https://letterboxd.com/{Uri.EscapeDataString(username)}/list/{Uri.EscapeDataString(listname ?? "")}/rss/"
        };

        var client = CreateClient();
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Letterboxd RSS feed returned status {(int)response.StatusCode}");
        }

        var xmlContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xmlContent);

        var items = new List<CustomRowItem>();
        var rssItems = document.Descendants("item");

        EnsureSlugsLoaded();

        // 1. Parse Letterboxd details from XML feed
        var parsedFeedItems = new List<LetterboxdFeedItem>();
        foreach (var rssItem in rssItems)
        {
            var title = rssItem.Element("title")?.Value ?? "Unknown";
            var link = rssItem.Element("link")?.Value ?? "";
            
            var filmTitle = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "filmTitle")?.Value ?? title;
            var filmYearStr = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "filmYear")?.Value;
            var memberRatingStr = rssItem.Elements().FirstOrDefault(e => e.Name.LocalName == "memberRating")?.Value;

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
                    Slug = slug
                });
            }
        }

        // 2. Resolve TMDB IDs for each slug
        var slugsToFetch = new List<string>();
        foreach (var pItem in parsedFeedItems)
        {
            if (_letterboxdSlugMap!.TryGetValue(pItem.Slug, out var tmdbId))
            {
                pItem.TmdbId = tmdbId;
            }
            else
            {
                slugsToFetch.Add(pItem.Slug);
            }
        }

        // Fetch unresolved slugs sequentially with a minor delay to avoid rate limit
        if (slugsToFetch.Count > 0)
        {
            foreach (var slug in slugsToFetch)
            {
                try
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    var filmUrl = $"https://letterboxd.com/film/{slug}/";
                    using var filmResp = await client.GetAsync(filmUrl, cancellationToken).ConfigureAwait(false);
                    if (filmResp.IsSuccessStatusCode)
                    {
                        var filmHtml = await filmResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        var tmdbMatch = Regex.Match(filmHtml, @"data-tmdb-id=""(\d+)""");
                        if (tmdbMatch.Success)
                        {
                            var resolvedId = tmdbMatch.Groups[1].Value;
                            _letterboxdSlugMap![slug] = resolvedId;
                            
                            // Map to items in this run
                            foreach (var pItem in parsedFeedItems.Where(i => i.Slug == slug))
                            {
                                pItem.TmdbId = resolvedId;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve TMDb ID for Letterboxd slug: {Slug}", slug);
                }
            }

            await FlushSlugsAsync().ConfigureAwait(false);
        }

        // 2.5 Fetch poster paths from TMDb API in parallel if TMDB key is configured
        var resolvedProfile = await _settingsService.GetResolvedProfileAsync(userId, "global");
        var tmdbKey = resolvedProfile?.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            tmdbKey = MoonfinPlugin.Instance?.Configuration?.TmdbApiKey;
        }

        if (!string.IsNullOrWhiteSpace(tmdbKey))
        {
            var movieTasks = parsedFeedItems.Where(i => !string.IsNullOrEmpty(i.TmdbId)).Select(async pItem =>
            {
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
                        if (detailsRoot.TryGetProperty("poster_path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                        {
                            pItem.PosterUrl = pProp.GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch TMDb details for movie ID: {Id}", pItem.TmdbId);
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
                    Id = long.Parse(pItem.TmdbId),
                    Name = pItem.Title,
                    Type = "Movie", // Letterboxd is strictly movies
                    ProductionYear = pItem.Year,
                    Rank = rank++,
                    ProviderIds = new CustomRowItemProviderIds
                    {
                        Tmdb = pItem.TmdbId
                    },
                    UserRating = stars,
                    PosterUrl = pItem.PosterUrl
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

    private void EnsureSlugsLoaded()
    {
        if (_letterboxdSlugMap != null) return;

        _slugFileLock.Wait();
        try
        {
            if (_letterboxdSlugMap != null) return;

            if (System.IO.File.Exists(_letterboxdSlugsFilePath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(_letterboxdSlugsFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    _letterboxdSlugMap = dict != null
                        ? new ConcurrentDictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase)
                        : new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    _letterboxdSlugMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                _letterboxdSlugMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _slugFileLock.Release();
        }
    }

    private async Task FlushSlugsAsync()
    {
        if (_letterboxdSlugMap == null) return;

        await _slugFileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(_letterboxdSlugMap);
            await System.IO.File.WriteAllTextAsync(_letterboxdSlugsFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush Letterboxd slugs map to disk");
        }
        finally
        {
            _slugFileLock.Release();
        }
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
