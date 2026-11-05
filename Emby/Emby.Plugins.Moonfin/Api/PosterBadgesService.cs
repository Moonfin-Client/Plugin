using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
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
            var rules = PosterBadgeMatcher.ForViewer(
                config?.PosterBadgeRules,
                AuthHelpers.GetCurrentUserId(Request, _authContext)?.ToString(),
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

            foreach (var raw in requested)
            {
                // A series, season or collection has no file of its own to read.
                var item = FindItem(raw);
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

            // A series, season or collection has no file of its own to read.
            var item = body?.ItemId == null ? null : FindItem(body.ItemId);
            if (body == null || item == null || item is Folder)
            {
                Request.Response.StatusCode = 400;
                return Json(new { error = "Pick a movie or an episode." });
            }

            var rules = body.Rules ?? new List<PosterBadgeRule>();
            var defaults = body.Defaults ?? new PosterBadgeDefaults();
            var file = FileFor(item);

            return Json(new
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
