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
                otherOwners = OwnerList(service.OtherOwners(showing, user.Id, OwnersEnabled())),
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

        // ---- Recording owners (My Recordings) ----

        private static bool OwnersEnabled() => Plugin.Instance?.Configuration?.LiveTvRecordingOwners ?? true;

        private object OwnersNotFound() => Json(404, new { error = "Recording owners are not available" });

        private static object[] OwnerList(System.Collections.Generic.IEnumerable<OwnerInfo> owners) =>
            owners.Select(o => (object)new { userId = o.UserId.ToString("N"), name = o.Name, imageTag = o.ImageTag }).ToArray();

        private static bool ProgramVisible(string programId, Guid userId)
        {
            var host = PluginServices.RecordingHost;
            var program = host?.GetProgram(programId);
            return program != null && host!.IsVisibleTo(program.ItemId, userId);
        }

        private object OwnershipResult(OwnershipOutcome outcome) =>
            Json(new { id = outcome.Id, owners = OwnerList(outcome.Owners), joined = outcome.Joined });

        /// <summary>Records a program as the caller, or joins the timer that already records it.</summary>
        public async Task<object> Post(RecordOwnerRequest request)
        {
            var service = PluginServices.RecordingOwners;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();
            if (!OwnersEnabled()) return OwnersNotFound();

            var body = await MoonfinJson.ReadBodyAsync<RecordOwnerBody>(request.RequestStream).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.ProgramId)) return Json(400, new { error = "programId is required." });
            if (!ProgramVisible(body!.ProgramId!, user.Id)) return Json(404, new { error = "No guide program with that id" });

            var attempt = await service.TryRecordProgramAsync(body.ProgramId!, user.Id, CancellationToken.None).ConfigureAwait(false);
            return attempt.Outcome == null
                ? Json(409, new { error = attempt.ErrorOr("The server could not schedule the recording.") })
                : OwnershipResult(attempt.Outcome);
        }

        /// <summary>Records a program's series as the caller, or joins the rule that already records it.</summary>
        public async Task<object> Post(RecordSeriesOwnerRequest request)
        {
            var service = PluginServices.RecordingOwners;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();
            if (!OwnersEnabled()) return OwnersNotFound();

            var body = await MoonfinJson.ReadBodyAsync<RecordOwnerBody>(request.RequestStream).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.ProgramId)) return Json(400, new { error = "programId is required." });
            if (!ProgramVisible(body!.ProgramId!, user.Id)) return Json(404, new { error = "No guide program with that id" });

            var attempt = await service.TryRecordSeriesAsync(body.ProgramId!, user.Id, body.Settings, CancellationToken.None).ConfigureAwait(false);
            return attempt.Outcome == null
                ? Json(409, new { error = attempt.ErrorOr("The server could not create the series recording.") })
                : OwnershipResult(attempt.Outcome);
        }

        /// <summary>Adds the caller to the owners of an existing timer, series rule or recording.</summary>
        public async Task<object> Post(ClaimOwnerRequest request)
        {
            var service = PluginServices.RecordingOwners;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvManagement != true) return NotAllowed();
            if (!OwnersEnabled()) return OwnersNotFound();

            var body = await MoonfinJson.ReadBodyAsync<ClaimOwnerBody>(request.RequestStream).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Id)) return Json(400, new { error = "id is required." });

            OwnedKind kind;
            switch (body!.Kind?.ToLowerInvariant())
            {
                case "timer": kind = OwnedKind.Timer; break;
                case "series": kind = OwnedKind.Series; break;
                case "item": kind = OwnedKind.Item; break;
                default: return Json(400, new { error = "kind must be timer, series or item." });
            }

            var id = body.Id!;
            if (kind == OwnedKind.Item)
            {
                var itemId = ResolveItem(id);
                if (itemId == null || PluginServices.RecordingHost?.IsVisibleTo(itemId.Value, user.Id) != true)
                {
                    return Json(404, new { error = "No recording with that id" });
                }

                id = itemId.Value.ToString("N");
            }

            var owners = await service.ClaimAsync(kind, id, user.Id, CancellationToken.None).ConfigureAwait(false);
            return owners == null
                ? Json(404, new { error = "Nothing to claim with that id" })
                : Json(new { owners = OwnerList(owners) });
        }

        /// <summary>The owners of the given timers, series rules and recordings, in one call.</summary>
        public async Task<object> Post(LookupOwnersRequest request)
        {
            var service = PluginServices.RecordingOwners;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvAccess != true) return NotAllowed();
            if (!OwnersEnabled()) return OwnersNotFound();

            var body = await MoonfinJson.ReadBodyAsync<LookupOwnersBody>(request.RequestStream).ConfigureAwait(false);
            var itemIds = (body?.ItemIds ?? new System.Collections.Generic.List<string>())
                .Select(s => ResolveItem(s?.Trim()))
                .Where(id => id != null)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var lookup = await service.LookupAsync(
                body?.TimerIds ?? new System.Collections.Generic.List<string>(),
                body?.SeriesTimerIds ?? new System.Collections.Generic.List<string>(),
                itemIds,
                user.Id,
                CancellationToken.None).ConfigureAwait(false);

            var timers = lookup.Timers.ToDictionary(p => p.Key, p => OwnerList(p.Value));
            var seriesTimers = lookup.SeriesTimers.ToDictionary(p => p.Key, p => OwnerList(p.Value));
            var items = lookup.Items.ToDictionary(p => ClientId(p.Key.ToString("N")), p => OwnerList(p.Value));

            // recordingItemIds is left out, not sent as null, when the server can't tell: clients
            // read a missing field as "unknown".
            return lookup.RecordingItemIds == null
                ? Json(new { timers, seriesTimers, items })
                : Json(new { timers, seriesTimers, items, recordingItemIds = lookup.RecordingItemIds.Select(i => ClientId(i.ToString("N"))).ToArray() });
        }

        /// <summary>Everything the caller owns. Timer ids may be stale; clients intersect them with the live timer list.</summary>
        public async Task<object> Get(GetMyOwnedRequest request)
        {
            var service = PluginServices.RecordingOwners;
            if (service == null) return NotReady();
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return NoUser();
            if (user.Policy?.EnableLiveTvAccess != true) return NotAllowed();
            if (!OwnersEnabled()) return OwnersNotFound();

            var mine = await service.GetMineAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            return Json(new
            {
                timerIds = mine.TimerIds,
                seriesTimerIds = mine.SeriesTimerIds,
                itemIds = mine.ItemIds.Select(i => ClientId(i.ToString("N"))).ToArray(),
            });
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
