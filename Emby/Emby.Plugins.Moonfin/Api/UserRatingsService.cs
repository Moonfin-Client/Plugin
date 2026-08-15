using System;
using System.Collections.Generic;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Serves the calling user's personal ratings in one response, so clients
    /// can sort and filter a library by them. The server has no user rating
    /// sort, and its liked and disliked item filters are defined against the
    /// rating threshold, which makes their union exactly the set of rated
    /// items.
    /// </summary>
    public class UserRatingsService : IService, IRequiresRequest, IHasResultFactory
    {
        // The server derives its liked and disliked filters from the rating at
        // this threshold rather than storing a thumb of its own.
        private const double LikedRatingThreshold = 6.5;

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public UserRatingsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetMyUserRatingsRequest request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return Json(401, new { Error = "User not authenticated" });

            var lm = PluginServices.LibraryManager;
            var udm = PluginServices.UserDataManager;
            if (lm == null || udm == null) return Json(new { Items = Array.Empty<object>() });

            var seen = new HashSet<long>();
            var items = new List<object>();

            // Liked is a rating at or above the threshold and disliked is below
            // it, and neither matches an unrated item, so the two passes cover
            // the rated items and nothing else.
            foreach (var liked in new[] { true, false })
            {
                var query = new InternalItemsQuery
                {
                    Recursive = true,
                    IsLiked = liked,
                    User = user
                };

                foreach (var item in lm.GetItemList(query))
                {
                    if (!seen.Add(item.InternalId)) continue;

                    var userData = udm.GetUserData(user, item);
                    if (userData?.Rating == null) continue;

                    items.Add(new
                    {
                        ItemId = item.Id.ToString("N"),
                        Rating = userData.Rating,
                        Likes = userData.Rating >= LikedRatingThreshold
                    });
                }
            }

            return Json(new { Items = items });
        }
    }
}
