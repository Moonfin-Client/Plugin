using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/UserRatings/Mine", "GET")]
    [Authenticated]
    public class GetMyUserRatingsRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/UserRatings/{ItemId}", "POST")]
    [Authenticated]
    public class SetMyUserRatingRequest : IReturn<object>
    {
        public string ItemId { get; set; } = string.Empty;

        /// <summary>Score out of ten. Send this or <see cref="Likes"/>.</summary>
        public double? Rating { get; set; }

        /// <summary>A thumb. Send this or <see cref="Rating"/>.</summary>
        public bool? Likes { get; set; }
    }

    [Route("/Moonfin/UserRatings/{ItemId}", "DELETE")]
    [Authenticated]
    public class DeleteMyUserRatingRequest : IReturn<object>
    {
        public string ItemId { get; set; } = string.Empty;
    }
}
