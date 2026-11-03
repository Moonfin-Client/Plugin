using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using Moonfin.SmartSearch;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>Plain sentence search over the library and, through Seerr, titles the library lacks.</summary>
    public class SmartSearchService : IService, IRequiresRequest, IHasResultFactory
    {
        private const int MaxLimit = 50;
        private const int Candidates = 120;
        private const int MaxQueryLength = 200;

        private readonly IAuthorizationContext _authContext;
        private readonly ITaskManager _taskManager;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public SmartSearchService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            _taskManager = appHost.Resolve<ITaskManager>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        /// <summary>
        /// Library items and Seerr titles that match a plain sentence such as "90s movie about a kid
        /// pulled into a board game". Items use the trimmed card shape of the other Moonfin browse
        /// endpoints, Seerr titles the shape of Seerr's own search results.
        /// </summary>
        public async Task<object> Get(GetSmartSearchRequest request)
        {
            var service = Plugin.Instance?.SmartSearch;
            if (!SmartSearchIndexService.Enabled || service == null)
            {
                Request.Response.StatusCode = 404;
                return null!;
            }

            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null || user.Id == Guid.Empty) return Json(401, new { error = "User not authenticated" });

            var text = (request.Query ?? string.Empty).Trim();
            if (text.Length == 0) return Json(400, new { error = "query is required" });
            if (text.Length > MaxQueryLength) text = text.Substring(0, MaxQueryLength);

            if (!service.Engine.Ready)
            {
                // Turning the feature on doesn't restart the server, so the first search starts the build.
                _taskManager.QueueIfNotRunning<SmartSearchIndexTask>();
                return Json(new { Ready = false, Items = Array.Empty<object>(), Seerr = Array.Empty<object>() });
            }

            var max = Math.Clamp(request.Limit ?? 20, 1, MaxLimit);
            var started = DateTime.UtcNow;
            var results = service.Engine.Search(text, Candidates);

            var items = new List<object>();
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in results.Library)
            {
                var item = service.Resolve(hit.Entry);
                // People sit outside every library, so only the titles they're credited on are access checked.
                if (item == null || (hit.Entry.Kind != SmartQuery.Person && !item.IsVisibleStandalone(user))) continue;

                // The same show in two libraries would otherwise fill two cards with one title.
                if (!shown.Add($"{hit.Entry.Kind}|{item.Name}|{item.ProductionYear}")) continue;

                var card = SettingsService.MapItemToDto(item, user);
                items.Add(request.Debug ? new { Card = card, hit.Score, hit.Similarity } : card);
                if (items.Count >= max) break;
            }

            var seerr = new List<JsonNode>();
            if (Plugin.Instance?.Configuration?.SeerrEnabled == true)
            {
                var allowed = await SeerrService.VerdictFor(user).ConfigureAwait(false);
                foreach (var hit in results.Catalog)
                {
                    if (allowed != null && !await allowed(hit.Entry.Kind == SmartQuery.Movie ? "movie" : "tv", long.Parse(hit.Entry.TmdbId)).ConfigureAwait(false)) continue;

                    var payload = JsonNode.Parse(hit.Entry.Payload)!;
                    if (request.Debug)
                    {
                        payload["score"] = hit.Score;
                        payload["similarity"] = hit.Similarity;
                    }

                    seerr.Add(payload);
                    if (seerr.Count >= max) break;
                }
            }

            if (request.Debug)
            {
                var q = results.Query;
                return Json(new
                {
                    Ready = true,
                    Items = items,
                    Seerr = seerr,
                    Query = new { q.YearMin, q.YearMax, Kinds = q.Kinds.ToArray(), People = q.People.ToArray() },
                    Milliseconds = (DateTime.UtcNow - started).TotalMilliseconds
                });
            }

            return Json(new { Ready = true, Items = items, Seerr = seerr });
        }
    }
}
