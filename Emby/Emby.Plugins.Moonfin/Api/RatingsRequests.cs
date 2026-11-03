using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/MdbList/Ratings", "GET")]
    [Authenticated]
    public class GetMdbListRatingsRequest : IReturn<object>
    {
        public string? Type { get; set; }
        public string? TmdbId { get; set; }

        /// <summary>Device profile to filter with. Falls back to global when absent or unknown.</summary>
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/MdbList/WatchingNow", "GET")]
    [Authenticated]
    public class GetMdbListWatchingNowRequest : IReturn<object>
    {
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/MdbList/KeyInfo", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetMdbListKeyInfoRequest : IReturn<object>
    {
        /// <summary>Optional key to test before saving. Falls back to the server-wide key.</summary>
        public string? Key { get; set; }
    }

    [Route("/Moonfin/MdbList/ClearCache", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearMdbListCacheRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/Tmdb/EpisodeRating", "GET")]
    [Authenticated]
    public class GetTmdbEpisodeRatingRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
    }

    [Route("/Moonfin/Tmdb/SeasonRatings", "GET")]
    [Authenticated]
    public class GetTmdbSeasonRatingsRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
        public int Season { get; set; }
    }

    [Route("/Moonfin/Tmdb/ProductionCompanies", "GET")]
    [Authenticated]
    public class GetProductionCompaniesRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
        public string? Type { get; set; }
    }

    [Route("/Moonfin/Tmdb/StudioImage/{CompanyId}", "GET")]
    [Authenticated]
    public class GetStudioImageRequest : IReturn<object>
    {
        public int CompanyId { get; set; }
    }

    [Route("/Moonfin/Tmdb/Thumb", "GET")]
    [Authenticated]
    public class GetTmdbThumbRequest : IReturn<object>
    {
        public string? Type { get; set; }
        public long? TmdbId { get; set; }
        public string? ImdbId { get; set; }

        /// <summary>http for a client that can't follow a redirect from http to https, like a Roku on a plain http server.</summary>
        public string? Scheme { get; set; }
    }

    [Route("/Moonfin/Tmdb/Details", "GET")]
    [Authenticated]
    public class GetTmdbDetailsRequest : IReturn<object>
    {
        public string? Type { get; set; }
        public long? TmdbId { get; set; }
        public string? ImdbId { get; set; }
    }

    [Route("/Moonfin/Tmdb/NextEpisode", "GET")]
    [Authenticated]
    public class GetTmdbNextEpisodeRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
    }

    [Route("/Moonfin/Tmdb/PersonLinks", "GET")]
    [Authenticated]
    public class GetTmdbPersonLinksRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
    }

    [Route("/Moonfin/Tmdb/Reviews", "GET")]
    [Authenticated]
    public class GetTmdbReviewsRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
        public string? Type { get; set; }
        public string? Language { get; set; }
    }
}
