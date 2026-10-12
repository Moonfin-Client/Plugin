using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class CinemaService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;
        private readonly CinemaMediaResolver _resolver;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public CinemaService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            _resolver = new CinemaMediaResolver(
                appHost.Resolve<ILibraryManager>(),
                appHost.Resolve<IProviderManager>());
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);

        private object Json(int statusCode, object? body)
        {
            Request.Response.StatusCode = statusCode;
            return Json(body);
        }

        private object Unresolved() => Json(new
        {
            tmdbId = (int?)null,
            mediaType = (string?)null,
        });

        public async Task<object> Get(ResolveCinemaMediaRequest request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return Json(401, new { error = "User not authenticated" });

            var expected = request.ExpectedMediaType;
            if (string.IsNullOrWhiteSpace(request.ItemId) || (expected != null && expected != "movie" && expected != "tv"))
                return Json(400, new { error = "Invalid Cinema media request" });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                var result = await _resolver.ResolveMediaAsync(
                    request.ItemId,
                    user,
                    expected,
                    timeout.Token,
                    Plugin.Instance?.Configuration.CinemaFilenameSearchEnabled == true).ConfigureAwait(false);

                return Json(new
                {
                    tmdbId = result.TmdbId,
                    mediaType = result.MediaType,
                });
            }
            catch (OperationCanceledException)
            {
                return Unresolved();
            }
            catch
            {
                // A failed lookup should leave playback and Skip available.
                return Unresolved();
            }
        }
    }
}
