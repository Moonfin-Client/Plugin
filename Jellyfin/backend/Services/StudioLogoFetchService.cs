using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fetches an item's production companies from TMDB, downloads their logos, and
/// stores everything in <see cref="StudioLogoCacheService"/>. Shared by the
/// on-demand controller endpoint and the scheduled sync task.
/// </summary>
public class StudioLogoFetchService
{
    private const string LogoBase = "https://image.tmdb.org/t/p/w500";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StudioLogoCacheService _cache;
    private readonly ILogger<StudioLogoFetchService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public StudioLogoFetchService(
        IHttpClientFactory httpClientFactory,
        StudioLogoCacheService cache,
        ILogger<StudioLogoFetchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// TMDB numbers networks and production companies in separate spaces, so a
    /// network id can equal an unrelated company id. The cache keys entries and
    /// logo files by this id alone, so networks are stored negated to keep the
    /// two from overwriting each other.
    /// </summary>
    public static int NetworkCacheId(int networkId) => -networkId;

    /// <summary>
    /// Fetches the companies for a TMDB movie/tv item, downloads any logos, caches
    /// them, and returns the company list. Returns null when TMDB can't be reached.
    /// </summary>
    public async Task<List<StudioCompanyInfo>?> FetchAndCacheItemAsync(
        string type, string tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        var url = $"https://api.themoviedb.org/3/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(tmdbId)}";
        var client = CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, apiKey);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("TMDB details returned status {Status} for {Type}/{Id}", (int)response.StatusCode, type, tmdbId);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var details = JsonSerializer.Deserialize<TmdbDetailsResponse>(json, JsonOptions);
        var companies = new List<TmdbCompany>();
        if (details?.ProductionCompanies != null) companies.AddRange(details.ProductionCompanies);
        if (details?.Networks != null)
        {
            foreach (var network in details.Networks)
            {
                companies.Add(new TmdbCompany
                {
                    Id = NetworkCacheId(network.Id),
                    Name = network.Name,
                    LogoPath = network.LogoPath
                });
            }
        }

        var entries = new List<StudioCompanyEntry>();
        foreach (var company in companies)
        {
            if (string.IsNullOrWhiteSpace(company.Name)) continue;

            var entry = new StudioCompanyEntry
            {
                Id = company.Id,
                Name = company.Name!,
                LogoPath = company.LogoPath,
                HasLogo = false
            };

            if (!string.IsNullOrWhiteSpace(company.LogoPath))
            {
                entry.HasLogo = await DownloadLogoAsync(company.Id, company.LogoPath!, client, cancellationToken).ConfigureAwait(false);
            }

            entries.Add(entry);
        }

        _cache.SetItem(type, tmdbId, entries);

        return entries
            .Select(e => new StudioCompanyInfo { Id = e.Id, Name = e.Name, HasLogo = e.HasLogo })
            .ToList();
    }

    /// <summary>
    /// Ensures a company's logo file exists, re-downloading from the cached TMDB
    /// path if the file was evicted. Returns false when no logo is known.
    /// </summary>
    public async Task<bool> EnsureImageAsync(int companyId, CancellationToken cancellationToken)
    {
        if (_cache.HasImage(companyId)) return true;

        var logoPath = _cache.GetLogoPath(companyId);
        if (string.IsNullOrWhiteSpace(logoPath)) return false;

        return await DownloadLogoAsync(companyId, logoPath, CreateClient(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DownloadLogoAsync(int companyId, string logoPath, HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{LogoBase}{logoPath}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) return false;

            await _cache.SaveImageAsync(companyId, bytes).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download studio logo for company {Id}", companyId);
            return false;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");
        return client;
    }

    private static void ApplyAuth(HttpRequestMessage request, string apiKey)
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

    private class TmdbDetailsResponse
    {
        [JsonPropertyName("production_companies")]
        public List<TmdbCompany>? ProductionCompanies { get; set; }

        [JsonPropertyName("networks")]
        public List<TmdbCompany>? Networks { get; set; }
    }

    private class TmdbCompany
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("logo_path")]
        public string? LogoPath { get; set; }
    }
}
