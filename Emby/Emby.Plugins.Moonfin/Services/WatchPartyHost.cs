using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using Moonfin.WatchParty;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>What the shared watch party engine needs from Emby: item access, names and alerts.</summary>
    public class WatchPartyHost : IWatchPartyHost
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly MoonfinSettingsService _settings;
        private readonly PushDeliveryService _push;
        private readonly ILogger _logger;

        public WatchPartyHost(ILibraryManager libraryManager, IUserManager userManager, MoonfinSettingsService settings, PushDeliveryService push, ILogger logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _settings = settings;
            _push = push;
            _logger = logger;
        }

        public static WatchPartySettings Settings()
        {
            var c = Plugin.Instance?.Configuration;
            return c == null
                ? new WatchPartySettings()
                : new WatchPartySettings
                {
                    Enabled = c.WatchPartiesEnabled,
                    Chat = c.WatchPartyChat,
                    Reactions = c.WatchPartyReactions,
                    Alerts = c.WatchPartyAlerts,
                    EveryoneControl = c.WatchPartyEveryoneControl,
                    MaxMembers = c.WatchPartyMaxMembers,
                };
        }

        /// <summary>Library access and parental ratings both come from the item's visibility for the user.</summary>
        public WatchPartyItem? FindItem(string userId, string contentId)
        {
            var user = Guid.TryParse(userId, out var uid) ? _userManager.GetUserById(uid) : null;
            var item = user == null ? null : Lookup(contentId);
            if (item == null || item is Folder || !item.IsVisibleStandalone(user))
            {
                return null;
            }

            var episode = item as Episode;
            return new WatchPartyItem
            {
                Id = item.InternalId.ToString(CultureInfo.InvariantCulture),
                Title = item.Name ?? string.Empty,
                Type = item.GetType().Name,
                SeriesName = episode?.SeriesName,
                Season = episode?.ParentIndexNumber,
                Episode = episode?.IndexNumber,
                Year = item.ProductionYear,
                RuntimeSeconds = item.RunTimeTicks is long ticks && ticks > 0 ? ticks / 10_000_000.0 : (double?)null,
            };
        }

        public WatchPartyProfile Profile(string userId)
        {
            var user = Guid.TryParse(userId, out var uid) ? _userManager.GetUserById(uid) : null;
            return new WatchPartyProfile
            {
                DisplayName = user?.Name ?? "Someone",
                AvatarUrl = user != null && user.HasImage(ImageType.Primary, 0) ? "/Users/" + userId + "/Images/Primary" : null,
            };
        }

        public void Announce(WatchPartyAnnouncement announcement) => _ = Task.Run(async () =>
        {
            try
            {
                var skip = new HashSet<string>(announcement.MemberUserIds) { announcement.HostUserId };
                foreach (var user in _userManager.GetUserList(new UserQuery()))
                {
                    var id = user.Id.ToString("N");
                    bool wantsAlerts;
                    try
                    {
                        wantsAlerts = await WantsAlerts(user.Id).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.Debug("Could not load settings for user " + user.Id + " during watch party announce: " + ex.Message);
                        continue;
                    }

                    if (skip.Contains(id) || !wantsAlerts || FindItem(id, announcement.ContentId) == null)
                    {
                        continue;
                    }

                    _push.QueueToUser(user.Id, announcement.Title, announcement.Body, announcement.Route);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("Watch party alerts failed: " + ex.Message);
            }
        });

        private async Task<bool> WantsAlerts(Guid userId)
        {
            var profile = await _settings.GetResolvedProfileAsync(userId, "global").ConfigureAwait(false)
                ?? _settings.AdminDefaultsFor("global");
            return profile?.WatchPartyAlerts == true;
        }

        /// <summary>Emby clients send either the internal number or the guid.</summary>
        private BaseItem? Lookup(string id)
        {
            try
            {
                if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId))
                {
                    return internalId > 0 ? _libraryManager.GetItemById(internalId) : null;
                }

                return Guid.TryParse(id, out var guid) ? _libraryManager.GetItemById(guid) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
