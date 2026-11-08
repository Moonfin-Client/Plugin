using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/MiniGames/{GameId}/Scores", "GET")]
    [Authenticated]
    public class GetMiniGameScoresRequest : IReturn<object>
    {
        public string? GameId { get; set; }
    }

    [Route("/Moonfin/MiniGames/{GameId}/Scores", "POST")]
    [Authenticated]
    public class PostMiniGameScoreRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? GameId { get; set; }

        public System.IO.Stream RequestStream { get; set; } = null!;
    }
}
