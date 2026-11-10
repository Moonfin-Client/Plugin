using System.Globalization;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using Moonfin.Recommendations;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>Similar movies and shows for an item, ranked by Moonfin Recommends.</summary>
    public class SimilarItemsService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public SimilarItemsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetSimilarItemsRequest request)
        {
            var none = new { Items = Array.Empty<object>(), TotalRecordCount = 0 };

            // Empty rather than 404 when it's off, because that's what the app falls back to the
            // server's own similar items on.
            if (Plugin.Instance?.Configuration?.RecommendationsProviderEnabled == false) return Json(none);

            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return Json(401, new { Error = "User not authenticated" });

            var lm = PluginServices.LibraryManager;
            if (lm == null) return Json(503, new { Error = "The library is not ready yet." });

            var item = FindItem(lm, request.Id);
            if (item == null) return Json(404, new { Error = "Item not found" });

            var excluded = new HashSet<long> { item.InternalId };
            foreach (var id in (request.ExcludeItemIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var other = FindItem(lm, id.Trim());
                if (other != null) excluded.Add(other.InternalId);
            }

            var cards = Rank(lm, item, user, request.Limit, excluded)
                .Where(i => i.IsVisibleStandalone(user))
                .Select(i => SettingsService.MapItemToDto(i, user))
                .ToList();

            return Json(new { Items = cards, TotalRecordCount = cards.Count });
        }

        // Emby apps send the numeric internal id, older links a GUID.
        private static BaseItem? FindItem(ILibraryManager lm, string id)
        {
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId))
                return internalId > 0 ? lm.GetItemById(internalId) : null;
            return Guid.TryParse(id, out var guid) ? lm.GetItemById(guid) : null;
        }

        private static List<BaseItem> Rank(ILibraryManager lm, BaseItem item, User user, int? limit, HashSet<long> excluded)
        {
            var isSeries = item is Series;
            if (!(item is Movie) && !isSeries) return new List<BaseItem>();

            var seed = new SimilarSeed(Facts(item), Credits(lm.GetItemPeople(item)));
            var type = isSeries ? "Series" : "Movie";
            var candidates = new Dictionary<long, BaseItem>();

            void Collect(Action<InternalItemsQuery>? filter, int count)
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { type },
                    IsVirtualItem = false,
                    Recursive = true,
                    Limit = count,
                    EnableTotalRecordCount = false,
                    User = user,
                    // A popular genre alone can cover most of the library, so ordering first makes
                    // the capped pool the best rated or the newest rather than whatever came back.
                    OrderBy = new[] { (filter == null ? ItemSortBy.PremiereDate : ItemSortBy.CommunityRating, SortOrder.Descending) }
                };
                filter?.Invoke(query);

                foreach (var candidate in lm.GetItemList(query))
                {
                    if (!excluded.Contains(candidate.InternalId)) candidates[candidate.InternalId] = candidate;
                }
            }

            if (seed.Genres.Count > 0) Collect(q => q.Genres = seed.Genres.ToArray(), SimilarScoring.MaxCandidatesPerQuery);
            if (seed.Tags.Count > 0) Collect(q => q.Tags = seed.Tags.ToArray(), SimilarScoring.MaxCandidatesPerQuery);
            if (candidates.Count < SimilarScoring.SparseCandidateThreshold) Collect(null, SimilarScoring.SparseFallbackCount);

            return SimilarScoring.Rank(
                candidates.Values,
                seed,
                SimilarScoring.EffectiveLimit(limit),
                Facts,
                i => i.PremiereDate?.UtcDateTime,
                _ => i => Credits(lm.GetItemPeople(i)),
                CancellationToken.None);
        }

        private static SimilarItemFacts Facts(BaseItem item) =>
            new SimilarItemFacts(item.Name, item.Genres, item.Tags, item.Studios, item.ProductionYear, item.CommunityRating);

        private static IEnumerable<(string? Name, CreditRole Role)> Credits(IEnumerable<PersonInfo>? people) =>
            (people ?? Enumerable.Empty<PersonInfo>()).Select(p => ((string?)p.Name, p.Type switch
            {
                PersonType.Actor => CreditRole.Actor,
                PersonType.GuestStar => CreditRole.Actor,
                PersonType.Director => CreditRole.Director,
                PersonType.Writer => CreditRole.Writer,
                _ => CreditRole.Other
            }));
    }
}
