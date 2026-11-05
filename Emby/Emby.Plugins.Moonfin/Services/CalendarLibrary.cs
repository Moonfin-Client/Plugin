using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Moonfin.Calendar;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Answers the calendar annotation from Emby's library, as seen by one user. Ids are Emby's
    /// numeric item ids, which is what clients see.
    /// </summary>
    internal sealed class EmbyCalendarLibrary : ICalendarLibrary
    {
        private const int ProviderIdChunk = 100;

        private readonly ILibraryManager _libraryManager;
        private readonly User? _user;

        public EmbyCalendarLibrary(ILibraryManager libraryManager, User? user)
        {
            _libraryManager = libraryManager;
            _user = user;
        }

        public IReadOnlyList<CalendarLibraryFolder> Folders()
        {
            var folders = new List<CalendarLibraryFolder>();
            foreach (var info in _libraryManager.GetVirtualFolders())
            {
                var folder = FindItem(info.ItemId);
                if (folder == null) continue;

                // A user that can't be resolved sees no library, so their calendar keeps only titles in none.
                var visible = _user != null && folder.IsVisible(_user);
                folders.Add(new CalendarLibraryFolder(IdOf(folder), info.Locations ?? Array.Empty<string>(), visible));
            }

            return folders;
        }

        public IReadOnlyList<CalendarLibraryItem> FindTitles(bool series, IReadOnlyCollection<CalendarProviderIds> titles)
        {
            var pairs = titles
                .SelectMany(ids => new[] { ("Tvdb", ids.Tvdb), ("Tmdb", ids.Tmdb), ("Imdb", ids.Imdb) })
                .Where(pair => pair.Item2 != null)
                .Select(pair => new KeyValuePair<string, string>(pair.Item1, pair.Item2!))
                .Distinct()
                .ToList();

            var found = new Dictionary<long, CalendarLibraryItem>();
            for (var i = 0; i < pairs.Count; i += ProviderIdChunk)
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { series ? "Series" : "Movie" },
                    IsVirtualItem = false,
                    Recursive = true,
                    AnyProviderIdEquals = pairs.Skip(i).Take(ProviderIdChunk).ToList()
                };

                foreach (var item in _libraryManager.GetItemsResult(query).Items)
                {
                    if (found.ContainsKey(item.InternalId)) continue;
                    var libraries = _libraryManager.GetCollectionFolders(item).Select(IdOf).ToList();
                    found[item.InternalId] = new CalendarLibraryItem(IdOf(item), CalendarReminderNotifier.IdsOf(item), libraries);
                }
            }

            return found.Values.ToList();
        }

        public IReadOnlyList<CalendarLibraryEpisode> FindEpisodes(IReadOnlyCollection<string> seriesIds)
        {
            var ancestors = seriesIds
                .Select(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId) ? internalId : 0)
                .Where(id => id > 0)
                .ToArray();
            if (ancestors.Length == 0) return new List<CalendarLibraryEpisode>();

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                AncestorIds = ancestors,
                IsVirtualItem = false,
                Recursive = true
            };

            return _libraryManager.GetItemsResult(query).Items
                .OfType<Episode>()
                .Select(e => new CalendarLibraryEpisode(IdOf(e), e.SeriesId.ToString(CultureInfo.InvariantCulture), e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd))
                .ToList();
        }

        internal static string IdOf(BaseItem item) => item.InternalId.ToString(CultureInfo.InvariantCulture);

        private BaseItem? FindItem(string? id)
        {
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId)) return _libraryManager.GetItemById(internalId);
            return Guid.TryParse(id, out var guid) ? _libraryManager.GetItemById(guid) : null;
        }
    }

    /// <summary>
    /// Fires calendar reminders when the episode or movie they wait for lands in the library. A fresh
    /// scan can add an item before its provider ids or episode numbers are read, so updates are
    /// checked too, until one matches. A reminder ignores the user's other notification toggles,
    /// since they asked for it. Owned by ServerEntryPoint: Start() subscribes, Dispose() unsubscribes.
    /// </summary>
    public sealed class CalendarReminderNotifier : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly CalendarReminderStore _store;
        private readonly MoonfinSettingsService _settings;
        private readonly PushDeliveryService _push;
        private readonly ILogger _logger;
        private bool _started;

        public CalendarReminderNotifier(
            ILibraryManager libraryManager,
            IUserManager userManager,
            CalendarReminderStore store,
            MoonfinSettingsService settings,
            PushDeliveryService push,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _store = store;
            _settings = settings;
            _push = push;
            _logger = logger;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
        }

        internal static CalendarProviderIds IdsOf(BaseItem item) =>
            new CalendarProviderIds(ProviderId(item, "Tvdb"), ProviderId(item, "Tmdb"), ProviderId(item, "Imdb"));

        private static string? ProviderId(BaseItem item, string name)
        {
            string? value = null;
            item.ProviderIds?.TryGetValue(name, out value);
            return value;
        }

        private static CalendarArrival? ArrivalOf(BaseItem item)
        {
            if (item is Episode episode && episode.Series is Series series)
                return new CalendarArrival(true, IdsOf(series), episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd);
            return item is Movie movie ? new CalendarArrival(false, IdsOf(movie), null, null, null) : null;
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null || item.IsVirtualItem || _store.IsEmpty) return;

                var arrival = ArrivalOf(item);
                if (arrival == null) return;

                foreach (var match in _store.Matching(arrival))
                {
                    var user = _userManager.GetUserById(match.UserId);
                    if (user == null || !item.IsVisibleStandalone(user) || !_store.Remove(match.UserId, match.Reminder.Key!)) continue;

                    var message = CalendarReminders.Message(match.Reminder);
                    var route = "/item/" + EmbyCalendarLibrary.IdOf(item);
                    _settings.NotifyUser(match.UserId, JsonSerializer.Serialize(new
                    {
                        type = "seerrNotification",
                        title = message.Title,
                        body = message.Body,
                        route
                    }));
                    _push.QueueToUser(match.UserId, message.Title, message.Body, route);
                    _logger.Info("{0}", "calendar: reminder " + match.Reminder.Key + " fired for user " + match.UserId);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}", "Calendar reminder check failed: " + ex.Message);
            }
        }
    }
}
