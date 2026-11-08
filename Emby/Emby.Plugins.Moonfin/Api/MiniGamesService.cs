using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Moonfin.MiniGames;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>The top 3 of each Moonfin After Hours game, shared by everyone on the server.</summary>
    public class MiniGamesService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public MiniGamesService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetMiniGameScoresRequest request)
        {
            var scores = Plugin.Instance?.MiniGames;
            if (scores == null) return NotReady();
            if (!MiniGameScoreService.IsGame(request.GameId)) return NoGame();

            return Json(scores.Top(request.GameId!));
        }

        public async Task<object> Post(PostMiniGameScoreRequest request)
        {
            var scores = Plugin.Instance?.MiniGames;
            if (scores == null) return NotReady();
            if (!MiniGameScoreService.IsGame(request.GameId)) return NoGame();

            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null || user.Id == System.Guid.Empty || string.IsNullOrEmpty(user.Name))
                return Json(401, new { error = "A valid user token is required" });

            var body = await MoonfinJson.ReadBodyAsync<JsonElement>(request.RequestStream).ConfigureAwait(false);
            var score = MiniGameScoreService.ReadScore(body);
            if (score == null) return Json(400, new { error = "The score must be a whole number from 0 to 9,999,999" });

            return Json(new MiniGameRank { Rank = scores.Submit(request.GameId!, user.Id, user.Name, score.Value) });
        }

        private object NotReady() => Json(503, new { error = "The plugin is not ready" });

        private object NoGame() => Json(404, new { error = "There is no such game" });
    }
}
