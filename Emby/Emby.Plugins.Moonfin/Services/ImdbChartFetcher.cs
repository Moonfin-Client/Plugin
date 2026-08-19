using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Fetches IMDb charts via the public IMDb GraphQL endpoint. Shared by the IMDb lists
    /// scheduled task and the custom-rows API (imdb source). Uses a per-call HttpClient. There is no IHttpClientFactory on Emby.
    /// </summary>
    public class ImdbChartFetcher
    {
        private readonly ILogger _logger;

        public static readonly Dictionary<string, string> ChartMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "imdb_top_250_movies", "TOP_RATED_MOVIES" },
            { "imdb_top_250_tv_shows", "TOP_RATED_TV_SHOWS" },
            { "imdb_most_popular_movies", "MOST_POPULAR_MOVIES" },
            { "imdb_most_popular_tv_shows", "MOST_POPULAR_TV_SHOWS" },
            { "imdb_lowest_rated_movies", "LOWEST_RATED_MOVIES" },
            { "imdb_top_english_movies", "TOP_RATED_ENGLISH_MOVIES" }
        };

        public ImdbChartFetcher(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<List<CustomRowItem>> FetchChartAsync(string key, CancellationToken cancellationToken)
        {
            if (!ChartMap.TryGetValue(key, out var chartType))
                throw new ArgumentException("Invalid chart key: " + key);

            const string endpoint = "https://caching.graphql.imdb.com/";
            const int limit = 250;

            var query = @"
{
  chartTitles(chart: {chartType: " + chartType + @"}, first: " + limit + @") {
    edges {
      node {
        id
        titleText {
          text
        }
        primaryImage {
          url
        }
        releaseYear {
          year
        }
        titleType {
          id
        }
      }
    }
  }
}
";

            var payload = new { query = query };

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.imdb.com");
            request.Headers.Referrer = new Uri("https://www.imdb.com/");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn("IMDb GraphQL returned status code " + (int)response.StatusCode);
                return new List<CustomRowItem>();
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("chartTitles", out var chartEl) ||
                !chartEl.TryGetProperty("edges", out var edgesEl) ||
                edgesEl.ValueKind != JsonValueKind.Array)
            {
                _logger.Warn("IMDb GraphQL response is missing expected structure");
                return new List<CustomRowItem>();
            }

            var items = new List<CustomRowItem>();
            var rank = 1;
            foreach (var edge in edgesEl.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out var node)) continue;

                var id = node.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                var title = "Unknown Title";
                if (node.TryGetProperty("titleText", out var textEl) && textEl.TryGetProperty("text", out var textProp))
                    title = textProp.GetString() ?? "Unknown Title";

                string? posterUrl = null;
                if (node.TryGetProperty("primaryImage", out var imgEl) && imgEl.ValueKind == JsonValueKind.Object && imgEl.TryGetProperty("url", out var urlProp))
                    posterUrl = urlProp.GetString();

                int? year = null;
                if (node.TryGetProperty("releaseYear", out var yearEl) && yearEl.ValueKind == JsonValueKind.Object && yearEl.TryGetProperty("year", out var yearProp)
                    && yearProp.ValueKind == JsonValueKind.Number && yearProp.TryGetInt32(out var parsedYear))
                    year = parsedYear;

                var typeId = "movie";
                if (node.TryGetProperty("titleType", out var typeEl) && typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("id", out var typeIdProp))
                    typeId = typeIdProp.GetString() ?? "movie";

                var type = (typeId == "tvSeries" || typeId == "tvMiniSeries") ? "Series" : "Movie";

                items.Add(new CustomRowItem
                {
                    Name = title,
                    Type = type,
                    ProductionYear = year,
                    Rank = rank++,
                    PosterUrl = posterUrl,
                    BackdropUrl = posterUrl,
                    ProviderIds = new CustomRowItemProviderIds { Imdb = id }
                });
            }

            return items;
        }
    }
}
