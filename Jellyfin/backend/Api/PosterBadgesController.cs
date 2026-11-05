using System.Collections;
using System.Reflection;
using System.Net.Mime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.PosterBadges;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Serves the badges the admin's rules give each item, for clients to draw on posters.
/// </summary>
[ApiController]
[Route("Moonfin/PosterBadges")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class PosterBadgesController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<PosterBadgesController> _logger;

    public PosterBadgesController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILogger<PosterBadgesController> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <summary>
    /// The badges for up to 200 items, keyed by the id the client sent. Items with no
    /// badge are left out. The profile is the client's device, "tv", "desktop" or "mobile".
    /// </summary>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetItemBadges([FromQuery] string? ids = null, [FromQuery] string? profile = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var defaults = config?.PosterBadgeDefaults ?? new PosterBadgeDefaults();
        var rules = PosterBadgeMatcher.ForViewer(config?.PosterBadgeRules, this.GetUserIdFromClaims()?.ToString(), profile);
        var version = PosterBadgeMatcher.Version(config?.PosterBadgeRules, defaults);
        if (rules.Count == 0)
        {
            // Lets a client stop asking while this viewer has no rules.
            return Ok(new { enabled = false, version, items = new Dictionary<string, object>() });
        }

        // No ids asks only whether this viewer gets any label, so a client can hide a
        // setting that would do nothing.
        if (string.IsNullOrWhiteSpace(ids))
        {
            return Ok(new { enabled = true, version, maxPerCorner = Math.Max(1, defaults.MaxPerCorner), items = new Dictionary<string, object>() });
        }

        // Capped so one request cant be turned into an unbounded amount of work.
        var requested = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(200);

        var answer = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in requested)
        {
            if (!Guid.TryParse(raw, out var itemGuid))
            {
                continue;
            }

            // A series, season or collection has no file of its own to read.
            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null || item is Folder)
            {
                continue;
            }

            try
            {
                var file = FileFor(item);
                var badges = PosterBadgeMatcher.Match(rules, file.Path, file.Streams, defaults);

                if (badges.Count > 0)
                {
                    answer[raw] = badges.Select(b => b.ToJson());
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Poster badges: tracks unreadable for {Item}", item.Id);
            }
        }

        return Ok(new { enabled = true, version, maxPerCorner = Math.Max(1, defaults.MaxPerCorner), items = answer });
    }

    /// <summary>
    /// Tries the rules the admin page holds, saved or not, on one movie or episode: which
    /// labels show, why the others dont, and the track values the conditions read.
    /// </summary>
    [HttpPost("Test")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> Test([FromBody] PosterBadgeTestRequest request)
    {
        if (request == null || !Guid.TryParse(request.ItemId, out var itemGuid))
        {
            return BadRequest(new { error = "Pick a movie or an episode." });
        }

        // A series, season or collection has no file of its own to read.
        var item = _libraryManager.GetItemById(itemGuid);
        if (item == null || item is Folder)
        {
            return BadRequest(new { error = "Pick a movie or an episode." });
        }

        var rules = request.Rules ?? new List<PosterBadgeRule>();
        var defaults = request.Defaults ?? new PosterBadgeDefaults();
        var file = FileFor(item);

        return Ok(new
        {
            name = item.Name,
            path = file.Path,
            tracks = PosterBadgeTest.DescribeTracks(file.Streams),
            results = PosterBadgeTest.Explain(rules, file.Path, file.Streams).Select(r => new { index = r.Index, outcome = r.Outcome, conditions = r.Conditions }),
            badges = PosterBadgeMatcher.Match(rules, file.Path, file.Streams, defaults).Select(b => b.ToJson()),
            maxPerCorner = Math.Max(1, defaults.MaxPerCorner),
        });
    }

    /// <summary>
    /// GetStaticMediaSources takes a User whose type moved between 10.10 and 10.11, so it is
    /// looked up by name rather than called directly.
    /// </summary>
    private static readonly Lazy<MethodInfo?> StaticSources = new(() => typeof(IMediaSourceManager)
        .GetMethods()
        .FirstOrDefault(m => m.Name == "GetStaticMediaSources" && m.GetParameters().Length == 3));

    /// <summary>
    /// The path and tracks labels are read from: the version with the largest picture when the
    /// movie is stored several times, else the item's own file.
    /// </summary>
    private (string? Path, IEnumerable? Streams) FileFor(BaseItem item)
    {
        try
        {
            var sources = StaticSources.Value?.Invoke(_mediaSourceManager, new object?[] { item, false, null }) as IEnumerable;
            var picked = PosterBadgeVersions.Pick(sources);
            if (picked != null)
            {
                return picked.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Poster badges: versions unreadable for {Item}", item.Id);
        }

        return (item.Path, AnimeMediaStreamReader.GetStreams(_mediaSourceManager, item));
    }

    /// <summary>The built-in labels, for the admin page to list.</summary>
    [HttpGet("Presets")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetPresets() => Ok(PosterBadgePresets.All.Select(p => new
    {
        id = p.Id,
        label = p.Label,
        group = p.Group,
        color = p.Color,
        description = p.Description,
    }));
}
