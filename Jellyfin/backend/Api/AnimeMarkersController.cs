using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Serves filler, canon and recap markers for anime episodes.
/// </summary>
[ApiController]
[Route("Moonfin/AnimeMarkers")]
public class AnimeMarkersController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly AnimeFillerListClient _client;
    private readonly AnimeMarkerResolver _resolver;
    private readonly AnimeMarkerCacheService _cache;
    private readonly AnimeMarkerDiagnosticLog _diagnostics;
    private readonly ILogger<AnimeMarkersController> _logger;

    public AnimeMarkersController(
        ILibraryManager libraryManager,
        AnimeFillerListClient client,
        AnimeMarkerResolver resolver,
        AnimeMarkerCacheService cache,
        AnimeMarkerDiagnosticLog diagnostics,
        ILogger<AnimeMarkersController> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _resolver = resolver;
        _cache = cache;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>
    /// Whether a marker kind is enabled in the plugin configuration.
    /// </summary>
    private static bool IsKindEnabled(AnimeEpisodeKind kind)
    {
        var configuration = MoonfinPlugin.Instance?.Configuration;

        return kind switch
        {
            AnimeEpisodeKind.Filler => configuration?.AnimeMarkerShowFiller != false,
            AnimeEpisodeKind.Mixed => configuration?.AnimeMarkerShowMixed != false,
            AnimeEpisodeKind.MangaCanon => configuration?.AnimeMarkerShowMangaCanon == true,
            AnimeEpisodeKind.AnimeCanon => configuration?.AnimeMarkerShowAnimeCanon == true,
            _ => false
        };
    }

    /// <summary>
    /// Applies the admin's dual-audio preference at the edge, so the classifier keeps
    /// reporting what a file actually holds regardless of how it is labelled.
    /// </summary>
    private static AnimeAudioKind Present(AnimeAudioKind kind) =>
        AnimeAudioClassifier.Collapse(
            kind,
            MoonfinPlugin.Instance?.Configuration?.AnimeAudioSeparateDualAudio == true);

    /// <summary>
    /// Where clients should draw the pills. Passed through rather than interpreted, so a
    /// client that does not know a placement can fall back to its own default.
    /// </summary>
    private static string Placement =>
        MoonfinPlugin.Instance?.Configuration?.AnimeMarkerPlacement is { Length: > 0 } value
            ? value
            : "below";

    private static bool RecapEnabled =>
        MoonfinPlugin.Instance?.Configuration?.AnimeMarkerShowRecap != false;

    private static TimeSpan CacheMaxAge =>
        TimeSpan.FromDays(Math.Max(1, MoonfinPlugin.Instance?.Configuration?.AnimeMarkerMaxAgeDays ?? 30));

    /// <summary>
    /// Markers for the episodes of one series.
    ///
    /// Reads the cache only and never touches the network, so a client can call it on every
    /// episode-list render without inheriting an upstream site's latency. A series whose
    /// data has not been fetched yet answers with <c>pending: true</c>.
    ///
    /// Answers with <c>enabled: false</c> rather than an error when the feature is off, so
    /// a client can call it all the time and simply render nothing.
    /// </summary>
    [HttpGet("Series")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetSeriesMarkers(
        [FromQuery] string seriesId,
        CancellationToken cancellationToken = default)
    {
        _diagnostics.Write($"request  seriesId={seriesId} from={Request.Headers.UserAgent}");

        var configuration = MoonfinPlugin.Instance?.Configuration;
        var fillerEnabled = configuration?.AnimeMarkersEnabled == true;
        var audioEnabled = configuration?.AnimeAudioMarkersEnabled == true;

        // The client can call this endpoint on every episode list render, so it is not an error
        // when the feature is off: the client simply renders nothing. The admin page can still
        // call it to see what the feature would do if it were on, so the endpoint.
        if (!fillerEnabled && !audioEnabled)
        {
            _diagnostics.Write("  -> answered enabled=false (both marker features are off in settings)");
            return Ok(new
            {
                enabled = false,
                matched = false,
                episodes = new Dictionary<string, object>(),
                seasons = new Dictionary<string, object>()
            });
        }

        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            _diagnostics.Write("  -> 400, the seriesId was missing or malformed");
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            _diagnostics.Write("  -> 404, no series in the library has that id");
            return NotFound(new { error = "Series not found" });
        }

        // Loads from disk on the first call after a restart.
        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        _diagnostics.Write(
            $"  series=\"{series.Name}\" filler={fillerEnabled} audio={audioEnabled}");

        var result = new SeriesMarkerResult();
        if (fillerEnabled)
        {
            try
            {
                result = _resolver.GetMarkersForSeries(series, CacheMaxAge);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anime markers: filler lookup failed for {Series}", series.Name);
                _diagnostics.Write($"  !! filler lookup threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var audio = new AudioMarkerResult();
        if (audioEnabled)
        {
            try
            {
                if (_resolver.IsAudioMarkerCandidate(series))
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    audio = _resolver.BuildAudioMarkers(series);
                    _diagnostics.Write($"  audio pass took {started.ElapsedMilliseconds} ms");
                }
                else
                {
                    _diagnostics.Write("  audio skipped: this series is not in a selected library and does not look like anime");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anime markers: audio lookup failed for {Series}", series.Name);
                _diagnostics.Write($"  !! audio lookup threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_diagnostics is { } log && AnimeMarkerDiagnosticLog.Enabled)
        {
            log.Write(
                $"  -> matched={result.Slug ?? "(nothing)"} pending={result.Pending} " +
                $"markers={result.Episodes.Count} recapKnown={result.RecapKnown} " +
                $"audioEpisodes={audio.Episodes.Count} audioSeasons={audio.Seasons.Count}");

            // The episode ids are the join the client has to match on, so a couple are
            // written out verbatim: a client that cannot find them is formatting ids
            // differently, which looks identical to having no data at all.
            foreach (var (episodeId, marker) in result.Episodes.Take(3))
            {
                log.Write($"    sample episodeId={episodeId} kind={marker.Kind} recap={marker.Recap}");
            }
        }

        return Ok(new
        {
            enabled = true,
            matched = result.Slug != null,
            slug = result.Slug,
            title = result.Title,
            pending = result.Pending,
            recapKnown = result.RecapKnown,

            // The episode list is flattened to one row per episode, so a client can render it without
            // having to know how many files are in each episode. The client can still show the
            // per-file badge if it wants.
            episodes = result.Episodes.Keys
                .Concat(audio.Episodes.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    episodeId => episodeId,
                    episodeId =>
                    {
                        result.Episodes.TryGetValue(episodeId, out var marker);
                        var hasAudio = audio.Episodes.TryGetValue(episodeId, out var audioKind);

                        var kind = marker != null && IsKindEnabled(marker.Kind)
                            ? marker.Kind
                            : (AnimeEpisodeKind?)null;

                        var recap = marker?.Recap == true && RecapEnabled;

                        return new
                        {
                            kind,
                            filler = kind == AnimeEpisodeKind.Filler,
                            recap,
                            audio = hasAudio ? Present(audioKind) : (AnimeAudioKind?)null
                        };
                    },
                    StringComparer.OrdinalIgnoreCase),

            seasons = audio.Seasons.ToDictionary(
                pair => pair.Key,
                pair => new { audio = Present(pair.Value) }),

            placement = Placement
        });
    }

    /// <summary>
    /// What the feature currently knows: how many series matched a show, how many are
    /// waiting on a fetch, and which matched nothing. Intended for the admin page and for
    /// pasting into a bug report.
    /// </summary>
    [HttpGet("Diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetDiagnostics(CancellationToken cancellationToken)
    {
        var configuration = MoonfinPlugin.Instance?.Configuration;

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        if (!_client.CatalogLoaded)
        {
            return Ok(new
            {
                enabled = configuration?.AnimeMarkersEnabled ?? false,
                catalogLoaded = false,
                error = "The AnimeFillerList show catalogue could not be downloaded. Check that the server can reach animefillerlist.com."
            });
        }

        var candidates = _resolver.GetCandidateSeries();
        var fresh = _cache.GetFreshSlugs(CacheMaxAge);

        var matched = new List<object>();
        var unmatched = new List<string>();
        var pendingShows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var series in candidates)
        {
            var show = _resolver.MatchSeries(series);
            if (show == null)
            {
                unmatched.Add(series.Name ?? series.Id.ToString("N"));
                continue;
            }

            var cached = fresh.Contains(show.Slug);
            if (!cached)
            {
                pendingShows.Add(show.Slug);
            }

            matched.Add(new { series = series.Name, slug = show.Slug, title = show.Title, cached });
        }

        var configuredLibraries = configuration?.AnimeMarkerLibraryIds ?? new List<string>();
        var resolvedLibraries = _resolver.GetSelectedLibraryFolderIds();

        return Ok(new
        {
            enabled = configuration?.AnimeMarkersEnabled ?? false,
            catalogLoaded = true,
            catalogShows = _client.CatalogCount,
            catalogDownloadedAt = _client.CatalogDownloadedAt,
            libraryFilterActive = resolvedLibraries != null,
            configuredLibraryCount = configuredLibraries.Count,
            resolvedLibraryCount = resolvedLibraries?.Count ?? 0,
            seriesConsidered = candidates.Count,
            seriesMatched = matched.Count,
            showsPending = pendingShows.Count,
            showsCached = _cache.EntryCount(),
            episodesClassified = _cache.TotalEpisodeCount(),
            episodesFlagged = _cache.FlaggedEpisodeCount(),

            // Capped so a large library cannot turn the admin page into a wall of text.
            matches = matched.Take(200).ToList(),
            unmatchedCount = unmatched.Count,
            unmatched = unmatched.Take(50).ToList()
        });
    }

    /// <summary>
    /// Markers for the audio tracks of one or more items, 
    /// including subbed/dubbed classification.
    /// </summary>
    [HttpGet("Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> GetItemMarkers([FromQuery] string ids)
    {
        var configuration = MoonfinPlugin.Instance?.Configuration;
        if (configuration?.AnimeAudioMarkersEnabled != true)
        {
            return Ok(new { enabled = false, items = new Dictionary<string, object>() });
        }

        if (string.IsNullOrWhiteSpace(ids))
        {
            return BadRequest(new { error = "Missing ids" });
        }

        // Capped so one request cannot be turned into an unbounded amount of work.
        var requested = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(200)
            .ToList();

        var moviesEnabled = configuration.AnimeAudioMarkersMovies;
        var answer = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // The client can call this endpoint on every episode list render, so it is not an error
        var notFound = 0;
        var moviesSkipped = 0;
        var notCandidate = 0;
        var noAudioTags = 0;
        var seriesOrFolder = 0;

        foreach (var raw in requested)
        {
            if (!Guid.TryParse(raw, out var itemGuid))
            {
                notFound++;
                continue;
            }

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                notFound++;
                continue;
            }

            // A series or a folder owns no file of its own, so it has no audio to read. Its
            // episodes and seasons are answered through the Series endpoint instead.
            if (item is Series || item is MediaBrowser.Controller.Entities.Folder)
            {
                seriesOrFolder++;
                continue;
            }

            // Movies are opted in separately, because they are where a mixed library is most
            // likely to put a pill on something that is not anime.
            if (item is MediaBrowser.Controller.Entities.Movies.Movie && !moviesEnabled)
            {
                moviesSkipped++;
                continue;
            }

            if (!_resolver.IsAudioMarkerCandidateItem(item))
            {
                notCandidate++;
                continue;
            }

            var audio = _resolver.GetAudioForItem(item);
            if (audio == null)
            {
                noAudioTags++;
                continue;
            }

            answer[item.Id.ToString("N")] = new { audio = Present(audio.Value) };
        }

        _diagnostics.Write(
            $"items  asked about {requested.Count}, answered {answer.Count} " +
            $"(skipped: {seriesOrFolder} series/folders, {moviesSkipped} movies with the movie option off, " +
            $"{notCandidate} outside the chosen libraries or not anime, {noAudioTags} with no audio language tags, " +
            $"{notFound} unknown ids)");

        return Ok(new { enabled = true, items = answer, placement = Placement });
    }

    /// <summary>
    /// What the feature currently knows about one series, including the episode list and
    /// which episodes are marked as filler, recap or canon.
    /// </summary>
    [HttpGet("Preview")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetPreview(
        [FromQuery] string seriesId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            return NotFound(new { error = "Series not found" });
        }

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        _diagnostics.Write($"preview  series=\"{series.Name}\" (from the admin page)");

        var show = _resolver.MatchSeries(series);
        if (show == null)
        {
            _diagnostics.Write("  -> matched nothing on AnimeFillerList");
            return Ok(new
            {
                series = series.Name,
                matched = false,
                hint = "No AnimeFillerList show has this title. Check the site's spelling for it."
            });
        }

        var entry = _resolver.GetCachedEntry(series, CacheMaxAge);
        if (entry == null)
        {
            return Ok(new
            {
                series = series.Name,
                matched = true,
                slug = show.Slug,
                title = show.Title,
                pending = true,
                hint = "Matched, but not fetched yet. Use Fetch now, or run the Anime Markers Sync task."
            });
        }

        var markersByNumber = entry.Episodes.ToDictionary(episode => episode.Number);
        var numbering = _resolver.BuildNumbering(series);

        // The episode list is flattened to one row per episode, so a client can render it without
        // having to know how many files are in each episode. The client can still show the
        // per-file badge if it wants, but the admin page is more interested in the episode
        // list and the badge's presence there is what the user will notice first.
        var rows = numbering.Episodes
            .GroupBy(numbered => (numbered.Episode.ParentIndexNumber, numbered.Episode.IndexNumber))
            .Select(group =>
            {
                var numbered = group.First();
                markersByNumber.TryGetValue(numbered.AbsoluteNumber, out var marker);

                return new
                {
                    season = numbered.Episode.ParentIndexNumber,
                    index = numbered.Episode.IndexNumber,
                    absolute = numbered.AbsoluteNumber,
                    name = numbered.Episode.Name,
                    kind = marker?.Kind.ToString(),
                    recap = marker?.Recap ?? false,
                    marked = marker != null,
                    copies = group.Count()
                };
            })
            .OrderBy(row => row.season)
            .ThenBy(row => row.index)
            .ToList();

        return Ok(new
        {
            series = series.Name,
            matched = true,
            slug = show.Slug,
            title = show.Title,
            pending = false,
            recapKnown = entry.RecapMalId != null,
            numbering = numbering.Mode,
            showEpisodes = entry.Episodes.Count,
            libraryEpisodes = rows.Count,
            libraryFiles = numbering.Episodes.Count,
            markedEpisodes = rows.Count(row => row.marked),
            fillerEpisodes = rows.Count(row => row.kind == nameof(AnimeEpisodeKind.Filler)),
            episodes = rows
        });
    }

    /// <summary>
    /// Fetches one series' show page immediately instead of waiting for the nightly task.
    /// </summary>
    [HttpPost("Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> RefreshSeries(
        [FromQuery] string seriesId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || !Guid.TryParse(seriesId, out var seriesGuid))
        {
            return BadRequest(new { error = "Missing or malformed seriesId" });
        }

        if (_libraryManager.GetItemById(seriesGuid) is not Series series)
        {
            return NotFound(new { error = "Series not found" });
        }

        await _client.EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);

        var show = _resolver.MatchSeries(series);
        if (show == null)
        {
            return Ok(new { matched = false, series = series.Name });
        }

        var episodes = await _client.FetchEpisodesAsync(show.Slug, cancellationToken).ConfigureAwait(false);
        if (episodes == null || episodes.Count == 0)
        {
            _logger.LogWarning("Anime markers: refresh of {Slug} returned nothing usable", show.Slug);
            return Ok(new { matched = true, slug = show.Slug, fetched = false });
        }

        _cache.Set(show.Slug, new AnimeMarkerCacheEntry
        {
            Slug = show.Slug,
            Title = show.Title,
            Episodes = episodes
        });

        await _cache.FlushAsync().ConfigureAwait(false);

        return Ok(new { matched = true, slug = show.Slug, fetched = true, episodes = episodes.Count });
    }

    /// <summary>
    /// The tail of the dedicated marker log, so a reproduction can be read straight from
    /// the admin page instead of hunting through the server log.
    /// </summary>
    [HttpGet("Log")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetLog([FromQuery] int lines = 200)
    {
        var clamped = Math.Clamp(lines, 1, 2000);

        return Ok(new
        {
            enabled = AnimeMarkerDiagnosticLog.Enabled,
            path = _diagnostics.Path_,
            sizeBytes = _diagnostics.SizeBytes,
            lines = _diagnostics.Tail(clamped)
        });
    }

    /// <summary>Empties the marker log so a fresh reproduction starts clean.</summary>
    [HttpPost("ClearLog")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> ClearLog()
    {
        var cleared = await _diagnostics.ClearAsync().ConfigureAwait(false);
        return Ok(new { cleared });
    }

    /// <summary>
    /// Drops every cached show table so the next sync refetches from scratch. The show
    /// catalogue is kept, since re-downloading it is a separate concern.
    /// </summary>
    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> ClearCache()
    {
        var removed = await _cache.ClearAsync().ConfigureAwait(false);
        return Ok(new { cleared = removed });
    }
}
