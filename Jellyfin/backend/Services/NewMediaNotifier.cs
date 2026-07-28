using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Notifies opted-in users (SSE + push) when new media lands in the library, so closed apps
/// hear about additions that aren't Seerr requests. Additions are coalesced per movie /
/// series with a quiet-period flush so a season drop or import burst produces one
/// notification instead of dozens, and large flushes collapse to a single summary so a
/// full library scan can't fire hundreds of pushes.
/// </summary>
public class NewMediaNotifier : IHostedService, IDisposable
{
    // Flush a group once it has been quiet this long...
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);

    // ...or unconditionally once it has been pending this long (continuous imports).
    private static readonly TimeSpan MaxPending = TimeSpan.FromMinutes(5);

    // Ignore events for a window after startup: server boot triggers library scans whose
    // additions are not "new" to the user.
    private static readonly TimeSpan StartupSuppression = TimeSpan.FromMinutes(5);

    // More groups than this in a single flush collapses into one summary notification.
    private const int MaxGroupsPerFlush = 5;

    private static readonly TimeSpan FlushScanInterval = TimeSpan.FromSeconds(15);

    // The user manager and the user entity have moved between Jellyfin versions, so
    // they are resolved by name to keep one DLL working across them.
    private static readonly Type? _userManagerType =
        Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");
    private static readonly MethodInfo? _userManagerGetUserById =
        _userManagerType?.GetMethod("GetUserById", [typeof(Guid)]);
    private static readonly MethodInfo? _baseItemIsVisible = typeof(BaseItem)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m => m.Name == "IsVisible" && m.GetParameters().Length == 1);
    private static readonly Type? _baseItemIsVisibleUserType =
        _baseItemIsVisible?.GetParameters()[0].ParameterType;

    private readonly ILibraryManager _libraryManager;
    private readonly MoonfinSettingsService _settingsService;
    private readonly NotificationStore _store;
    private readonly PushDeliveryService _pushDelivery;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NewMediaNotifier> _logger;

    private readonly ConcurrentDictionary<Guid, PendingGroup> _pending = new();
    private Timer? _flushTimer;
    private DateTimeOffset _startedAt;

    public NewMediaNotifier(
        ILibraryManager libraryManager,
        MoonfinSettingsService settingsService,
        NotificationStore store,
        PushDeliveryService pushDelivery,
        IServiceProvider serviceProvider,
        ILogger<NewMediaNotifier> logger)
    {
        _libraryManager = libraryManager;
        _settingsService = settingsService;
        _store = store;
        _pushDelivery = pushDelivery;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedAt = DateTimeOffset.UtcNow;
        _libraryManager.ItemAdded += OnItemAdded;
        _flushTimer = new Timer(_ => FlushDueGroups(), null, FlushScanInterval, FlushScanInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _flushTimer?.Dispose();
        _flushTimer = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (DateTimeOffset.UtcNow - _startedAt < StartupSuppression)
            {
                return;
            }

            var item = e.Item;
            if (item == null || item.IsVirtualItem || item.Id == Guid.Empty)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            switch (item)
            {
                case Movie movie:
                    _pending.AddOrUpdate(
                        movie.Id,
                        _ => PendingGroup.ForMovie(movie, now),
                        (_, g) => g.Touch(now));
                    break;

                case Episode episode when episode.SeriesId != Guid.Empty:
                    _pending.AddOrUpdate(
                        episode.SeriesId,
                        _ => PendingGroup.ForSeries(episode, now),
                        (_, g) => g.Touch(now));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-media event handling failed");
        }
    }

    private void FlushDueGroups()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

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
                    {
                        due.Add(removed);
                    }
                }
            }

            if (due.Count == 0)
            {
                return;
            }

            // The one line that says the notifier ran and whether anyone asked for it,
            // since every way this can end quietly happens after this point.
            var recipients = _store.GetUsersWantingNewMedia().ToList();
            _logger.LogInformation(
                "newMedia: {Groups} groups ready for {Users} opted in users",
                due.Count, recipients.Count);
            if (recipients.Count == 0)
            {
                return;
            }

            if (due.Count > MaxGroupsPerFlush)
            {
                var total = due.Sum(g => g.Count);
                var title = "New in library";
                var body = $"{total} new items were added to the library";
                foreach (var userId in recipients)
                {
                    // No per-item visibility on the collapsed summary: it names nothing.
                    DeliverToUser(userId, title, body, route: "");
                }

                _logger.LogInformation(
                    "newMedia: collapsed {Groups} groups ({Items} items) into a summary for {Users} users",
                    due.Count, total, recipients.Count);
                return;
            }

            // Resolve each recipient once instead of per group, since the lookup
            // goes through the user manager every time.
            var resolved = recipients.Select(id => (Id: id, User: ResolveUser(id))).ToList();

            foreach (var group in due)
            {
                var (title, body, route) = group.Compose();
                foreach (var (id, user) in resolved)
                {
                    if (!IsVisibleToUser(group.Item, user))
                    {
                        continue;
                    }

                    DeliverToUser(id, title, body, route);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-media flush failed");
        }
    }

    private void DeliverToUser(Guid userId, string title, string body, string route)
    {
        // Same event shape as Seerr notifications so the client banner path
        // and notification tap routing need zero changes.
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

    // True when the host exposes everything needed to ask whether an item is
    // visible to a user. When it doesn't, visibility checks are skipped.
    private static bool CanCheckVisibility =>
        _userManagerType != null &&
        _userManagerGetUserById != null &&
        _baseItemIsVisible != null &&
        _baseItemIsVisibleUserType != null;

    private object? ResolveUser(Guid userId)
    {
        if (!CanCheckVisibility)
        {
            return null;
        }

        try
        {
            var userManager = _serviceProvider.GetService(_userManagerType!);
            if (userManager == null)
            {
                _logger.LogWarning("newMedia: no user manager, skipping the visibility check");
                return null;
            }

            return _userManagerGetUserById!.Invoke(userManager, [userId]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "newMedia: could not resolve user {UserId}", userId);
            return null;
        }
    }

    // Fails open whenever the check can't be made, so a host or a user we can't
    // read still gets notifications rather than losing every one of them.
    private static bool IsVisibleToUser(BaseItem? item, object? user)
    {
        if (item == null || !CanCheckVisibility || user == null)
        {
            return true;
        }

        if (!_baseItemIsVisibleUserType!.IsInstanceOfType(user))
        {
            return true;
        }

        try
        {
            return _baseItemIsVisible!.Invoke(item, [user]) is true;
        }
        catch
        {
            return true;
        }
    }

    private sealed class PendingGroup
    {
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public int? Year { get; init; }
        public required Guid RouteId { get; init; }
        public BaseItem? Item { get; init; }
        public int Count { get; private set; } = 1;
        public DateTimeOffset FirstSeen { get; private set; }
        public DateTimeOffset LastSeen { get; private set; }

        public static PendingGroup ForMovie(Movie movie, DateTimeOffset now) => new()
        {
            Kind = "movie",
            Name = movie.Name ?? "Unknown",
            Year = movie.ProductionYear,
            RouteId = movie.Id,
            Item = movie,
            FirstSeen = now,
            LastSeen = now
        };

        public static PendingGroup ForSeries(Episode episode, DateTimeOffset now) => new()
        {
            Kind = "series",
            Name = episode.SeriesName ?? "Unknown",
            RouteId = episode.SeriesId,
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
            var route = $"/item/{RouteId.ToString("N")}";
            if (Kind == "movie")
            {
                var year = Year.HasValue ? $" ({Year})" : string.Empty;
                return ("New in library", $"{Name}{year} was added", route);
            }

            var body = Count == 1
                ? $"{Name}: 1 new episode"
                : $"{Name}: {Count} new episodes";
            return ("New in library", body, route);
        }
    }
}
