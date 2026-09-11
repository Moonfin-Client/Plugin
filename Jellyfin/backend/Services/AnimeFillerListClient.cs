using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fetches and caches the animefillerlist.com catalogue and individual show pages, and parses
/// them into a form that can be used to mark episodes as filler, recap or canon.
/// </summary>
public class AnimeFillerListClient
{
    private const string SiteBase = "https://www.animefillerlist.com";
    private const string CatalogFileName = "anime_filler_catalog.json";

    /// <summary>
    /// The site's robots.txt asks for 10 seconds between requests.
    /// </summary>
    public static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(10);

    /// <summary>A show's filler list never changes.</summary>
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(14);

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeFillerListClient> _logger;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly string _catalogPath;

    private List<AnimeFillerShow>? _shows;
    private Dictionary<string, AnimeFillerShow>? _index;

    public AnimeFillerListClient(IHttpClientFactory httpClientFactory, ILogger<AnimeFillerListClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _catalogPath = Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), CatalogFileName);
    }

    /// <summary>True once the show index is in memory and matching can be attempted.</summary>
    public bool CatalogLoaded => _index != null;

    /// <summary>How many shows the index holds.</summary>
    public int CatalogCount => _shows?.Count ?? 0;

    /// <summary>When the catalogue on disk was last written, or null if there isn't one.</summary>
    public DateTimeOffset? CatalogDownloadedAt =>
        File.Exists(_catalogPath) ? File.GetLastWriteTimeUtc(_catalogPath) : null;

    /// <summary>
    /// The title-to-show lookup. Empty until <see cref="EnsureCatalogAsync"/> succeeds.
    /// </summary>
    public IReadOnlyDictionary<string, AnimeFillerShow> Index =>
        _index ?? new Dictionary<string, AnimeFillerShow>(StringComparer.Ordinal);

    /// <summary>
    /// Loads the show index, downloading it only when the cached copy is missing or stale.
    /// A failed download keeps whatever is already on disk rather than clearing it.
    /// </summary>
    public async Task EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        if (_index != null && !IsCatalogStale())
        {
            return;
        }

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index != null && !IsCatalogStale())
            {
                return;
            }

            if (IsCatalogStale())
            {
                var downloaded = await DownloadCatalogAsync(cancellationToken).ConfigureAwait(false);
                if (downloaded != null)
                {
                    Adopt(downloaded);
                    await SaveCatalogAsync(downloaded, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Anime marker catalogue refreshed ({Count} shows)", downloaded.Count);
                    return;
                }
            }

            if (_index == null)
            {
                var fromDisk = LoadCatalogFromDisk();
                if (fromDisk != null)
                {
                    Adopt(fromDisk);
                    _logger.LogInformation("Anime marker catalogue loaded from disk ({Count} shows)", fromDisk.Count);
                }
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private void Adopt(List<AnimeFillerShow> shows)
    {
        _shows = shows;
        _index = AnimeTitleMatcher.BuildIndex(shows);
    }

    private bool IsCatalogStale()
    {
        if (!File.Exists(_catalogPath))
        {
            return true;
        }

        return DateTime.UtcNow - File.GetLastWriteTimeUtc(_catalogPath) > CatalogMaxAge;
    }

    private List<AnimeFillerShow>? LoadCatalogFromDisk()
    {
        try
        {
            if (!File.Exists(_catalogPath))
            {
                return null;
            }

            using var stream = File.OpenRead(_catalogPath);
            return JsonSerializer.Deserialize<List<AnimeFillerShow>>(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anime marker catalogue on disk could not be read, it will be downloaded again");
            return null;
        }
    }

    private async Task SaveCatalogAsync(List<AnimeFillerShow> shows, CancellationToken cancellationToken)
    {
        try
        {
            var tempPath = _catalogPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, shows, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _catalogPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anime marker catalogue could not be written to disk");
        }
    }

    private async Task<List<AnimeFillerShow>?> DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        var html = await GetStringAsync(SiteBase + "/shows", cancellationToken).ConfigureAwait(false);
        if (html == null)
        {
            return null;
        }

        var shows = AnimeFillerListParser.ParseShowIndex(html);

        // An index that parses to almost nothing means the markup moved, not that the site
        // lost its catalogue.
        if (shows.Count < 50)
        {
            _logger.LogWarning(
                "Anime marker catalogue looked wrong ({Count} shows parsed), keeping any existing copy", shows.Count);
            return null;
        }

        return shows;
    }

    /// <summary>
    /// Fetches one show's episode table. Returns null when the page could not be read, and
    /// an empty list only when the page lists no episodes.
    /// </summary>
    public async Task<List<AnimeMarkerEpisode>?> FetchEpisodesAsync(string slug, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync(SiteBase + "/shows/" + slug, cancellationToken).ConfigureAwait(false);
        return html == null ? null : AnimeFillerListParser.ParseEpisodes(html);
    }

    /// <summary>
    /// Fetches a page and returns its HTML, or null if the page does not exist or could not be read.
    /// </summary>
    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        await WaitForSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0 (+https://github.com/Moonfin-Client/Plugin)");

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Anime marker page {Url} does not exist", url);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Anime marker page {Url} returned {Status}", url, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Anime marker page {Url} could not be fetched", url);
            return null;
        }
    }

    /// <summary>
    /// Waits until the next request is allowed by the site's robots.txt Crawl-delay, and updates
    /// the last-request timestamp.
    /// </summary>
    private static async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
            if (sinceLast < CrawlDelay)
            {
                await Task.Delay(CrawlDelay - sinceLast, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }
}
