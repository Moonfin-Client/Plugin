using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches an item's production companies from TMDB, downloads their logos, and stores
    /// everything in <see cref="StudioLogoCacheService"/>. Shared by the on-demand endpoint and
    /// the scheduled sync task. Uses a per-call HttpClient.
    /// </summary>
    public class StudioLogoFetchService
    {
        private const string LogoBase = "https://image.tmdb.org/t/p/w500";

        private readonly StudioLogoCacheService _cache;
        private readonly ILogger _logger;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        public StudioLogoFetchService(StudioLogoCacheService cache, ILogger logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public async Task<List<StudioCompanyInfo>?> FetchAndCacheItemAsync(string type, string tmdbId, string apiKey, CancellationToken cancellationToken)
        {
            var url = $"https://api.themoviedb.org/3/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(tmdbId)}";
            using var client = CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            MoonfinHttp.ApplyTmdbAuth(request, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn("TMDB details returned status " + (int)response.StatusCode + " for " + type + "/" + tmdbId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
                    entry.HasLogo = await DownloadLogoAsync(company.Id, company.LogoPath!, client, cancellationToken).ConfigureAwait(false);

                entries.Add(entry);
            }

            _cache.SetItem(type, tmdbId, entries);

            return entries
                .Select(e => new StudioCompanyInfo { Id = e.Id, Name = e.Name, HasLogo = e.HasLogo })
                .ToList();
        }

        public async Task<bool> EnsureImageAsync(int companyId, CancellationToken cancellationToken)
        {
            if (_cache.HasImage(companyId)) return true;

            var logoPath = _cache.GetLogoPath(companyId);
            if (string.IsNullOrWhiteSpace(logoPath)) return false;

            using var client = CreateClient();
            return await DownloadLogoAsync(companyId, logoPath!, client, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> DownloadLogoAsync(int companyId, string logoPath, HttpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var url = LogoBase + logoPath;
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length == 0) return false;

                _cache.SaveImage(companyId, bytes);
                return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Failed to download studio logo for company " + companyId + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// TMDB numbers networks and production companies in separate spaces, so a
        /// network id can equal an unrelated company id. The cache keys entries and
        /// logo files by this id alone, so networks are stored negated to keep the
        /// two from overwriting each other.
        /// </summary>
        public static int NetworkCacheId(int networkId) => -networkId;

        private static HttpClient CreateClient() => MoonfinHttp.CreateClient(TimeSpan.FromSeconds(20), "Moonfin/1.0");

        private class TmdbDetailsResponse
        {
            [JsonPropertyName("production_companies")] public List<TmdbCompany>? ProductionCompanies { get; set; }
            [JsonPropertyName("networks")] public List<TmdbCompany>? Networks { get; set; }
        }

        private class TmdbCompany
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("logo_path")] public string? LogoPath { get; set; }
        }
    }
}
