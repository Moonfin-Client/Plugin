using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class SeasonalService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private SeasonalRowService Rows => Plugin.Instance?.SeasonalRows
            ?? throw new InvalidOperationException("SeasonalRowService not initialized");

        public SeasonalService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public async Task<object> Get(GetSeasonalRowRequest request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null || user.Id == Guid.Empty) return Json(401, new { error = "User not authenticated" });

            try
            {
                return Json(await Rows.BuildAsync(user, request.Country, DateTime.Now, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception)
            {
                return Json(new SeasonalRowResponse());
            }
        }
    }
}
