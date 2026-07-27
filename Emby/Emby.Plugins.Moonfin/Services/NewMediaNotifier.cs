using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Notifies opted-in users (websocket + push) when new media lands in the library, so closed
    /// apps hear about additions that aren't Seerr requests. Additions are coalesced per movie /
    /// series with a quiet-period flush, and large flushes collapse to a single summary so a full
    /// library scan can't fire hundreds of pushes. Owned by ServerEntryPoint: Start() subscribes,
    /// Dispose() unsubscribes.
    /// </summary>
    public class NewMediaNotifier : IDisposable
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MaxPending = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StartupSuppression = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FlushScanInterval = TimeSpan.FromSeconds(15);
        private const int MaxGroupsPerFlush = 5;

        private readonly ILibraryManager _libraryManager;
        private readonly MoonfinSettingsService _settingsService;
        private readonly NotificationStore _store;
        private readonly PushDeliveryService _pushDelivery;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<Guid, PendingGroup> _pending =
            new ConcurrentDictionary<Guid, PendingGroup>();
        private Timer? _flushTimer;
        private DateTimeOffset _startedAt;
        private bool _started;

        public NewMediaNotifier(
            ILibraryManager libraryManager,
            MoonfinSettingsService settingsService,
            NotificationStore store,
            PushDeliveryService pushDelivery,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _settingsService = settingsService;
            _store = store;
            _pushDelivery = pushDelivery;
            _logger = logger;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _startedAt = DateTimeOffset.UtcNow;
            _libraryManager.ItemAdded += OnItemAdded;
            _flushTimer = new Timer(_ => FlushDueGroups(), null, FlushScanInterval, FlushScanInterval);
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            _libraryManager.ItemAdded -= OnItemAdded;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                // Server boot triggers library scans whose additions are not "new" to the user.
                if (DateTimeOffset.UtcNow - _startedAt < StartupSuppression)
                    return;

                var item = e.Item;
                if (item == null || item.IsVirtualItem || item.Id == Guid.Empty)
                    return;

                var now = DateTimeOffset.UtcNow;
                if (item is Movie movie)
                {
                    _pending.AddOrUpdate(
                        movie.Id,
                        _ => PendingGroup.ForMovie(movie, now),
                        (_, g) => g.Touch(now));
                }
                else if (item is Episode episode && episode.Series != null && episode.Series.Id != Guid.Empty)
                {
                    _pending.AddOrUpdate(
                        episode.Series.Id,
                        _ => PendingGroup.ForSeries(episode, now),
                        (_, g) => g.Touch(now));
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("New-media event handling failed: " + ex.Message);
            }
        }

        private void FlushDueGroups()
        {
            if (_pending.IsEmpty)
                return;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var due = new List<PendingGroup>();
                foreach (var pair in _pending)
                {
                    var group = pair.Value;
                    if (now - group.LastSeen >= QuietPeriod || now - group.FirstSeen >= MaxPending)
                    {
                        if (_pending.TryRemove(pair.Key, out var removed))
                            due.Add(removed);
                    }
                }

                if (due.Count == 0)
                    return;

                var recipients = _store.GetUsersWantingNewMedia().ToList();
                if (recipients.Count == 0)
                    return;

                if (due.Count > MaxGroupsPerFlush)
                {
                    var total = due.Sum(g => g.Count);
                    var body = total + " new items were added to the library";
                    foreach (var userId in recipients)
                    {
                        // No per-item visibility on the collapsed summary: it names nothing.
                        DeliverToUser(userId, "New in library", body, route: "");
                    }

                    _logger.Info("newMedia: collapsed " + due.Count + " groups (" + total +
                        " items) into a summary for " + recipients.Count + " users");
                    return;
                }

                // Resolve each recipient once instead of per group, since the lookup
                // goes through the user manager every time.
                var resolved = recipients
                    .Select(id => new { Id = id, User = PluginServices.UserManager?.GetUserById(id) })
                    .ToList();

                foreach (var group in due)
                {
                    var composed = group.Compose();
                    foreach (var entry in resolved)
                    {
                        if (!IsVisibleToUser(group.Item, entry.User))
                            continue;

                        DeliverToUser(entry.Id, composed.Title, composed.Body, composed.Route);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("New-media flush failed: " + ex.Message);
            }
        }

        private void DeliverToUser(Guid userId, string title, string body, string route)
        {
            // Same event shape as Seerr notifications so the client banner path and
            // notification tap routing need zero changes.
            var json = JsonSerializer.Serialize(new
            {
                type = "seerrNotification",
                title,
                body,
                route
            });

            _settingsService.NotifyUser(userId, json);
            _pushDelivery.QueueToUser(userId, title, body, route);
        }

        private static bool IsVisibleToUser(BaseItem? item, User? user)
        {
            if (item == null)
                return true;
            if (user == null)
                return false;

            try
            {
                return item.IsVisible(user);
            }
            catch
            {
                // Fail open so a check we can't complete doesn't silently drop
                // the notification.
                return true;
            }
        }

        private sealed class PendingGroup
        {
            public string Kind { get; private set; } = string.Empty;
            public string Name { get; private set; } = string.Empty;
            public int? Year { get; private set; }
            public Guid RouteId { get; private set; }
            public BaseItem? Item { get; private set; }
            public int Count { get; private set; } = 1;
            public DateTimeOffset FirstSeen { get; private set; }
            public DateTimeOffset LastSeen { get; private set; }

            public static PendingGroup ForMovie(Movie movie, DateTimeOffset now) => new PendingGroup
            {
                Kind = "movie",
                Name = movie.Name ?? "Unknown",
                Year = movie.ProductionYear,
                RouteId = movie.Id,
                Item = movie,
                FirstSeen = now,
                LastSeen = now
            };

            public static PendingGroup ForSeries(Episode episode, DateTimeOffset now) => new PendingGroup
            {
                Kind = "series",
                Name = episode.SeriesName ?? "Unknown",
                RouteId = episode.Series?.Id ?? Guid.Empty,
                Item = episode,
                FirstSeen = now,
                LastSeen = now
            };

            public PendingGroup Touch(DateTimeOffset now)
            {
                Count++;
                LastSeen = now;
                return this;
            }

            public (string Title, string Body, string Route) Compose()
            {
                // "N" format matches the routes SeerrWebhookService emits.
                var route = "/item/" + RouteId.ToString("N");
                if (Kind == "movie")
                {
                    var year = Year.HasValue ? " (" + Year.Value + ")" : string.Empty;
                    return ("New in library", Name + year + " was added", route);
                }

                var body = Count == 1
                    ? Name + ": 1 new episode"
                    : Name + ": " + Count + " new episodes";
                return ("New in library", body, route);
            }
        }
    }
}
