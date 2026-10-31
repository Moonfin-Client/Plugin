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
    /// badge are left out.
    /// </summary>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> GetItemBadges([FromQuery] string ids)
    {
        var rules = MoonfinPlugin.Instance?.Configuration?.PosterBadgeRules;
        if (rules == null || rules.Count == 0)
        {
            // Lets a client stop asking while the admin has no rules.
            return Ok(new { enabled = false, items = new Dictionary<string, object>() });
        }

        if (string.IsNullOrWhiteSpace(ids))
        {
            return BadRequest(new { error = "Missing ids" });
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
                var badges = PosterBadgeMatcher.Match(
                    rules,
                    item.Path,
                    AnimeMediaStreamReader.GetStreams(_mediaSourceManager, item));

                if (badges.Count > 0)
                {
                    answer[raw] = badges.Select(b => new { label = b.Label, color = b.Color });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Poster badges: tracks unreadable for {Item}", item.Id);
            }
        }

        return Ok(new { enabled = true, items = answer });
    }
}
