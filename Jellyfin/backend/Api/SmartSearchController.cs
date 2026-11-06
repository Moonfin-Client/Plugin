using System.Net.Mime;
using System.Text.Json.Nodes;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Helpers;
using Moonfin.Server.Services;
using Moonfin.SmartSearch;

namespace Moonfin.Server.Api;

/// <summary>Plain sentence search over the library and, through Seerr, titles the library lacks.</summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class SmartSearchController : ControllerBase
{
    private const int MaxLimit = 50;
    private const int Candidates = 120;
    private const int MaxQueryLength = 200;

    private readonly SmartSearchService _service;
    private readonly ItemCardDtoMapper _cardMapper;
    private readonly ServerRatingLimitService _ratingLimits;
    private readonly TmdbRatingService _ratings;
    private readonly MoonfinSettingsService _settings;
    private readonly ITaskManager _taskManager;

    public SmartSearchController(
        SmartSearchService service,
        ItemCardDtoMapper cardMapper,
        ServerRatingLimitService ratingLimits,
        TmdbRatingService ratings,
        MoonfinSettingsService settings,
        ITaskManager taskManager)
    {
        _service = service;
        _cardMapper = cardMapper;
        _ratingLimits = ratingLimits;
        _ratings = ratings;
        _settings = settings;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Library items and Seerr titles that match a plain sentence such as "90s movie about a kid
    /// pulled into a board game". Items use the trimmed card shape of the other Moonfin browse
    /// endpoints, Seerr titles the shape of Seerr's own search results.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="limit">Most results per list, 20 by default.</param>
    /// <param name="debug">Adds each result's score and the parsed query, for tuning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("SmartSearch")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] int? limit,
        [FromQuery] bool debug,
        CancellationToken cancellationToken)
    {
        if (!SmartSearchService.Enabled)
        {
            return NotFound();
        }

        var userId = this.GetUserIdFromClaims();
        var user = userId.HasValue ? UserReflection.ResolveUser(HttpContext.RequestServices, userId.Value) : null;
        if (user == null || userId == null)
        {
            return Unauthorized();
        }

        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return BadRequest();
        }

        if (text.Length > MaxQueryLength)
        {
            text = text.Substring(0, MaxQueryLength);
        }

        if (!_service.Engine.Ready)
        {
            // Turning the feature on doesn't restart the server, so the first search starts the build.
            _taskManager.QueueIfNotRunning<SmartSearchIndexTask>();
            return Ok(new { Ready = false, Items = Array.Empty<object>(), Seerr = Array.Empty<object>() });
        }

        var max = Math.Clamp(limit ?? 20, 1, MaxLimit);
        var started = DateTime.UtcNow;
        var results = _service.Engine.Search(text, Candidates);

        var items = new List<object>();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in results.Library)
        {
            var item = _service.Resolve(hit.Entry);
            // People sit outside every library, so only the titles they're credited on are access checked.
            if (item == null || (hit.Entry.Kind != SmartQuery.Person && !UserReflection.IsVisibleStandaloneTo(item, user)))
            {
                continue;
            }

            // The same show in two libraries would otherwise fill two cards with one title.
            if (!shown.Add($"{hit.Entry.Kind}|{item.Name}|{item.ProductionYear}"))
            {
                continue;
            }

            items.Add(debug ? new { Card = _cardMapper.Map(item, user), hit.Score, hit.Similarity } : _cardMapper.Map(item, user));
            if (items.Count >= max)
            {
                break;
            }
        }

        var seerr = new List<JsonNode>();
        if (MoonfinPlugin.Instance?.Configuration?.SeerrEnabled == true)
        {
            var allowed = await _ratings.SeerrVerdictForAsync(_ratingLimits, _settings, userId.Value, cancellationToken).ConfigureAwait(false);
            foreach (var hit in results.Catalog)
            {
                if (allowed != null && !await allowed(hit.Entry.Kind == SmartQuery.Movie ? "movie" : "tv", long.Parse(hit.Entry.TmdbId)).ConfigureAwait(false))
                {
                    continue;
                }

                var payload = JsonNode.Parse(hit.Entry.Payload)!;
                if (debug)
                {
                    payload["score"] = hit.Score;
                    payload["similarity"] = hit.Similarity;
                }

                seerr.Add(payload);
                if (seerr.Count >= max)
                {
                    break;
                }
            }
        }

        if (debug)
        {
            var q = results.Query;
            return Ok(new
            {
                Ready = true,
                Items = items,
                Seerr = seerr,
                Query = new { q.YearMin, q.YearMax, Kinds = q.Kinds.ToArray(), People = q.People.ToArray() },
                Milliseconds = (DateTime.UtcNow - started).TotalMilliseconds,
            });
        }

        return Ok(new { Ready = true, Items = items, Seerr = seerr });
    }
}
