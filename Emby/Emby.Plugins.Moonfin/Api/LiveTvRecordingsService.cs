using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Incomplete Live TV recordings for Moonfin clients. Permissions mirror Emby's own Live TV
    /// policies, and items are addressed by Emby's numeric ids.
    /// </summary>
    public class LiveTvRecordingsService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public LiveTvRecordingsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        /// <summary>
        /// The incomplete state of each listed item, or of every incomplete item when none are listed.
        /// Items that aren't incomplete are left out.
        /// </summary>
        public async Task<object> Get(GetIncompleteRecordingsRequest request)
        {
            var service = PluginServices.IncompleteRecordings;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvAccess != true) return NotAllowed();

            var ids = (request.ItemIds ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ResolveItem(s.Trim()))
                .Where(id => id != null)
                .Select(id => id!.Value)
                .Take(500)
                .ToList();

            // A list was asked about but none of it names an item: that is no items, not all of them.
            if (ids.Count == 0 && !string.IsNullOrWhiteSpace(request.ItemIds))
            {
                return Json(Array.Empty<IncompleteRecordingStateDto>());
            }

            var states = await service.GetStatesAsync(ids.Count > 0 ? ids : null, user.Id, CancellationToken.None).ConfigureAwait(false);
            foreach (var state in states)
            {
                state.ItemId = ClientId(state.ItemId);
            }

            return Json(states);
        }

        /// <summary>Keep as Complete: dismisses the item's incomplete record for everyone and removes its tag.</summary>
        public async Task<object> Post(DismissIncompleteRecordingRequest request)
        {
            var service = PluginServices.IncompleteRecordings;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();

            var itemId = ResolveItem(request.ItemId);
            if (itemId == null || !await service.DismissAsync(itemId.Value, user.Id, CancellationToken.None).ConfigureAwait(false))
            {
                return Json(404, new { error = "No incomplete recording with that id" });
            }

            Request.Response.StatusCode = 204;
            return null!;
        }

        /// <summary>
        /// Asks for a later showing of a program's episode to be recorded. Repeating the call for the
        /// same episode joins the same request, so a client can safely retry it.
        /// </summary>
        public async Task<object> Post(RequestCompleteShowingRequest request)
        {
            var service = PluginServices.IncompleteRecordings;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();

            var body = await MoonfinJson.ReadBodyAsync<CompleteShowingBody>(request.RequestStream).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.ProgramId))
            {
                return Json(400, new { error = "programId is required." });
            }

            var (showing, refusal) = await service.RequestCompleteShowingAsync(body!.ProgramId!, user.Id, CancellationToken.None).ConfigureAwait(false);
            if (showing == null)
            {
                return refusal == CompleteShowingRefusal.NoEpisodeNumbers
                    ? Json(400, new { error = "The program needs season and episode numbers, or movie classification with a title and description." })
                    : Json(404, new { error = "No guide program with that id" });
            }

            return Json(new
            {
                state = IncompleteRecordingRules.DisplayState(showing),
                scheduledStart = showing.ScheduledStart,
                channelName = showing.ScheduledChannelName,
            });
        }

        /// <summary>Whether a program's episode or movie can be searched for, and the future showing that would record it.</summary>
        public async Task<object> Get(GetCompleteShowingOptionsRequest request)
        {
            var service = PluginServices.IncompleteRecordings;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();

            return Json(await service.GetCompleteShowingOptionsAsync(request.ProgramId ?? string.Empty, user.Id, CancellationToken.None).ConfigureAwait(false));
        }

        /// <summary>Schedules the future showing the user confirmed, when it still matches.</summary>
        public async Task<object> Post(ScheduleSuggestedShowingRequest request)
        {
            var service = PluginServices.IncompleteRecordings;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();

            var body = await MoonfinJson.ReadBodyAsync<SuggestedShowingBody>(request.RequestStream).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.ProgramId) || body!.Showing == null)
            {
                return Json(400, new { error = "programId and showing are required." });
            }

            if (!await service.ScheduleSuggestedShowingAsync(body.ProgramId!, body.Showing, user.Id, CancellationToken.None).ConfigureAwait(false))
            {
                return Json(409, new { error = "The selected airing is no longer available or no longer matches." });
            }

            Request.Response.StatusCode = 204;
            return null!;
        }

        public object Get(GetIncompleteRecordingSettingsRequest request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvAccess != true) return NotAllowed();

            return Json(IncompleteRecordingConfig.Read());
        }

        public async Task<object> Post(SaveIncompleteRecordingSettingsRequest request)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return NotReady();

            var settings = await MoonfinJson.ReadBodyAsync<IncompleteRecordingSettings>(request.RequestStream).ConfigureAwait(false);
            if (settings == null) return Json(400, new { error = "A settings body is required" });

            plugin.Configuration.LiveTvDetectIncompleteRecordings = settings.Enabled;
            plugin.Configuration.LiveTvAutoRecordCompleteShowing = settings.AutoRecordComplete;
            plugin.Configuration.LiveTvIncompleteThresholdPercent = IncompleteRecordingRules.ClampThreshold(settings.ThresholdPercent);
            plugin.Configuration.LiveTvDeleteIncompleteCopies = settings.DeleteIncompleteCopies;
            plugin.SaveConfiguration();
            return Json(IncompleteRecordingConfig.Read());
        }

        // Emby clients send numeric item ids, and the feature keys items by their Guid.
        private static Guid? ResolveItem(string? id)
        {
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId))
            {
                return PluginServices.LibraryManager?.GetItemById(internalId)?.Id;
            }

            return Guid.TryParse(id, out var guid) ? guid : (Guid?)null;
        }

        private static string ClientId(string itemId) =>
            Guid.TryParse(itemId, out var guid) && PluginServices.LibraryManager?.GetItemById(guid) is BaseItem item
                ? item.InternalId.ToString(CultureInfo.InvariantCulture)
                : itemId;

        private object NotReady() => Json(503, new { error = "The plugin is not ready" });

        private object NoUser() => Json(401, new { error = "A valid user token is required" });

        private object NotAllowed() => Json(403, new { error = "This user can't use Live TV recordings" });
    }
}
