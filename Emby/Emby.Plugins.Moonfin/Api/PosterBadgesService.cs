using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Moonfin.PosterBadges;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Serves the badges the admin's rules give each item, for clients to draw on posters.
    /// </summary>
    public class PosterBadgesService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ILogger _logger;
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public PosterBadgesService(IApplicationHost appHost)
        {
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
            _libraryManager = appHost.Resolve<ILibraryManager>();
            _mediaSourceManager = appHost.Resolve<IMediaSourceManager>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("MoonfinPosterBadges");
            _authContext = appHost.Resolve<IAuthorizationContext>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);

        /// <summary>
        /// The badges for up to 200 items, keyed by the id the client sent. Items with no
        /// badge are left out.
        /// </summary>
        public object Get(GetPosterBadgesRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            var defaults = config?.PosterBadgeDefaults ?? new PosterBadgeDefaults();
            var viewer = AuthHelpers.GetCurrentUser(Request, _authContext);
            var rules = PosterBadgeMatcher.ForViewer(
                config?.PosterBadgeRules,
                viewer != null && viewer.Id != Guid.Empty ? viewer.Id.ToString() : null,
                request.Profile);
            var version = PosterBadgeMatcher.Version(config?.PosterBadgeRules, defaults);
            if (rules.Count == 0)
            {
                // Lets a client stop asking while this viewer has no rules.
                return Json(new { enabled = false, version, items = new Dictionary<string, object>() });
            }

            // No ids asks only whether this viewer gets any label, so a client can hide a
            // setting that would do nothing.
            if (string.IsNullOrWhiteSpace(request.Ids))
            {
                return Json(new { enabled = true, version, maxPerCorner = Math.Max(1, defaults.MaxPerCorner), items = new Dictionary<string, object>() });
            }

            // Capped so one request cant be turned into an unbounded amount of work.
            var requested = request.Ids
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .Take(200);

            var answer = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Labels come from the file, so a viewer only gets them for items they can see.
            // Without a user there is no telling, so nothing is answered.
            if (viewer == null)
            {
                return Json(new { enabled = true, version, maxPerCorner = Math.Max(1, defaults.MaxPerCorner), items = answer });
            }

            var clock = Stopwatch.StartNew();

            foreach (var raw in requested)
            {
                // A whole-request budget for admin patterns that backtrack badly. Items
                // past it get no labels this session; a per-item retry would need the client to ask again.
                if (clock.Elapsed > RequestBudget)
                {
                    _logger.Warn("Poster badges: stopped after " + RequestBudget.TotalSeconds + "s, a label pattern is too slow");
                    break;
                }

                // A collection has nothing to label. A series or season has no file, only a rating.
                var item = FindItem(raw);
                if (item == null || (item is Folder && !HasRatingOnly(item)) || !item.IsVisible(viewer))
                {
                    continue;
                }

                try
                {
                    var file = HasRatingOnly(item) ? (null, null) : FileFor(item);
                    // Unread tracks would pass every "no such track" rule, so an unread file counts as
                    // none: only labels from the rating can show.
                    if (file.Streams == null)
                    {
                        file = (null, null);
                    }

                    var badges = PosterBadgeMatcher.Match(rules, file.Path, file.Streams, defaults, RatingOf(item));
                    if (badges.Count > 0)
                    {
                        answer[raw] = badges.Select(b => b.ToJson());
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Poster badges: tracks unreadable for " + item.InternalId + ": " + ex.Message);
                }
            }

            return Json(new { enabled = true, version, maxPerCorner = Math.Max(1, defaults.MaxPerCorner), items = answer });
        }

        /// <summary>
        /// Tries the rules the admin page holds, saved or not, on one movie or episode: which
        /// labels show, why the others dont, and the track values the conditions read.
        /// </summary>
        public async Task<object> Post(TestPosterBadgesRequest request)
        {
            var body = await MoonfinJson.ReadBodyAsync<PosterBadgeTestRequest>(request.RequestStream).ConfigureAwait(false);

            // A collection has nothing to label.
            var item = body?.ItemId == null ? null : FindItem(body.ItemId);
            if (body == null || item == null || (item is Folder && !HasRatingOnly(item)))
            {
                Request.Response.StatusCode = 400;
                return Json(new { error = "Pick a movie, an episode or a series." });
            }

            var rules = body.Rules ?? new List<PosterBadgeRule>();
            var defaults = body.Defaults ?? new PosterBadgeDefaults();
            var file = HasRatingOnly(item) ? (null, null) : FileFor(item);
            var rating = RatingOf(item);

            return Json(new
            {
                name = item.Name,
                path = file.Path,
                rating = PosterBadgeRating.Clean(rating),
                age = PosterBadgeRating.AgeInText(rating),
                tracks = PosterBadgeTest.DescribeTracks(file.Streams),
                results = PosterBadgeTest.Explain(rules, file.Path, file.Streams, rating).Select(r => new { index = r.Index, outcome = r.Outcome, conditions = r.Conditions }),
                badges = PosterBadgeMatcher.Match(rules, file.Path, file.Streams, defaults, rating).Select(b => b.ToJson()),
                maxPerCorner = Math.Max(1, defaults.MaxPerCorner),
            });
        }

        /// <summary>A series or season: no file of its own, but a rating.</summary>
        private static bool HasRatingOnly(BaseItem item) => item is Series || item is Season;

        /// <summary>The item's own age rating, else its series'. Episodes and seasons seldom have one.</summary>
        private static string? RatingOf(BaseItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.OfficialRating))
            {
                return item.OfficialRating;
            }

            return item is Episode episode ? episode.Series?.OfficialRating
                : item is Season season ? season.Series?.OfficialRating
                : null;
        }

        /// <summary>How long one request for up to 200 items may spend matching.</summary>
        private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(3);

        /// <summary>
        /// The path and tracks labels are read from: the version with the largest picture when
        /// the movie is stored several times, else the item's own file.
        /// </summary>
        private (string? Path, System.Collections.IEnumerable? Streams) FileFor(BaseItem item)
        {
            try
            {
                var picked = PosterBadgeVersions.Pick(_mediaSourceManager.GetStaticMediaSources(item, false, false, null, null));
                if (picked != null)
                {
                    return picked.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Poster badges: versions unreadable for " + item.InternalId + ": " + ex.Message);
            }

            return (item.Path, _mediaSourceManager.GetMediaStreams(item));
        }

        /// <summary>The built-in labels, for the admin page to list.</summary>
        public object Get(GetPosterBadgePresetsRequest request) => Json(PosterBadgePresets.All.Select(p => new
        {
            id = p.Id,
            label = p.Label,
            group = p.Group,
            color = p.Color,
            description = p.Description,
        }));

        /// <summary>Emby clients send either the internal number or the guid.</summary>
        private BaseItem? FindItem(string id)
        {
            try
            {
                if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId))
                {
                    return internalId > 0 ? _libraryManager.GetItemById(internalId) : null;
                }

                return Guid.TryParse(id, out var guid) ? _libraryManager.GetItemById(guid) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
