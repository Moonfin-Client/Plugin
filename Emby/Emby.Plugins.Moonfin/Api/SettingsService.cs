using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class SettingsService : IService, IRequiresRequest, IHasResultFactory
    {
        private const int MaxDetailsBackdropBlur = 40;

        private readonly IAuthorizationContext _authContext;


        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private MoonfinSettingsService Settings => Plugin.Instance?.SettingsService
            ?? throw new InvalidOperationException("MoonfinSettingsService not initialized");

        private ILibraryManager? LibraryManager => PluginServices.LibraryManager;

        public SettingsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
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
                // Legacy field names so clients from before the Seerr rename keep reading these.
                jellyseerrEnabled = seerrEnabled,
                jellyseerrUrl = seerrUrl,
                mdblistAvailable = !string.IsNullOrWhiteSpace(config?.MdblistApiKey),
                tmdbAvailable = !string.IsNullOrWhiteSpace(config?.TmdbApiKey),
                // Older plugins leave this out, which the app reads as false and hides the
                // messages button.
                messagesSupported = true,
                defaultSettings = config?.DefaultUserSettings
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
                var adminDefaults = config.DefaultUserSettings;
                if (adminDefaults != null) return Json(adminDefaults);
                return Json(404, new { Error = "No settings found" });
            }
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
                ?? Plugin.Instance?.Configuration?.DefaultUserSettings
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
                ?? Plugin.Instance?.Configuration?.DefaultUserSettings
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

        public async Task<object> Post(PushDefaultsRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { error = "Settings sync is disabled" });

            var defaults = Plugin.Instance?.Configuration?.DefaultUserSettings;
            if (defaults == null) return Json(400, new { error = "No default user settings configured" });

            var overwrite = request.Overwrite || ReadBoolQuery("overwrite");

            int usersAffected;
            var orphansDeleted = 0;

            if (overwrite)
            {
                var serverUserIds = AuthHelpers.GetAllServerUserIds();
                if (serverUserIds == null)
                    return Json(503, new { error = "User manager unavailable" });

                (usersAffected, orphansDeleted) = await Settings.ResetAllUsersToDefaultsAsync(defaults, serverUserIds, deleteOrphans: true).ConfigureAwait(false);
            }
            else
            {
                usersAffected = await Settings.MergeDefaultsToAllUsersAsync(defaults).ConfigureAwait(false);
            }

            var liveRefreshDeliveries = Settings.BroadcastSystemEvent("settingsUpdated");
            return Json(new { success = true, overwrite, usersAffected, orphansDeleted, liveRefreshDeliveries });
        }

        public async Task<object> Post(PushDefaultsToUserRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { error = "Settings sync is disabled" });

            var defaults = Plugin.Instance?.Configuration?.DefaultUserSettings;
            if (defaults == null) return Json(400, new { error = "No default user settings configured" });
            if (!Guid.TryParse(request.UserId, out var userId) || userId == Guid.Empty)
                return Json(400, new { error = "A valid userId is required" });

            var overwrite = request.Overwrite || ReadBoolQuery("overwrite");
            if (overwrite)
                await Settings.ResetUserToDefaultsAsync(userId, defaults).ConfigureAwait(false);
            else
                await Settings.MergeDefaultsToUserAsync(userId, defaults).ConfigureAwait(false);

            return Json(new { success = true, overwrite });
        }

        public async Task<object> Post(BroadcastRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableSettingsSync != true)
                return Json(503, new { Error = "Settings sync is disabled" });

            var body = await MoonfinJson.ReadBodyAsync<BroadcastBody>(request.RequestStream).ConfigureAwait(false);
            var message = body?.Message?.Trim();
            if (string.IsNullOrWhiteSpace(message)) return Json(400, new { Error = "message is required" });

            var deliveries = Settings.BroadcastMessage(message);

            // Also save it, so users who were not connected still find it in the app. Older
            // clients keep reading the "adminMessage" event above and ignore this.
            var config = Plugin.Instance?.Configuration;
            if (Plugin.Instance != null && config != null)
            {
                config.Messages.Add(new ServerMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Body = message,
                    Color = ServerMessage.ColorWhite,
                    Delivery = ServerMessage.DeliveryPopup,
                    Audience = ServerMessage.AudienceAll,
                    CreatedUtc = DateTime.UtcNow,
                    CreatedByUserId = AuthHelpers.GetCurrentUserId(Request, _authContext)?.ToString("N")
                });
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
            var resolved = await Settings.GetResolvedProfileAsync(user.Id, profile).ConfigureAwait(false);
            var isFallback = resolved == null;
            var settings = resolved ?? Plugin.Instance?.Configuration?.DefaultUserSettings ?? new MoonfinSettingsProfile();

            var sourceType = isFallback ? "library" : (settings.MediaBarSourceType ?? "library");
            var limit = isFallback ? 5 : (settings.MediaBarItemCount ?? 10);
            var excludedNames = ResolveExcludedGenreNames(settings.MediaBarExcludedGenres);

            if (LibraryManager == null)
                return Json(new { Items = Array.Empty<object>(), TotalRecordCount = 0 });

            List<BaseItem> items;
            if (sourceType == "collection" && settings.MediaBarCollectionIds != null && settings.MediaBarCollectionIds.Count > 0)
                items = QueryItems(settings.MediaBarCollectionIds, limit, user, excludedNames);
            else
                items = QueryItems(isFallback ? null : settings.MediaBarLibraryIds, limit, user, excludedNames);

            var dtos = items.Where(HasBackdropImage).Select(MapItemToDto).ToList();
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


        /// <summary>Queries random visible Movies/Series, optionally constrained to the given container GUIDs, with genre-name exclusion.</summary>
        private List<BaseItem> QueryItems(List<string>? containerGuids, int limit, User user, HashSet<string>? excludedNames)
        {
            var lm = LibraryManager;
            if (lm == null) return new List<BaseItem>();

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Series" },
                Recursive = true,
                IsVirtualItem = false,
                User = user,
                Limit = Math.Max(limit * 4, 50)
            };

            if (containerGuids != null && containerGuids.Count > 0)
            {
                var ancestorIds = ResolveInternalIds(containerGuids, user);
                if (ancestorIds.Length == 0) return new List<BaseItem>();
                query.AncestorIds = ancestorIds;
            }

            var items = lm.GetItemsResult(query).Items
                .Where(i => i.IsVisible(user))
                .ToList();

            IEnumerable<BaseItem> seq = items;
            if (excludedNames != null)
                seq = seq.Where(i => i.Genres == null || !i.Genres.Any(excludedNames.Contains));

            var list = seq.ToList();
            var rng = new Random();
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
            return list.Take(limit).ToList();
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

        private static object MapItemToDto(BaseItem item)
        {
            var imageTags = new Dictionary<string, string>();
            var primaryInfo = item.GetImageInfo(ImageType.Primary, 0);
            if (primaryInfo != null) imageTags["Primary"] = primaryInfo.DateModified.Ticks.ToString("X");
            var logoInfo = item.GetImageInfo(ImageType.Logo, 0);
            if (logoInfo != null) imageTags["Logo"] = logoInfo.DateModified.Ticks.ToString("X");

            var backdropTags = new List<string>();
            foreach (var bd in item.GetImages(ImageType.Backdrop))
                backdropTags.Add(bd.DateModified.Ticks.ToString("X"));

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
                BackdropImageTags = backdropTags
            };
        }

    }
}
