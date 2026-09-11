using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fetches the MyAnimeList recap flags for a series, resolving the MyAnimeList id first.
/// </summary>
public class AnimeRecapFetchService
{
    
    private const string EpisodeApiBase = "https://api.tenrai.org/v1";


    private const string AniListEndpoint = "https://graphql.anilist.co";

    /// <summary>The API rate limits at roughly a few requests a second.</summary>
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(2);

    /// <summary>Guards against a malformed pagination response spinning forever. 100 episodes a page.</summary>
    private const int MaxPages = 30;

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AnimeIdMappingService _mapping;
    private readonly AnimeMarkerDiagnosticLog _diagnostics;
    private readonly ILogger<AnimeRecapFetchService> _logger;

    public AnimeRecapFetchService(
        IHttpClientFactory httpClientFactory,
        AnimeIdMappingService mapping,
        AnimeMarkerDiagnosticLog diagnostics,
        ILogger<AnimeRecapFetchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _mapping = mapping;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the MyAnimeList recap flags for a series, resolving the MyAnimeList id first.
    /// Returns null when the lookup failed, so the caller leaves the cached show alone instead of recording an empty answer.
    /// </summary>
    public async Task<int?> TryResolveMalIdAsync(BaseItem series, CancellationToken cancellationToken)
    {
        if (TryGetProviderInt(series, out var malId, "MyAnimeList", "Mal", "MAL"))
        {
            return malId;
        }

        // The mapping service caches the AniList->MyAnimeList mapping table, so it is used first to avoid a network request.
        await _mapping.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var mapped = _mapping.Resolve(series);
        if (mapped != null)
        {
            return mapped;
        }

        if (TryGetProviderInt(series, out var anilistId, "AniList", "Anilist"))
        {
            var viaAniList = await ResolveViaAniListAsync(anilistId, cancellationToken).ConfigureAwait(false);
            if (viaAniList != null)
            {
                return viaAniList;
            }
        }

        // Last resort and the least reliable of the three, since the search is fuzzy.
        return await ResolveByTitleAsync(series.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a series by its title using AniList's search API. 
    /// Returns null when the lookup failed, so the caller leaves the cached show alone 
    /// instead of recording an empty answer.
    /// </summary>
    private async Task<int?> ResolveByTitleAsync(string? title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            var body = JsonSerializer.Serialize(new
            {
                query = "query($search:String){Media(search:$search,type:ANIME){idMal title{romaji english native}}}",
                variables = new { search = title }
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(AniListEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Media", out var media) ||
                media.ValueKind != JsonValueKind.Object ||
                !media.TryGetProperty("idMal", out var idMal) ||
                idMal.ValueKind != JsonValueKind.Number ||
                !idMal.TryGetInt32(out var malId))
            {
                return null;
            }

            var wanted = AnimeTitleMatcher.Normalize(title);
            if (wanted.Length == 0 || !media.TryGetProperty("title", out var titles))
            {
                return null;
            }

            foreach (var field in new[] { "romaji", "english", "native" })
            {
                if (titles.TryGetProperty(field, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    AnimeTitleMatcher.Normalize(value.GetString()) == wanted)
                {
                    return malId;
                }
            }

            _logger.LogDebug(
                "Recap lookup: the AniList search for {Title} came back as a different show, ignoring it", title);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AniList title search failed for {Title}", title);
            return null;
        }
    }

    /// <summary>
    /// Fetches the MyAnimeList recap flags for a series, resolving the MyAnimeList id first.
    /// Returns null when the lookup failed, so the caller leaves the cached show alone instead of recording an empty answer.
    /// </summary>
    public async Task<RecapLookup?> FetchRecapsAsync(int malId, CancellationToken cancellationToken)
    {
        var recaps = new HashSet<int>();
        var episodeCount = 0;

        for (var page = 1; page <= MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await GetEpisodePageAsync(malId, page, cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                return null;
            }

            foreach (var episode in response.Data)
            {
                episodeCount++;

                if (episode.Recap)
                {
                    recaps.Add(episode.MalId);
                }
            }

            if (response.Pagination?.HasNextPage != true)
            {
                break;
            }
        }

        return new RecapLookup(recaps, episodeCount);
    }

    private const int MaxAttemptsPerPage = 3;

    private async Task<MalEpisodesResponse?> GetEpisodePageAsync(int malId, int page, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttemptsPerPage; attempt++)
        {
            var (result, retryable) = await TryGetEpisodePageAsync(malId, page, cancellationToken).ConfigureAwait(false);
            if (result != null || !retryable)
            {
                return result;
            }

            if (attempt < MaxAttemptsPerPage)
            {
                _logger.LogDebug(
                    "Recap lookup for MAL {MalId} page {Page} failed on attempt {Attempt}, retrying",
                    malId, page, attempt);
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<(MalEpisodesResponse? Page, bool Retryable)> TryGetEpisodePageAsync(
        int malId, int page, CancellationToken cancellationToken)
    {
        await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            var url = EpisodeApiBase + "/anime/" + malId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/episodes?page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var body = string.Empty;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (body.Length > 200)
                    {
                        body = body[..200];
                    }
                }
                catch
                {
                    // The status alone still tells us something.
                }

                _diagnostics.Write($"    episodes MAL {malId} page {page} -> HTTP {status} {body}");
                _logger.LogDebug(
                    "Recap lookup for MAL {MalId} page {Page} returned {Status}", malId, page, status);

                // 5xx and 429 are the API or MyAnimeList being briefly unavailable. A 404 is a
                // that the entry does not exist, and must not be retried.
                return (null, status >= 500 || status == 429);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<MalEpisodesResponse>(json, JsonOptions), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Recap lookup for MAL {MalId} page {Page} failed", malId, page);
            _diagnostics.Write(
                $"    episodes MAL {malId} page {page} -> {ex.GetType().Name}: {ex.Message}" +
                (ex.InnerException != null ? $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})" : string.Empty));

            // No response at all: a timeout or a dropped connection, both worth another go.
            return (null, true);
        }
    }

    private async Task<int?> ResolveViaAniListAsync(int anilistId, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            var body = JsonSerializer.Serialize(new
            {
                query = "query($id:Int){Media(id:$id,type:ANIME){idMal}}",
                variables = new { id = anilistId }
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(AniListEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("Media", out var media) &&
                media.ValueKind == JsonValueKind.Object &&
                media.TryGetProperty("idMal", out var idMal) &&
                idMal.ValueKind == JsonValueKind.Number &&
                idMal.TryGetInt32(out var malId))
            {
                return malId;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AniList idMal lookup failed for {AniListId}", anilistId);
        }

        return null;
    }

    /// <summary>Spaces episode requests process-wide.</summary>
    private static async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
            if (sinceLast < MinRequestSpacing)
            {
                await Task.Delay(MinRequestSpacing - sinceLast, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static bool TryGetProviderInt(BaseItem series, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (providerKey, providerValue) in series.ProviderIds)
            {
                if (string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(providerValue?.Trim(), out value))
                {
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// The episode numbers MyAnimeList marks as recaps, along with how many episodes the entry has.
    /// Returns null when the lookup failed, so the caller leaves the cached show alone instead of recording an empty answer.
    /// </summary>
    public readonly record struct RecapLookup(HashSet<int> RecapNumbers, int EpisodeCount);

    private class MalEpisodesResponse
    {
        [JsonPropertyName("data")]
        public List<MalEpisode> Data { get; set; } = new();

        [JsonPropertyName("pagination")]
        public MalPagination? Pagination { get; set; }
    }

    private class MalPagination
    {
        [JsonPropertyName("has_next_page")]
        public bool HasNextPage { get; set; }
    }

    private class MalEpisode
    {
        /// <summary>On the episodes endpoint this is the episode number within the entry.</summary>
        [JsonPropertyName("mal_id")]
        public int MalId { get; set; }

        [JsonPropertyName("recap")]
        public bool Recap { get; set; }
    }
}
