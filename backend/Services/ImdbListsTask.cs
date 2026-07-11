using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Scheduled task that fetches and caches IMDb lists server-side.
/// </summary>
public class ImdbListsTask : IScheduledTask
{
    public string Name => "Moonfin IMDb Lists Sync";
    public string Key => "Moonfin.Imdb.ListsSync";
    public string Description => "Fetches and caches IMDb lists (curated charts like Top 250) so clients can show them as home rows.";
    public string Category => "Moonfin";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImdbListsCacheService _cacheService;
    private readonly ILogger<ImdbListsTask> _logger;

    private static readonly Dictionary<string, string> ChartMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "imdb_top_250_movies", "TOP_RATED_MOVIES" },
        { "imdb_top_250_tv_shows", "TOP_RATED_TV_SHOWS" },
        { "imdb_most_popular_movies", "MOST_POPULAR_MOVIES" },
        { "imdb_most_popular_tv_shows", "MOST_POPULAR_TV_SHOWS" },
        { "imdb_lowest_rated_movies", "LOWEST_RATED_MOVIES" },
        { "imdb_top_english_movies", "TOP_RATED_ENGLISH_MOVIES" }
    };

    public ImdbListsTask(
        IHttpClientFactory httpClientFactory,
        ImdbListsCacheService cacheService,
        ILogger<ImdbListsTask> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        if (config != null && !config.ImdbListsEnabled)
        {
            _logger.LogInformation("IMDb lists sync skipped: disabled in plugin configuration");
            return;
        }

        progress.Report(0);
        var keys = ChartMap.Keys.ToList();
        var processed = 0;

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Fetching IMDb chart: {Key}", key);

            try
            {
                var items = await FetchChartAsync(key, cancellationToken).ConfigureAwait(false);
                if (items != null && items.Count > 0)
                {
                    _cacheService.SetItems(key, items);
                    _logger.LogInformation("Successfully cached {Count} items for IMDb chart: {Key}", items.Count, key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch IMDb chart {Key}", key);
            }

            processed++;
            progress.Report((double)processed / keys.Count * 100);
        }

        await _cacheService.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("IMDb lists sync complete");
        progress.Report(100);
    }

    public async Task<List<CustomRowItem>> FetchChartAsync(string key, CancellationToken cancellationToken)
    {
        if (!ChartMap.TryGetValue(key, out var chartType))
        {
            throw new ArgumentException($"Invalid chart key: {key}");
        }

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
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("IMDb GraphQL returned status code {Status}", (int)response.StatusCode);
            return new List<CustomRowItem>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("chartTitles", out var chartEl) ||
            !chartEl.TryGetProperty("edges", out var edgesEl) ||
            edgesEl.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("IMDb GraphQL response is missing expected structure");
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
            {
                title = textProp.GetString() ?? "Unknown Title";
            }

            string? posterUrl = null;
            if (node.TryGetProperty("primaryImage", out var imgEl) && imgEl.ValueKind == JsonValueKind.Object && imgEl.TryGetProperty("url", out var urlProp))
            {
                posterUrl = urlProp.GetString();
            }

            int? year = null;
            if (node.TryGetProperty("releaseYear", out var yearEl) && yearEl.ValueKind == JsonValueKind.Object && yearEl.TryGetProperty("year", out var yearProp)
                && yearProp.ValueKind == JsonValueKind.Number && yearProp.TryGetInt32(out var parsedYear))
            {
                year = parsedYear;
            }

            var typeId = "movie";
            if (node.TryGetProperty("titleType", out var typeEl) && typeEl.ValueKind == JsonValueKind.Object && typeEl.TryGetProperty("id", out var typeIdProp))
            {
                typeId = typeIdProp.GetString() ?? "movie";
            }

            var type = (typeId == "tvSeries" || typeId == "tvMiniSeries") ? "Series" : "Movie";

            items.Add(new CustomRowItem
            {
                Name = title,
                Type = type,
                ProductionYear = year,
                Rank = rank++,
                PosterUrl = posterUrl,
                BackdropUrl = posterUrl,
                ProviderIds = new CustomRowItemProviderIds
                {
                    Imdb = id
                }
            });
        }

        return items;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerStartup
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}
