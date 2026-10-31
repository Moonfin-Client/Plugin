using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public PosterBadgesService(IApplicationHost appHost)
        {
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
            _libraryManager = appHost.Resolve<ILibraryManager>();
            _mediaSourceManager = appHost.Resolve<IMediaSourceManager>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("MoonfinPosterBadges");
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        /// <summary>
        /// The badges for up to 200 items, keyed by the id the client sent. Items with no
        /// badge are left out.
        /// </summary>
        public object Get(GetPosterBadgesRequest request)
        {
            var rules = Plugin.Instance?.Configuration?.PosterBadgeRules;
            if (rules == null || rules.Count == 0)
            {
                // Lets a client stop asking while the admin has no rules.
                return Json(new { enabled = false, items = new Dictionary<string, object>() });
            }

            if (string.IsNullOrWhiteSpace(request.Ids))
            {
                return Json(400, new { error = "Missing ids" });
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
                    var badges = PosterBadgeMatcher.Match(rules, item.Path, _mediaSourceManager.GetMediaStreams(item));
                    if (badges.Count > 0)
                    {
                        answer[raw] = badges.Select(b => new { label = b.Label, color = b.Color });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Poster badges: tracks unreadable for " + item.InternalId + ": " + ex.Message);
                }
            }

            return Json(new { enabled = true, items = answer });
        }

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
