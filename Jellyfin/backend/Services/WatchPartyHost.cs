using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Helpers;
using Moonfin.WatchParty;

namespace Moonfin.Server.Services;

/// <summary>What the shared watch party engine needs from Jellyfin: item access, names and alerts.</summary>
public class WatchPartyHost : IWatchPartyHost
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly IUserDirectory _directory;
    private readonly MoonfinSettingsService _settings;
    private readonly PushDeliveryService _push;
    private readonly ILogger<WatchPartyHost> _logger;

    public WatchPartyHost(
        ILibraryManager libraryManager,
        IServiceProvider services,
        IUserDirectory directory,
        MoonfinSettingsService settings,
        PushDeliveryService push,
        ILogger<WatchPartyHost> logger)
    {
        _libraryManager = libraryManager;
        _services = services;
        _directory = directory;
        _settings = settings;
        _push = push;
        _logger = logger;
    }

    public static WatchPartySettings Settings()
    {
        var c = MoonfinPlugin.Instance?.Configuration;
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
        if (!Guid.TryParse(userId, out var uid) || !Guid.TryParse(contentId, out var id))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(id);
        var user = item == null || item.IsFolder ? null : UserReflection.ResolveUser(_services, uid);
        if (item == null || user == null || !UserReflection.IsVisibleStandaloneTo(item, user))
        {
            return null;
        }

        var episode = item as Episode;
        return new WatchPartyItem
        {
            Id = item.Id.ToString("N"),
            Title = item.Name ?? string.Empty,
            Type = item.GetBaseItemKind().ToString(),
            SeriesName = episode?.SeriesName,
            Season = episode?.ParentIndexNumber,
            Episode = episode?.IndexNumber,
            Year = item.ProductionYear,
            RuntimeSeconds = item.RunTimeTicks is long ticks && ticks > 0 ? ticks / 10_000_000.0 : (double?)null,
        };
    }

    public WatchPartyProfile Profile(string userId)
    {
        var user = _directory.Users().FirstOrDefault(u => u.Id.ToString("N") == userId);
        return new WatchPartyProfile
        {
            DisplayName = user?.Name ?? "Someone",
            AvatarUrl = string.IsNullOrEmpty(user?.ImageTag) ? null : $"/Users/{userId}/Images/Primary?tag={user!.ImageTag}",
        };
    }

    public void Announce(WatchPartyAnnouncement announcement) => _ = Task.Run(async () =>
    {
        try
        {
            var skip = new HashSet<string>(announcement.MemberUserIds) { announcement.HostUserId };
            foreach (var user in _directory.Users())
            {
                var id = user.Id.ToString("N");
                bool wantsAlerts;
                try
                {
                    wantsAlerts = await WantsAlerts(user.Id).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not load settings for user {UserId} during watch party announce", user.Id);
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
            _logger.LogWarning(ex, "Watch party alerts failed");
        }
    });

    private async Task<bool> WantsAlerts(Guid userId)
    {
        var profile = await _settings.GetResolvedProfileAsync(userId, "global").ConfigureAwait(false)
            ?? _settings.AdminDefaultsFor("global");
        return profile?.WatchPartyAlerts == true;
    }
}
