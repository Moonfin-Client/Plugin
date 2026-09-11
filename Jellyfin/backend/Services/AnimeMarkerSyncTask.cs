using System.Diagnostics;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Scheduled task that fetches the AnimeFillerList catalogue and individual show pages, parses
/// them into a form that can be used to mark episodes as filler, recap or canon, 
/// and caches the results for the library to use.
/// </summary>
public class AnimeMarkerSyncTask : IScheduledTask
{
    public string Name => "Moonfin Anime Markers Sync";
    public string Key => "Moonfin.Anime.MarkerSync";
    public string Description =>
        "Fetches filler, canon and recap classifications for the anime in your library from AnimeFillerList. No account or API key is needed.";
    public string Category => "Moonfin";

    /// <summary>
    /// The maximum time the task is allowed to run before it is considered to have failed. 
    /// The task is scheduled daily, so this should be less than 24 hours.
    /// </summary>
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(45);

    private const int FlushEveryNShows = 10;

    /// <summary>
    /// Consecutive failed show fetches before giving up. The site being unreachable looks
    /// identical from every show, so there is nothing to gain by working through the rest.
    /// </summary>
    private const int MaxConsecutiveFailures = 5;

    private readonly AnimeFillerListClient _client;
    private readonly AnimeMarkerResolver _resolver;
    private readonly AnimeMarkerCacheService _cache;
    private readonly AnimeRecapFetchService _recapService;
    private readonly AnimeMarkerDiagnosticLog _diagnostics;
    private readonly ILogger<AnimeMarkerSyncTask> _logger;

    public AnimeMarkerSyncTask(
        AnimeFillerListClient client,
        AnimeMarkerResolver resolver,
        AnimeMarkerCacheService cache,
        AnimeRecapFetchService recapService,
        AnimeMarkerDiagnosticLog diagnostics,
        ILogger<AnimeMarkerSyncTask> logger)
    {
        _client = client;
        _resolver = resolver;
        _cache = cache;
        _recapService = recapService;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = MoonfinPlugin.Instance?.Configuration;
        if (configuration?.AnimeMarkersEnabled != true)
        {
            _logger.LogInformation("Anime markers sync skipped: disabled in configuration");
            return;
        }

        var clock = Stopwatch.StartNew();
        progress.Report(0);

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!_client.CatalogLoaded)
        {
            _logger.LogWarning(
                "Anime markers sync aborted: the show catalogue could not be loaded. Check that the server can reach animefillerlist.com");
            return;
        }

        progress.Report(5);

        var matches = _resolver.BuildMatches();
        var showsBySlug = matches
            .GroupBy(match => match.Show.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Show, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Anime markers sync: {Series} series matched {Shows} shows on AnimeFillerList",
            matches.Count, showsBySlug.Count);

        _diagnostics.Write(
            $"sync  run started: {matches.Count} series matched {showsBySlug.Count} shows, " +
            $"recapLookup={configuration.AnimeMarkerRecapLookup}");

        var maxAge = TimeSpan.FromDays(Math.Max(1, configuration.AnimeMarkerMaxAgeDays));
        var fresh = _cache.GetFreshSlugs(maxAge);
        var pending = showsBySlug.Keys.Where(slug => !fresh.Contains(slug)).ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("Anime markers sync: every matched show is already cached");
        }
        else
        {
            _logger.LogInformation(
                "Anime markers sync: fetching {Count} shows, about {Minutes} minutes at the site's requested crawl delay",
                pending.Count,
                Math.Max(1, (int)(pending.Count * AnimeFillerListClient.CrawlDelay.TotalMinutes)));
        }

        await FetchShowsAsync(pending, showsBySlug, clock, progress, cancellationToken).ConfigureAwait(false);

        if (configuration.AnimeMarkerRecapLookup)
        {
            await FetchRecapsAsync(matches, clock, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _diagnostics.Write("recap pass skipped: the recap lookup is switched off in settings");
        }

        await _cache.FlushAsync().ConfigureAwait(false);

        _logger.LogInformation(
            "Anime markers sync finished in {Elapsed}: {Shows} shows cached, {Episodes} episodes classified, {Flagged} of them filler or mixed",
            clock.Elapsed,
            _cache.EntryCount(),
            _cache.TotalEpisodeCount(),
            _cache.FlaggedEpisodeCount());

        progress.Report(100);
    }

    private async Task FetchShowsAsync(
        List<string> pending,
        Dictionary<string, AnimeFillerShow> showsBySlug,
        Stopwatch clock,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        var fetched = 0;
        var failed = 0;
        var consecutiveFailures = 0;
        var sinceFlush = 0;

        foreach (var slug in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (clock.Elapsed > RunBudget)
            {
                _logger.LogInformation(
                    "Anime markers sync: run budget reached with {Remaining} shows left, they will be fetched on the next run",
                    pending.Count - processed);
                break;
            }

            var episodes = await _client.FetchEpisodesAsync(slug, cancellationToken).ConfigureAwait(false);
            processed++;

            _diagnostics.Write(
                episodes == null
                    ? $"sync  fetch {slug} -> the page could not be read"
                    : $"sync  fetch {slug} -> {episodes.Count} episodes");

            progress.Report(5 + ((double)processed / pending.Count * 75));

            if (episodes == null)
            {
                failed++;
                consecutiveFailures++;

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "Anime markers sync stopped early: {Count} shows failed in a row, so the site is unreachable rather than the shows being missing. {Fetched} were cached first",
                        consecutiveFailures, fetched);
                    break;
                }

                continue;
            }

            consecutiveFailures = 0;

            if (episodes.Count == 0)
            {
                _logger.LogDebug("Anime markers: {Slug} listed no episodes, not caching it", slug);
                continue;
            }

            var show = showsBySlug[slug];
            _cache.Set(slug, new AnimeMarkerCacheEntry
            {
                Slug = slug,
                Title = show.Title,
                Episodes = episodes
            });

            fetched++;
            sinceFlush++;

            if (sinceFlush >= FlushEveryNShows)
            {
                await _cache.FlushAsync().ConfigureAwait(false);
                sinceFlush = 0;

                _logger.LogInformation(
                    "Anime markers sync progress: {Processed}/{Total} shows, {Fetched} cached, {Failed} unreadable",
                    processed, pending.Count, fetched, failed);
            }
        }

        progress.Report(80);
    }

    /// <summary>
    /// Fetches the MyAnimeList recap flags for every show that matched a series and has a cached table,
    /// but has not yet had its recap flags fetched.
    /// </summary>
    private async Task FetchRecapsAsync(List<SeriesMatch> matches, Stopwatch clock, CancellationToken cancellationToken)
    {
        var updated = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (clock.Elapsed > RunBudget || !seen.Add(match.Show.Slug))
            {
                continue;
            }

            var entry = _cache.TryGetAny(match.Show.Slug);
            if (entry == null)
            {
                _diagnostics.Write($"recap {match.Show.Slug}: skipped, the show has no cached table yet");
                continue;
            }

            if (entry.RecapMalId != null)
            {
                _diagnostics.Write($"recap {match.Show.Slug}: already done from MAL {entry.RecapMalId}");
                continue;
            }

            var malId = await _recapService.TryResolveMalIdAsync(match.Series, cancellationToken).ConfigureAwait(false);
            if (malId == null)
            {
                _diagnostics.Write(
                    $"recap {match.Show.Slug}: no MyAnimeList id for \"{match.Series.Name}\" from its "
                    + $"provider ids ({string.Join(", ", match.Series.ProviderIds.Select(pair => pair.Key + "=" + pair.Value))}), "
                    + "the offline mapping table, or the AniList search");
                continue;
            }

            _diagnostics.Write($"recap {match.Show.Slug}: resolved to MAL {malId}, asking the episode API");

            var lookup = await _recapService.FetchRecapsAsync(malId.Value, cancellationToken).ConfigureAwait(false);
            if (lookup == null)
            {
                _diagnostics.Write(
                    $"recap {match.Show.Slug}: the episode API would not answer for MAL {malId} after retries, leaving it for the next run");
                continue;
            }

            _diagnostics.Write(
                $"recap {match.Show.Slug}: MAL {malId} covers {lookup.Value.EpisodeCount} episodes, " +
                $"{lookup.Value.RecapNumbers.Count} of them recaps");

            foreach (var episode in entry.Episodes)
            {
                if (episode.Number <= lookup.Value.EpisodeCount)
                {
                    episode.Recap = lookup.Value.RecapNumbers.Contains(episode.Number);
                }
            }

            entry.RecapMalId = malId;
            _cache.Set(match.Show.Slug, entry);
            updated++;
        }

        if (updated > 0)
        {
            _logger.LogInformation("Anime markers sync: recap flags added for {Count} shows", updated);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily only, and never at startup. See the class remarks.
        yield return TaskTriggers.Daily(TimeSpan.FromHours(4));
    }
}
