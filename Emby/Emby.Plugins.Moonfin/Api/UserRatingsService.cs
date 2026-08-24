using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Serves and stores the calling user's personal ratings, so clients can
    /// rate an item and sort and filter a library by what they rated.
    ///
    /// Writing goes through here because the server's own rating route answers
    /// 200 and stores nothing, and it leaves the rating out of the item it
    /// sends back. The user data store underneath it holds a rating fine, so
    /// that is what these routes read and write.
    /// </summary>
    public class UserRatingsService : IService, IRequiresRequest, IHasResultFactory
    {
        // The server derives its liked and disliked filters from the rating at
        // this threshold rather than storing a thumb of its own.
        private const double LikedRatingThreshold = 6.5;

        // A thumb has to be stored as a score, so it uses the ends of the
        // scale. These are the scores Jellyfin writes for the same gesture,
        // which keeps a thumb reading the same on either server.
        private const double LikedRating = 10;
        private const double DislikedRating = 1;

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

            var items = new List<object>();

            // The liked filter is the only user rating filter the server
            // exposes and it narrows nothing, answering the same for true as
            // for false, so the liked pass and the not liked pass it used to
            // run were two reads of the whole library. One read that keeps
            // what carries a rating gives the same answer for half the work.
            var query = new InternalItemsQuery
            {
                Recursive = true,
                User = user
            };

            foreach (var item in lm.GetItemList(query))
            {
                var userData = udm.GetUserData(user, item);
                if (userData?.Rating == null) continue;

                items.Add(new
                {
                    ItemId = item.Id.ToString("N"),
                    Rating = userData.Rating,
                    Likes = userData.Rating >= LikedRatingThreshold
                });
            }

            return Json(new { Items = items });
        }

        public object Post(SetMyUserRatingRequest request)
        {
            if (request.Rating == null && request.Likes == null)
            {
                return Json(400, new { Error = "Send either a rating or a thumb." });
            }

            if (request.Rating != null && (request.Rating < 0 || request.Rating > 10))
            {
                return Json(400, new { Error = "A rating runs from 0 to 10." });
            }

            var rating = request.Rating ?? (request.Likes == true ? LikedRating : DislikedRating);
            return SaveRating(request.ItemId, rating);
        }

        public object Delete(DeleteMyUserRatingRequest request)
        {
            return SaveRating(request.ItemId, null);
        }

        private object SaveRating(string itemId, double? rating)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return Json(401, new { Error = "User not authenticated" });

            var lm = PluginServices.LibraryManager;
            var udm = PluginServices.UserDataManager;
            if (lm == null || udm == null)
            {
                return Json(503, new { Error = "The library is not ready yet." });
            }

            if (!Guid.TryParse(itemId, out var guid))
            {
                return Json(400, new { Error = "That is not an item id." });
            }

            var item = lm.GetItemById(guid);
            if (item == null || !item.IsVisible(user))
            {
                return Json(404, new { Error = "No such item." });
            }

            var userData = udm.GetUserData(user, item);
            if (userData == null)
            {
                return Json(503, new { Error = "The user data store is not ready yet." });
            }

            userData.Rating = rating;
            udm.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

            return Json(new
            {
                ItemId = item.Id.ToString("N"),
                Rating = rating,
                // A cleared rating is neither liked nor disliked, so the thumb
                // goes back null rather than reading as a dislike.
                Likes = rating == null ? (bool?)null : rating >= LikedRatingThreshold
            });
        }
    }
}
