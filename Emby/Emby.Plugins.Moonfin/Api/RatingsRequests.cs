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
}
