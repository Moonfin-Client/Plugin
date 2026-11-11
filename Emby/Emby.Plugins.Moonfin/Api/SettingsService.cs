using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using Moonfin.Calendar;

namespace Emby.Plugins.Moonfin.Api
{
    public class SettingsService : IService, IRequiresRequest, IHasResultFactory
    {
        private const int MaxDetailsBackdropBlur = 40;

        private readonly IAuthorizationContext _authContext;
        private readonly ILogger _logger;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private MoonfinSettingsService Settings => Plugin.Instance?.SettingsService
            ?? throw new InvalidOperationException("MoonfinSettingsService not initialized");

        private ILibraryManager? LibraryManager => PluginServices.LibraryManager;

        public SettingsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("MoonfinSettings");
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        // Guards a settings-sync handler. Returns true with userId set when sync is enabled and
        // the caller is authenticated. Otherwise returns false and sets error to the result the
        // handler should return.
        private bool TryAuthorizedSync([NotNullWhen(true)] out Guid? userId, [NotNullWhen(false)] out object? error)
        {
            userId = null;
            error = null;
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
            {
                error = Json(503, new { Error = "Settings sync is disabled" });
                return false;
            }

            userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null)
            {
                error = Json(401, new { Error = "User not authenticated" });
                return false;
            }
            return true;
        }

        public object Get(GetPingRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            var seerrEnabled = config?.SeerrEnabled ?? false;
            var seerrUrl = seerrEnabled ? config?.SeerrUrl : null;

            // Member names serialize verbatim, so these spell the documented
            // camelCase.
            return Json(new
            {
                installed = true,
                version = Plugin.Instance?.Version.ToString() ?? "1.0.0.0",
                settingsSyncEnabled = config?.EnableSettingsSync ?? false,
                serverName = "Emby",
                seerrEnabled,
                seerrUrl,
                mdblistAvailable = !string.IsNullOrWhiteSpace(config?.MdblistApiKey),
                tmdbAvailable = !string.IsNullOrWhiteSpace(config?.TmdbApiKey),
                // Older plugins leave this out, so the app keeps the backdrop Seerr gave it.
                tmdbThumbsSupported = true,
                // Older plugins leave this out, so the app shows only the IMDb and TMDB person links
                // it can build itself.
                tmdbPersonLinksSupported = true,
                // Older plugins leave this out, so the app hides the Reviews section.
                tmdbReviewsSupported = true,
                // Older plugins leave this out, so the app shows an outside title with only what
                // its list carried.
                tmdbDetailsSupported = true,
                // Older plugins leave this out, which the app reads as false and hides the
                // messages button.
                messagesSupported = true,
                pinSupported = true,
                smartSearchEnabled = config?.SmartSearchEnabled ?? false,
                recommendationsSupported = config?.RecommendationsProviderEnabled ?? true,
                incompleteRecordingsSupported = config?.LiveTvDetectIncompleteRecordings ?? true,
                extendedCompleteShowingsSupported = config?.LiveTvDetectIncompleteRecordings ?? true,
                clientLogSupported = config?.EnableClientLogUpload ?? false,
                calendarAvailable = seerrEnabled
                    && !string.IsNullOrEmpty(config?.GetEffectiveSeerrUrl())
                    && ArrCalendarWindow.Refusal(config?.CalendarAdminsOnly == true, AuthHelpers.IsCurrentUserAdmin(Request, _authContext)) == null,
                calendarDaysBack = Math.Max(0, config?.CalendarDaysBack ?? 14),
                calendarDaysAhead = Math.Max(0, config?.CalendarDaysAhead ?? 90),
                calendarReminders = true,
                // Older plugins leave this out, so the app shows no download queue details.
                arrQueueAvailable = seerrEnabled && !string.IsNullOrEmpty(config?.GetEffectiveSeerrUrl()),
                defaultSettings = MoonfinSettingsService.AdminDefaultsFor(request.Profile, config)
            });
        }

        // Real-time events ride the session websocket as MoonfinEvent frames, so this tells
        // clients which transport to listen on.
        public object Get(StreamSettingsRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableSettingsSync != true)
                return Json(503, new { Error = "Settings sync is disabled" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { Error = "User not authenticated" });

            return Json(new { supported = false, transport = "websocket" });
        }

        public async Task<object> Get(GetMySettingsRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var settings = await Settings.GetUserSettingsAsync(userId.Value).ConfigureAwait(false);
            if (settings == null) return Json(404, new { Error = "No settings found" });
            return Json(settings);
        }

        public object? Head(CheckMySettingsRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
            { Request.Response.StatusCode = 503; return null; }

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) { Request.Response.StatusCode = 401; return null; }

            Request.Response.StatusCode = Settings.UserSettingsExist(userId.Value) ? 200 : 404;
            return null;
        }

        public async Task<object> Post(SaveMySettingsRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var body = await MoonfinJson.ReadBodyAsync<SaveSettingsBody>(request.RequestStream).ConfigureAwait(false);
            if (body?.Settings == null) return Json(400, new { Error = "Settings are required" });

            var existed = Settings.UserSettingsExist(userId.Value);
            await Settings.SaveUserSettingsAsync(userId.Value, body.Settings, body.ClientId, body.MergeMode ?? "merge").ConfigureAwait(false);
            return Json(new { Success = true, Created = !existed, UserId = userId.Value });
        }

        public async Task<object> Delete(DeleteMySettingsRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            await Settings.DeleteUserSettingsAsync(userId.Value).ConfigureAwait(false);
            return Json(new { Success = true, Message = "Settings deleted" });
        }

        public async Task<object> Get(GetUserSettingsRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { Error = "Settings sync is disabled" });
            if (!Guid.TryParse(request.UserId, out var userId)) return Json(400, new { Error = "Invalid userId" });

            var settings = await Settings.GetUserSettingsAsync(userId).ConfigureAwait(false);
            if (settings == null) return Json(404, new { Error = "No settings found" });
            return Json(settings);
        }

        public async Task<object> Post(SaveUserSettingsRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { Error = "Settings sync is disabled" });
            if (!Guid.TryParse(request.UserId, out var userId)) return Json(400, new { Error = "Invalid userId" });

            var body = await MoonfinJson.ReadBodyAsync<SaveSettingsBody>(request.RequestStream).ConfigureAwait(false);
            if (body?.Settings == null) return Json(400, new { Error = "Settings are required" });

            var existed = Settings.UserSettingsExist(userId);
            await Settings.SaveUserSettingsAsync(userId, body.Settings, body.ClientId, body.MergeMode ?? "merge").ConfigureAwait(false);
            return Json(new { Success = true, Created = !existed, UserId = userId });
        }

        public async Task<object> Get(GetResolvedProfileRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableSettingsSync != true)
                return Json(503, new { Error = "Settings sync is disabled" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { Error = "User not authenticated" });

            var profile = request.Profile ?? "global";
            if (!Array.Exists(MoonfinUserSettings.ValidProfiles, p => string.Equals(p, profile, StringComparison.OrdinalIgnoreCase)))
                return Json(400, new { Error = "Invalid profile: " + profile });

            var resolved = await Settings.GetResolvedProfileAsync(userId.Value, profile).ConfigureAwait(false);
            if (resolved == null)
            {
                var adminDefaults = Settings.AdminDefaultsFor(profile);
                if (adminDefaults != null) return Json(adminDefaults);
                return Json(404, new { Error = "No settings found" });
            }

            // A pull is the only place a client learns about hides made elsewhere, so this is
            // where its baseline moves.
            await Settings.RecordHiddenContentBaselineAsync(userId.Value, profile, resolved).ConfigureAwait(false);

            return Json(resolved);
        }

        public async Task<object> Post(SaveMyProfileRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var profile = request.Profile ?? string.Empty;
            if (!Array.Exists(MoonfinUserSettings.ValidProfiles, p => string.Equals(p, profile, StringComparison.OrdinalIgnoreCase)))
                return Json(400, new { Error = "Invalid profile: " + profile });

            var body = await MoonfinJson.ReadBodyAsync<SaveProfileBody>(request.RequestStream).ConfigureAwait(false);
            if (body?.Profile == null) return Json(400, new { Error = "Profile data is required" });

            var existed = Settings.UserSettingsExist(userId.Value);
            await Settings.SaveProfileAsync(userId.Value, profile, body.Profile, body.ClientId).ConfigureAwait(false);
            return Json(new { Success = true, Created = !existed, UserId = userId.Value });
        }

        public async Task<object> Delete(DeleteMyProfileRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var profile = (request.Profile ?? string.Empty).ToLowerInvariant();
            if (profile == "global") return Json(400, new { Error = "Cannot delete the global profile." });
            if (!Array.Exists(MoonfinUserSettings.ValidProfiles, p => p == profile))
                return Json(400, new { Error = "Invalid profile: " + profile });

            await Settings.DeleteProfileAsync(userId.Value, profile).ConfigureAwait(false);
            return Json(new { Success = true, Message = $"Profile '{profile}' deleted" });
        }

        public async Task<object> Get(GetDetailsBlurRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var profileName = string.IsNullOrWhiteSpace(request.Profile) ? "global" : request.Profile.ToLowerInvariant();
            var resolved = await Settings.GetResolvedProfileAsync(userId.Value, profileName).ConfigureAwait(false)
                ?? Settings.AdminDefaultsFor(profileName)
                ?? new MoonfinSettingsProfile();
            var blur = Math.Max(0, Math.Min(MaxDetailsBackdropBlur, ResolveBackdropBlur(resolved)));
            return Json(new { Profile = profileName, DetailsScreenBlur = blur.ToString() });
        }

        public async Task<object> Post(SaveDetailsBlurRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var body = await MoonfinJson.ReadBodyAsync<SaveDetailsBlurBody>(request.RequestStream).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.DetailsScreenBlur)) return Json(400, new { Error = "detailsScreenBlur is required" });
            if (!int.TryParse(body!.DetailsScreenBlur, out var parsedBlur)) return Json(400, new { Error = "detailsScreenBlur must be numeric" });

            var targetProfile = !string.IsNullOrWhiteSpace(request.Profile) ? request.Profile!.ToLowerInvariant()
                : (!string.IsNullOrWhiteSpace(body.Profile) ? body.Profile!.ToLowerInvariant() : "global");
            var normalizedBlur = Math.Max(0, Math.Min(MaxDetailsBackdropBlur, parsedBlur));
            var profilePatch = new MoonfinSettingsProfile { DetailsBackdropBlur = normalizedBlur, DetailsScreenBlur = normalizedBlur.ToString() };

            var existed = Settings.UserSettingsExist(userId.Value);
            await Settings.SaveProfileAsync(userId.Value, targetProfile, profilePatch, body.ClientId ?? "moonfin-blur").ConfigureAwait(false);
            return Json(new { Success = true, Created = !existed, UserId = userId.Value, Profile = targetProfile, DetailsScreenBlur = normalizedBlur.ToString() });
        }

        public async Task<object> Get(GetDetailsOpacityRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var profileName = string.IsNullOrWhiteSpace(request.Profile) ? "global" : request.Profile.ToLowerInvariant();
            var resolved = await Settings.GetResolvedProfileAsync(userId.Value, profileName).ConfigureAwait(false)
                ?? Settings.AdminDefaultsFor(profileName)
                ?? new MoonfinSettingsProfile();
            var opacity = Math.Max(0, Math.Min(100, resolved.DetailsBackdropOpacity ?? 90));
            return Json(new { Profile = profileName, DetailsScreenOpacity = opacity });
        }

        public async Task<object> Post(SaveDetailsOpacityRequest request)
        {
            if (!TryAuthorizedSync(out var userId, out var authError)) return authError;

            var body = await MoonfinJson.ReadBodyAsync<SaveDetailsOpacityBody>(request.RequestStream).ConfigureAwait(false);
            if (body?.DetailsScreenOpacity == null) return Json(400, new { Error = "detailsScreenOpacity is required" });

            var targetProfile = !string.IsNullOrWhiteSpace(request.Profile) ? request.Profile!.ToLowerInvariant()
                : (!string.IsNullOrWhiteSpace(body.Profile) ? body.Profile!.ToLowerInvariant() : "global");
            var normalizedOpacity = Math.Max(0, Math.Min(100, body.DetailsScreenOpacity.Value));
            var profilePatch = new MoonfinSettingsProfile { DetailsBackdropOpacity = normalizedOpacity };

            var existed = Settings.UserSettingsExist(userId.Value);
            await Settings.SaveProfileAsync(userId.Value, targetProfile, profilePatch, body.ClientId ?? "moonfin-opacity").ConfigureAwait(false);
            return Json(new { Success = true, Created = !existed, UserId = userId.Value, Profile = targetProfile, DetailsScreenOpacity = normalizedOpacity });
        }

        public object Get(GetDefaultsRequest request)
        {
            return Json(Plugin.Instance?.Configuration?.DefaultUserSettings ?? (object)new MoonfinSettingsProfile());
        }

        // The two PushDefaults routes stay for older callers, the admin page posts to Admin/ApplyDefaults.
        public object Get(GetConfigBackupRequest request)
        {
            var backup = Plugin.Instance?.ConfigBackup;
            return Json(new { looksReset = backup?.ConfigurationLooksReset ?? false, backupPath = backup?.BackupPath });
        }

        // Deliberately manual, so an admin who reset the settings on purpose doesn't have that undone.
        public object Post(RestoreConfigRequest request)
        {
            var backup = Plugin.Instance?.ConfigBackup;
            try
            {
                if (backup == null || !backup.Restore())
                    return Json(404, new { error = "No configuration backup was found." });
            }
            catch (Exception ex)
            {
                return Json(500, new { error = ex.Message });
            }
            return Json(new
            {
                restored = true,
                restartRequired = true,
                message = "Configuration restored. Restart the server for it to take effect.",
            });
        }

        public Task<object> Post(PushDefaultsRequest request)
            => ApplyDefaultsAsync(request.Overwrite || ReadBoolQuery("overwrite"), null, null, null);

        public Task<object> Post(PushDefaultsToUserRequest request)
        {
            if (!Guid.TryParse(request.UserId, out var userId) || userId == Guid.Empty)
                return Task.FromResult(Json(400, new { error = "A valid userId is required" }));
            return ApplyDefaultsAsync(request.Overwrite || ReadBoolQuery("overwrite"), new List<Guid> { userId }, null, null);
        }

        public async Task<object> Post(ApplyDefaultsRequest request)
        {
            var body = await MoonfinJson.ReadBodyAsync<ApplyDefaultsBody>(request.RequestStream).ConfigureAwait(false);
            if (body == null) return Json(400, new { error = "A request body is required" });

            List<Guid>? userIds = null;
            if (body.UserIds != null)
            {
                userIds = new List<Guid>();
                foreach (var raw in body.UserIds)
                {
                    if (!Guid.TryParse(raw, out var id) || id == Guid.Empty)
                        return Json(400, new { error = "A valid userId is required" });
                    userIds.Add(id);
                }
                if (userIds.Count == 0) return Json(400, new { error = "A valid userId is required" });
            }

            return await ApplyDefaultsAsync(body.Overwrite, userIds, body.Profiles, body.Keys).ConfigureAwait(false);
        }

        /// <summary>
        /// Pushes the admin defaults onto users' existing settings. Merge writes the set defaults of
        /// the chosen profiles, optionally only some keys, and keeps everything else a user chose.
        /// Overwrite resets each user to a clean file holding only those defaults, and for all users
        /// also removes the files of users that no longer exist.
        /// </summary>
        private async Task<object> ApplyDefaultsAsync(bool overwrite, List<Guid>? userIds, List<string>? requestedProfiles, List<string>? keys)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableSettingsSync != true)
                return Json(503, new { error = "Settings sync is disabled" });

            var profiles = ProfilesOrAll(requestedProfiles);
            if (profiles == null) return Json(400, new { error = UnknownProfileError });
            if (profiles.All(p => config.GetDefaultsFor(p) == null))
                return Json(400, new { error = "No default user settings configured" });

            if (overwrite && keys != null)
                return Json(400, new { error = "A full reset applies every default, so it can't take a list of settings" });

            int usersAffected;
            var orphansDeleted = 0;

            if (overwrite)
            {
                var targets = (IReadOnlyCollection<Guid>?)userIds ?? AuthHelpers.GetAllServerUserIds();
                if (targets == null)
                    return Json(503, new { error = "User manager unavailable" });

                (usersAffected, orphansDeleted) = await Settings.ResetUsersToDefaultsAsync(config, profiles, targets, deleteOrphans: userIds == null).ConfigureAwait(false);
            }
            else
            {
                usersAffected = await Settings.MergeDefaultsAsync(config, profiles, keys, userIds).ConfigureAwait(false);
            }

            // Hidden libraries are part of the defaults as a whole, not a setting someone can pick.
            if (keys == null)
            {
                if (userIds == null) PluginServices.HiddenLibraries?.ApplyToAll();
                else foreach (var userId in userIds) PluginServices.HiddenLibraries?.ApplyTo(userId);
            }

            var liveRefreshDeliveries = 0;
            if (userIds == null) liveRefreshDeliveries = Settings.BroadcastSystemEvent("settingsUpdated");
            else foreach (var userId in userIds) Settings.NotifySettingsChanged(userId);

            return Json(new { success = true, overwrite, usersAffected, orphansDeleted, liveRefreshDeliveries });
        }

        /// <summary>
        /// Copies one user's saved Moonfin settings onto other users. Merge keeps each target's values
        /// for whatever the copy leaves out, replace makes every copied profile an exact copy. API keys
        /// and hidden items always stay with their owner.
        /// </summary>
        public async Task<object> Post(CopySettingsRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { error = "Settings sync is disabled" });

            var body = await MoonfinJson.ReadBodyAsync<CopySettingsBody>(request.RequestStream).ConfigureAwait(false);
            if (body == null) return Json(400, new { error = "A request body is required" });

            Guid.TryParse(body.FromUserId, out var fromUserId);
            var targets = new List<Guid>();
            foreach (var raw in body.ToUserIds ?? new List<string>())
            {
                if (Guid.TryParse(raw, out var id) && id != Guid.Empty && id != fromUserId && !targets.Contains(id)) targets.Add(id);
            }
            if (fromUserId == Guid.Empty || targets.Count == 0)
                return Json(400, new { error = "A user to copy from and at least one other user to copy to are required" });

            var profiles = ProfilesOrAll(body.Profiles);
            if (profiles == null) return Json(400, new { error = UnknownProfileError });

            if (body.Replace && body.Keys != null)
                return Json(400, new { error = "Replace copies whole profiles, so it can't take a list of settings" });

            if (await Settings.GetUserSettingsAsync(fromUserId).ConfigureAwait(false) == null)
                return Json(404, new { error = "That user has no saved Moonfin settings" });

            var usersAffected = await Settings.CopySettingsAsync(fromUserId, targets, profiles, body.Keys, body.Replace).ConfigureAwait(false);
            return Json(new { success = true, replace = body.Replace, usersAffected });
        }

        private static readonly string UnknownProfileError =
            "Unknown profile. Valid profiles: " + string.Join(", ", MoonfinUserSettings.ValidProfiles);

        // The requested profile names, every profile when none were given, or null when one isn't valid.
        private static List<string>? ProfilesOrAll(List<string>? requested)
        {
            if (requested == null || requested.Count == 0) return MoonfinUserSettings.ValidProfiles.ToList();
            return requested.All(p => Array.Exists(MoonfinUserSettings.ValidProfiles, v => string.Equals(v, p, StringComparison.OrdinalIgnoreCase))) ? requested : null;
        }

        public async Task<object> Post(BroadcastRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { Error = "Settings sync is disabled" });

            var body = await MoonfinJson.ReadBodyAsync<BroadcastBody>(request.RequestStream).ConfigureAwait(false);
            // Sanitize up front so the live websocket text, the push text and the stored
            // copy all agree.
            var message = XmlText.Sanitize(body?.Message).Trim();
            if (string.IsNullOrWhiteSpace(message)) return Json(400, new { Error = "message is required" });

            var deliveries = Settings.BroadcastMessage(message);

            // Also save it, so users who were not connected still find it in the app. Older
            // clients keep reading the "adminMessage" event above and ignore this.
            var config = Plugin.Instance?.Configuration;
            if (Plugin.Instance != null && config != null)
            {
                var stored = new ServerMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Body = message,
                    Color = ServerMessage.ColorWhite,
                    Delivery = ServerMessage.DeliveryPopup,
                    Audience = ServerMessage.AudienceAll,
                    CreatedUtc = DateTime.UtcNow,
                    CreatedByUserId = AuthHelpers.GetCurrentUserId(Request, _authContext)?.ToString("N")
                };

                stored.Sanitize();

                config.Messages.Add(stored);
                ServerMessage.Prune(config.Messages);
                Plugin.Instance.UpdateConfiguration(config);
                Settings.BroadcastSystemEvent("messagesChanged");
            }

            // The websocket only reaches clients that are open right now, so push
            // covers backgrounded and killed apps. The route opens the messages window
            // on tap.
            var pushTargets = 0;
            var store = Plugin.Instance?.NotificationStore;
            var pushDelivery = Plugin.Instance?.PushDelivery;
            if (store != null && pushDelivery != null)
            {
                foreach (var userId in store.GetUsersWithDevices())
                {
                    pushDelivery.QueueToUser(userId, "Message from your server", message, route: "messages");
                    pushTargets++;
                }
            }

            return Json(new { Success = true, Deliveries = deliveries, PushTargets = pushTargets });
        }

        public object Get(GetGenresRequest request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return Json(401, new { Error = "User not authenticated" });

            var lm = LibraryManager;
            if (lm == null) return Json(new { Items = Array.Empty<object>() });

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Genre" },
                Recursive = true,
                User = user
            };

            var genres = lm.GetItemsResult(query).Items
                .Where(g => !string.IsNullOrWhiteSpace(g.Name) && g.IsVisible(user))
                .Select(g => new { Id = g.Id.ToString("N"), Name = g.Name })
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Json(new { Items = genres });
        }

        // Checks server-side write permissions for libraries that save local metadata, so the client
        // can warn before attempting artwork/metadata writes. Admin-only (see route DTO).
        public object Get(CheckLibrariesWriteAccessRequest request)
        {
            var report = new List<LibraryWriteAccessReport>();
            var lm = LibraryManager;
            if (lm == null) return Json(report);

            foreach (var folder in lm.GetVirtualFolders())
            {
                var options = folder.LibraryOptions;
                if (options == null || !options.SaveLocalMetadata) continue;

                var failedPaths = new List<string>();
                foreach (var location in folder.Locations ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(location)) continue;

                    var testFile = System.IO.Path.Combine(location, $"moonfin_write_test_{Guid.NewGuid():N}.tmp");
                    try
                    {
                        System.IO.File.WriteAllText(testFile, "test");
                        System.IO.File.Delete(testFile);
                    }
                    catch
                    {
                        failedPaths.Add(location);
                    }
                }

                if (failedPaths.Count > 0)
                {
                    report.Add(new LibraryWriteAccessReport
                    {
                        LibraryId = folder.ItemId ?? string.Empty,
                        LibraryName = folder.Name ?? string.Empty,
                        FailedPaths = failedPaths
                    });
                }
            }

            return Json(report);
        }

        public async Task<object> Get(GetMediaBarRequest request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (user == null) return Json(401, new { Error = "User not authenticated" });

            var profile = request.Profile ?? "global";
            MoonfinSettingsProfile? resolved = null;
            try
            {
                resolved = await Settings.GetResolvedProfileAsync(user.Id, profile).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn("Failed to read settings for user " + user.Id + " in GetMediaBar; falling back to random items: " + ex.Message);
            }

            var isFallback = resolved == null;
            var settings = resolved ?? Settings.AdminDefaultsFor(profile) ?? new MoonfinSettingsProfile();

            var sourceType = isFallback ? "random" : (settings.MediaBarSourceType ?? "random");
            var limit = isFallback ? 5 : (settings.MediaBarItemCount ?? 10);
            var excludedNames = ResolveExcludedGenreNames(settings.MediaBarExcludedGenres);

            if (LibraryManager == null)
                return Json(new { Items = Array.Empty<object>(), TotalRecordCount = 0 });

            List<BaseItem> items;
            // The picked libraries and collections draw as one pool whatever the source type, and the
            // library and collection sources from before fall through to the random draw.
            var parentIds = isFallback
                ? null
                : (settings.MediaBarLibraryIds ?? new List<string>()).Concat(settings.MediaBarCollectionIds ?? new List<string>()).ToList();
            var hideWatchedInRecent = !isFallback && settings.HideWatchedInRecentRows == true;
            if (sourceType == "recentlyAdded")
                items = QueryItems(parentIds, limit, user, excludedNames, ItemSortBy.DateCreated, hideWatchedInRecent);
            else if (sourceType == "recentlyReleased")
                items = QueryItems(parentIds, limit, user, excludedNames, ItemSortBy.PremiereDate, hideWatchedInRecent);
            else if (sourceType == "newReleases")
                items = QueryNewReleaseSeries(parentIds, settings.MediaBarNewReleaseDays ?? 30, limit, user, excludedNames);
            else
                items = QueryItems(parentIds, limit, user, excludedNames);

            // When nothing that aired in the window has a backdrop, the bar shows random titles instead, the way the app does.
            if (sourceType == "newReleases" && !items.Any(HasBackdropImage))
                items = QueryItems(parentIds, limit, user, excludedNames);

            var dtos = items.Where(HasBackdropImage).Select(i => MapItemToDto(i, user)).ToList();
            return Json(new { Items = dtos, TotalRecordCount = dtos.Count });
        }

        public async Task<object> Get(GetSeerrConfigRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            MoonfinUserSettings? userSettings = null;
            if (userId != null)
                userSettings = await Settings.GetUserSettingsAsync(userId.Value).ConfigureAwait(false);

            var displayName = string.IsNullOrWhiteSpace(config?.SeerrDisplayName) ? "Seerr" : config!.SeerrDisplayName;
            var userEnabled = userSettings?.Global?.SeerrEnabled ?? userSettings?.SeerrEnabled ?? true;

            return Json(new
            {
                Enabled = config?.SeerrEnabled ?? false,
                Url = config?.SeerrUrl,
                DisplayName = displayName,
                Variant = "seerr",
                UserEnabled = userEnabled
            });
        }

        public object Get(DiscoveryRequest request)
        {
            Request.Response.AddHeader("Cache-Control", "no-store, no-cache, must-revalidate");
            Request.Response.AddHeader("Pragma", "no-cache");

            var address = Http.BaseUrl(Request);
            return Json(new object[]
            {
                new { id = "emby-local", name = "Emby", address, type = "Emby" }
            });
        }

        private bool ReadBoolQuery(string name)
        {
            var v = Request.QueryString[name];
            return !string.IsNullOrEmpty(v) && (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        }


        /// <summary>
        /// Queries visible Movies/Series, optionally constrained to the given container GUIDs, with
        /// genre-name exclusion. Shuffled, or newest first by <paramref name="orderBy"/>. A release date
        /// still to come isn't recently released.
        /// </summary>
        private List<BaseItem> QueryItems(List<string>? containerGuids, int limit, User user, HashSet<string>? excludedNames, string? orderBy = null, bool excludePlayed = false)
        {
            var lm = LibraryManager;
            if (lm == null) return new List<BaseItem>();

            var candidates = Math.Max(limit * 4, 50);
            // Excluded genres are dropped after the query, so a newest-first list reads further ahead to still fill up.
            if (orderBy != null && excludedNames != null) candidates *= 5;

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Series" },
                Recursive = true,
                IsVirtualItem = false,
                User = user,
                Limit = candidates
            };
            if (excludePlayed) query.IsPlayed = false;

            if (containerGuids != null && containerGuids.Count > 0)
            {
                var ancestorIds = ResolveInternalIds(containerGuids, user);
                if (ancestorIds.Length == 0) return new List<BaseItem>();
                query.AncestorIds = ancestorIds;
            }

            if (orderBy != null)
            {
                query.OrderBy = new[] { (orderBy, SortOrder.Descending) };
                if (orderBy == ItemSortBy.PremiereDate) query.MaxPremiereDate = DateTimeOffset.UtcNow;
            }

            var items = lm.GetItemsResult(query).Items
                .Where(i => i.IsVisible(user) && HasBackdropImage(i))
                .ToList();

            IEnumerable<BaseItem> seq = items;
            if (excludedNames != null)
                seq = seq.Where(i => i.Genres == null || !i.Genres.Any(excludedNames.Contains));

            var list = seq.ToList();
            if (orderBy != null) return list.Take(limit).ToList();

            var rng = new Random();
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
            return list.Take(limit).ToList();
        }

        /// <summary>
        /// Series with an episode on the server that aired within the last <paramref name="days"/> days,
        /// newest first. A series' own premiere date is its first season's, so the episodes are queried
        /// and brought back to their series.
        /// </summary>
        private List<BaseItem> QueryNewReleaseSeries(List<string>? libraryGuids, int days, int limit, User user, HashSet<string>? excludedNames)
        {
            var lm = LibraryManager;
            if (lm == null) return new List<BaseItem>();

            // The window comes from synced settings, so keep it to the 7 to 90 days the apps offer.
            days = Math.Clamp(days, 7, 90);
            var now = DateTimeOffset.UtcNow;
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                Recursive = true,
                IsVirtualItem = false,
                User = user,
                MinPremiereDate = now.AddDays(-days),
                MaxPremiereDate = now,
                OrderBy = new[] { (ItemSortBy.PremiereDate, SortOrder.Descending) },
                Limit = 300
            };

            if (libraryGuids != null && libraryGuids.Count > 0)
            {
                var ancestorIds = ResolveInternalIds(libraryGuids, user);
                if (ancestorIds.Length == 0) return new List<BaseItem>();
                query.AncestorIds = ancestorIds;
            }

            var seen = new HashSet<long>();
            var series = new List<BaseItem>();
            // Hidden or excluded series, or ones without a backdrop, can use up a whole page of episodes, so a few more pages are read before giving up.
            for (var page = 0; page < 3 && series.Count < limit; page++)
            {
                query.StartIndex = page * 300;
                var episodes = lm.GetItemsResult(query).Items;
                foreach (var episode in episodes.OfType<Episode>())
                {
                    if (!seen.Add(episode.SeriesId)) continue;
                    var item = lm.GetItemById(episode.SeriesId);
                    if (item == null || !item.IsVisible(user) || !HasBackdropImage(item)) continue;
                    if (excludedNames != null && item.Genres != null && item.Genres.Any(excludedNames.Contains)) continue;
                    series.Add(item);
                    if (series.Count >= limit) break;
                }

                if (episodes.Length < 300) break;
            }

            return series;
        }

        /// <summary>Resolves client GUID strings to Emby internal (long) ids, dropping items the user can't see.</summary>
        private long[] ResolveInternalIds(List<string> guidStrings, User user)
        {
            var lm = LibraryManager;
            if (lm == null) return Array.Empty<long>();

            var ids = new List<long>();
            foreach (var s in guidStrings)
            {
                if (!Guid.TryParse(s, out var g)) continue;
                var item = lm.GetItemById(g);
                if (item == null || !item.IsVisible(user)) continue;
                ids.Add(item.InternalId);
            }
            return ids.ToArray();
        }

        private HashSet<string>? ResolveExcludedGenreNames(List<string>? excludedGenreIds)
        {
            if (excludedGenreIds == null || excludedGenreIds.Count == 0) return null;
            var lm = LibraryManager;
            if (lm == null) return null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in excludedGenreIds)
            {
                if (!Guid.TryParse(s, out var g)) continue;
                var genre = lm.GetItemById(g);
                if (genre != null && !string.IsNullOrWhiteSpace(genre.Name)) names.Add(genre.Name);
            }
            return names.Count > 0 ? names : null;
        }

        private static int ResolveBackdropBlur(MoonfinSettingsProfile profile)
        {
            if (profile.DetailsBackdropBlur.HasValue) return profile.DetailsBackdropBlur.Value;
            if (!string.IsNullOrWhiteSpace(profile.DetailsScreenBlur) && int.TryParse(profile.DetailsScreenBlur, out var v)) return v;
            return 0;
        }

        private static bool HasBackdropImage(BaseItem item) => item.GetImageInfo(ImageType.Backdrop, 0) != null;

        /// <summary>
        /// A trimmed BaseItemDto for a card. With a user it carries their played state too, which
        /// cards read for the checkmark and the resume bar.
        /// </summary>
        internal static object MapItemToDto(BaseItem item, User? user)
        {
            // Every image a card can ask for. Without Thumb and Banner a row set to either falls
            // back to the backdrop even when the library has the real art.
            var imageTags = new Dictionary<string, string>();
            foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner, ImageType.Logo })
            {
                var info = item.GetImageInfo(type, 0);
                if (info != null) imageTags[type.ToString()] = info.DateModified.Ticks.ToString("X");
            }

            var backdropTags = new List<string>();
            foreach (var bd in item.GetImages(ImageType.Backdrop))
                backdropTags.Add(bd.DateModified.Ticks.ToString("X"));

            object? userData = null;
            var userDataManager = PluginServices.UserDataManager;
            if (user != null && userDataManager != null)
            {
                try { userData = userDataManager.GetUserDataDto(item, user); }
                catch { /* a card without played state beats no card */ }
            }

            // PascalCase because clients read these as a BaseItemDto, the
            // shape the core item APIs return.
            return new
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.GetType().Name,
                ProductionYear = item.ProductionYear,
                OfficialRating = item.OfficialRating,
                RunTimeTicks = item.RunTimeTicks,
                Genres = item.Genres,
                Overview = item.Overview,
                CommunityRating = item.CommunityRating,
                CriticRating = item.CriticRating,
                ImageTags = imageTags,
                BackdropImageTags = backdropTags,
                UserData = userData
            };
        }

    }
}
