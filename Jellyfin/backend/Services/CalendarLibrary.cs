using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonfin.Calendar;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Answers the calendar annotation from Jellyfin's library, as seen by one user. Ids are the "N"
/// form Jellyfin's API hands clients.
/// </summary>
internal sealed class JellyfinCalendarLibrary : ICalendarLibrary
{
    private readonly ILibraryManager _libraryManager;
    private readonly object? _user;

    public JellyfinCalendarLibrary(ILibraryManager libraryManager, object? user)
    {
        _libraryManager = libraryManager;
        _user = user;
    }

    public IReadOnlyList<CalendarLibraryFolder> Folders()
    {
        var folders = new List<CalendarLibraryFolder>();
        foreach (var info in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(info.ItemId, out var id))
            {
                continue;
            }

            // A user that can't be resolved sees no library, so their calendar keeps only titles in none.
            var folder = _libraryManager.GetItemById(id);
            var visible = folder != null && _user != null && UserReflection.IsVisibleTo(folder, _user);
            folders.Add(new CalendarLibraryFolder(id.ToString("N"), info.Locations ?? [], visible));
        }

        return folders;
    }

    // HasAnyProviderId holds one value per provider, so each title is its own query.
    public IReadOnlyList<CalendarLibraryItem> FindTitles(bool series, IReadOnlyCollection<CalendarProviderIds> titles)
    {
        var found = new Dictionary<Guid, CalendarLibraryItem>();
        foreach (var ids in titles)
        {
            var wanted = new Dictionary<string, string>();
            if (ids.Tvdb != null) wanted[MetadataProvider.Tvdb.ToString()] = ids.Tvdb;
            if (ids.Tmdb != null) wanted[MetadataProvider.Tmdb.ToString()] = ids.Tmdb;
            if (ids.Imdb != null) wanted[MetadataProvider.Imdb.ToString()] = ids.Imdb;

            var query = new InternalItemsQuery
            {
                HasAnyProviderId = wanted,
                IncludeItemTypes = [series ? BaseItemKind.Series : BaseItemKind.Movie],
                IsVirtualItem = false,
                Recursive = true,
                EnableTotalRecordCount = false
            };

            foreach (var item in _libraryManager.GetItemsResult(query).Items)
            {
                if (!found.ContainsKey(item.Id))
                {
                    var libraries = _libraryManager.GetCollectionFolders(item).Select(f => f.Id.ToString("N")).ToList();
                    found[item.Id] = new CalendarLibraryItem(item.Id.ToString("N"), CalendarReminderNotifier.IdsOf(item), libraries);
                }
            }
        }

        return found.Values.ToList();
    }

    public IReadOnlyList<CalendarLibraryEpisode> FindEpisodes(IReadOnlyCollection<string> seriesIds)
    {
        var ancestors = seriesIds.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty).Where(guid => guid != Guid.Empty).ToArray();
        if (ancestors.Length == 0)
        {
            return [];
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = ancestors,
            IsVirtualItem = false,
            Recursive = true,
            EnableTotalRecordCount = false
        };

        return _libraryManager.GetItemsResult(query).Items
            .OfType<Episode>()
            .Select(e => new CalendarLibraryEpisode(e.Id.ToString("N"), e.SeriesId.ToString("N"), e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd))
            .ToList();
    }
}

/// <summary>
/// Fires calendar reminders when the episode or movie they wait for lands in the library. A fresh
/// scan can add an item before its provider ids or episode numbers are read, so updates are
/// checked too, until one matches. A reminder ignores the user's other notification toggles,
/// since they asked for it.
/// </summary>
public sealed class CalendarReminderNotifier : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly CalendarReminderStore _store;
    private readonly MoonfinSettingsService _settings;
    private readonly PushDeliveryService _push;
    private readonly IServiceProvider _services;
    private readonly ILogger<CalendarReminderNotifier> _logger;

    public CalendarReminderNotifier(
        ILibraryManager libraryManager,
        CalendarReminderStore store,
        MoonfinSettingsService settings,
        PushDeliveryService push,
        IServiceProvider services,
        ILogger<CalendarReminderNotifier> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _settings = settings;
        _push = push;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        return Task.CompletedTask;
    }

    internal static CalendarProviderIds IdsOf(BaseItem item) => new(
        item.GetProviderId(MetadataProvider.Tvdb),
        item.GetProviderId(MetadataProvider.Tmdb),
        item.GetProviderId(MetadataProvider.Imdb));

    private static CalendarArrival? ArrivalOf(BaseItem item) => item switch
    {
        Episode { Series: { } series } episode => new CalendarArrival(true, IdsOf(series), episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd),
        Movie movie => new CalendarArrival(false, IdsOf(movie), null, null, null),
        _ => null
    };

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (item == null || item.IsVirtualItem || _store.IsEmpty || ArrivalOf(item) is not { } arrival)
            {
                return;
            }

            foreach (var (userId, reminder) in _store.Matching(arrival))
            {
                var user = UserReflection.ResolveUser(_services, userId);
                if (user == null || !UserReflection.IsVisibleStandaloneTo(item, user) || !_store.Remove(userId, reminder.Key!))
                {
                    continue;
                }

                var (title, body) = CalendarReminders.Message(reminder);
                var route = "/item/" + item.Id.ToString("N");
                _settings.NotifyUser(userId, JsonSerializer.Serialize(new { type = "seerrNotification", title, body, route }));
                _push.QueueToUser(userId, title, body, route);
                _logger.LogInformation("calendar: reminder {Key} fired for user {UserId}", reminder.Key, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calendar reminder check failed");
        }
    }
}
