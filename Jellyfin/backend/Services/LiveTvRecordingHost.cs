using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Events;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>Tells the recording host a user was deleted, which the user manager has no event for.</summary>
public sealed class RecordingHostUserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly JellyfinLiveTvRecordingHost _host;

    public RecordingHostUserDeletedConsumer(JellyfinLiveTvRecordingHost host)
    {
        _host = host;
    }

    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        // The User the event carries changed type in 10.11, so its id is read by reflection.
        var user = eventArgs.GetType().GetProperty("Argument")?.GetValue(eventArgs);
        if (user?.GetType().GetProperty("Id")?.GetValue(user) is Guid id)
        {
            _host.NotifyUserDeleted(id);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The Jellyfin implementation of <see cref="ILiveTvRecordingHost"/>.</summary>
public sealed class JellyfinLiveTvRecordingHost : ILiveTvRecordingHost, IDisposable
{
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly ILiveTvManager _liveTvManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<JellyfinLiveTvRecordingHost> _logger;

    public JellyfinLiveTvRecordingHost(
        ILiveTvManager liveTvManager,
        IRecordingsManager recordingsManager,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IServiceProvider services,
        ILogger<JellyfinLiveTvRecordingHost> logger)
    {
        _services = services;
        _liveTvManager = liveTvManager;
        _recordingsManager = recordingsManager;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;

        _liveTvManager.TimerCreated += OnTimerCreated;
        _liveTvManager.TimerCancelled += OnTimerCancelled;
        _liveTvManager.SeriesTimerCreated += OnTimerEvent;
        _liveTvManager.SeriesTimerCancelled += OnTimerEvent;
        _taskManager.TaskCompleted += OnTaskCompleted;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
    }

    public event EventHandler? ScheduleChanged;

    public event EventHandler<ScheduledTimer>? TimerCreated;

    public event EventHandler<string>? TimerCancelled;

    public event EventHandler? GuideRefreshed;

    public event EventHandler<LibraryItem>? ItemAdded;

    public event EventHandler<LibraryItem>? ItemUpdated;

    public event EventHandler<Guid>? ItemRemoved;

    private readonly object _userEventLock = new();
    private EventHandler<ServerUser>? _userUpdated;
    private EventInfo? _userUpdatedEvent;
    private Delegate? _userUpdatedHandler;
    private object? _userUpdatedSource;

    // Subscribed on first use rather than in the constructor, so a host nobody watches for user
    // changes never touches the user manager's event.
    public event EventHandler<ServerUser>? UserUpdated
    {
        add
        {
            _userUpdated += value;
            SubscribeUserUpdated();
        }
        remove => _userUpdated -= value;
    }

    public event EventHandler<Guid>? UserDeleted;

    /// <summary>Raised by the user deleted event consumer, since the user manager has no event of its own for it.</summary>
    internal void NotifyUserDeleted(Guid userId) => UserDeleted?.Invoke(this, userId);

    private void SubscribeUserUpdated()
    {
        lock (_userEventLock)
        {
            if (_userUpdatedHandler != null)
            {
                return;
            }

            try
            {
                // The event's argument carries Jellyfin's User type, which changed in 10.11, so the
                // handler is bound to whichever event argument type the running server uses.
                var userManager = UserReflection.UserManager(_services);
                var updated = userManager?.GetType().GetInterfaces()
                    .Select(i => i.GetEvent("OnUserUpdated"))
                    .FirstOrDefault(e => e != null);
                if (userManager == null || updated?.EventHandlerType is not { IsGenericType: true } handlerType)
                {
                    return;
                }

                var method = typeof(JellyfinLiveTvRecordingHost)
                    .GetMethod(nameof(OnUserUpdatedRaw), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(handlerType.GetGenericArguments()[0]);
                var handler = Delegate.CreateDelegate(handlerType, this, method);
                updated.AddEventHandler(userManager, handler);
                _userUpdatedEvent = updated;
                _userUpdatedHandler = handler;
                _userUpdatedSource = userManager;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not subscribe to user changes");
            }
        }
    }

    private void OnUserUpdatedRaw<TArgs>(object? sender, TArgs e)
    {
        try
        {
            var user = typeof(TArgs).GetProperty("Argument")?.GetValue(e);
            var serverUser = user == null ? null : ToServerUser(user);
            if (serverUser != null)
            {
                _userUpdated?.Invoke(this, serverUser);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read an updated user");
        }
    }

    private ServerUser? ToServerUser(object user)
    {
        var dto = UserReflection.ToDto(_services, user);
        return dto == null ? null : new ServerUser(dto.Id, dto.Name ?? string.Empty, dto.PrimaryImageTag, dto.Policy?.IsDisabled == true);
    }

    public ServerUser? GetUser(Guid userId)
    {
        try
        {
            var user = UserReflection.ResolveUser(_services, userId);
            return user == null ? null : ToServerUser(user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read user {UserId}", userId);
            return null;
        }
    }

    // Only the lookup by id: the DTO a ServerUser is built from can fail on its own, which says
    // nothing about whether the user exists.
    public bool? UserExists(Guid userId)
    {
        try
        {
            if (!UserReflection.CanResolveUsers(_services))
            {
                return null;
            }

            return UserReflection.ResolveUser(_services, userId) != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check whether user {UserId} exists", userId);
            return null;
        }
    }

    public IReadOnlyList<ServerUser> GetUsers()
    {
        var result = new List<ServerUser>();
        if (UserReflection.AllUsers(_services) is { } users)
        {
            foreach (var user in users)
            {
                if (user == null)
                {
                    continue;
                }

                // One user whose DTO can't be built is left out rather than failing the whole list.
                try
                {
                    if (ToServerUser(user) is { } serverUser)
                    {
                        result.Add(serverUser);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read a user while listing users; leaving them out");
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_userEventLock)
        {
            if (_userUpdatedHandler != null)
            {
                _userUpdatedEvent!.RemoveEventHandler(_userUpdatedSource, _userUpdatedHandler);
                _userUpdatedHandler = null;
            }
        }

        _liveTvManager.TimerCreated -= OnTimerCreated;
        _liveTvManager.TimerCancelled -= OnTimerCancelled;
        _liveTvManager.SeriesTimerCreated -= OnTimerEvent;
        _liveTvManager.SeriesTimerCancelled -= OnTimerEvent;
        _taskManager.TaskCompleted -= OnTaskCompleted;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
    }

    public async Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken)
    {
        var result = await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false);
        var manual = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // The public DTO omits IsManual; only cancelled timers need the service's distinction.
        foreach (var service in _liveTvManager.Services.Where(s => result.Items.Any(t =>
            t.Status == RecordingStatus.Cancelled && string.Equals(t.ServiceName, s.Name, StringComparison.OrdinalIgnoreCase))))
        {
            // One service failing or timing out leaves its timers' IsManual unknown rather than
            // failing the load; only the caller's own cancellation ends it.
            try
            {
                foreach (var timer in await service.GetTimersAsync(cancellationToken).ConfigureAwait(false))
                {
                    manual[service.Name + "\0" + timer.Id] = timer.IsManual;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not read the timers of Live TV service {Service}", service.Name);
            }
        }

        return result.Items
            .Where(t => !string.IsNullOrEmpty(t.ExternalId))
            .Select(t => new ScheduledTimer
            {
                ApiId = t.Id,
                TimerId = t.ExternalId,
                ProgramId = t.ProgramId,
                ChannelName = t.ChannelName,
                StartDate = t.StartDate,
                EndDate = t.EndDate,
                PrePaddingSeconds = t.PrePaddingSeconds,
                PostPaddingSeconds = t.PostPaddingSeconds,
                Status = t.Status.ToString(),
                SeriesTimerId = string.IsNullOrEmpty(t.SeriesTimerId) ? null : t.SeriesTimerId,
                IsManual = manual.TryGetValue(t.ServiceName + "\0" + t.ExternalId, out var isManual) ? isManual : null,
            })
            .ToList();
    }

    public string? GetActiveRecordingPath(string timerId) => _recordingsManager.GetActiveRecordingPath(timerId);

    public ActiveRecordingTimer? GetActiveRecordingTimer(string path)
    {
        var timer = _recordingsManager.GetActiveRecordingInfo(path)?.Timer;
        if (timer == null)
        {
            return null;
        }

        return new ActiveRecordingTimer
        {
            RetryCount = timer.RetryCount,
            StartDate = timer.StartDate,
            EndDate = timer.EndDate,
            PrePaddingSeconds = timer.PrePaddingSeconds,
            PostPaddingSeconds = timer.PostPaddingSeconds,
            SeriesId = timer.SeriesId,
            Name = timer.Name,
            SeasonNumber = timer.SeasonNumber,
            EpisodeNumber = timer.EpisodeNumber,
            EpisodeTitle = timer.EpisodeTitle,
        };
    }

    public long? GetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public GuideProgram? GetProgram(string programId)
    {
        if (!Guid.TryParse(programId, out var id))
        {
            return null;
        }

        return _libraryManager.GetItemById(id) is LiveTvProgram program ? ToGuideProgram(program, new()) : null;
    }

    public IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            MinStartDate = after,
            ParentIndexNumber = episode.IsMovie ? null : episode.Season,
            IndexNumber = episode.IsMovie ? null : episode.Episode,
            IsMovie = episode.IsMovie ? true : null,
        };

        if (!episode.IsMovie && episode.SeriesId != null)
        {
            query.ExternalSeriesId = episode.SeriesId;
        }
        else
        {
            query.Name = episode.SeriesName;
        }

        // GetItemList changed its return type in Jellyfin 10.11, so a compiled call
        // only binds on the version it was built against. GetItemsResult has the same
        // signature everywhere and answers the same question.
        var channels = new Dictionary<Guid, BaseItem?>();
        return _libraryManager.GetItemsResult(query).Items
            .OfType<LiveTvProgram>()
            .Select(p => ToGuideProgram(p, channels))
            .ToList();
    }

    public IReadOnlyList<GuideProgram> FindProgramsByName(string name, DateTime after)
    {
        var channels = new Dictionary<Guid, BaseItem?>();
        return _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            MinStartDate = after,
            Name = name,
        }).Items.OfType<LiveTvProgram>().Select(p => ToGuideProgram(p, channels)).ToList();
    }

    public async Task<IReadOnlyList<SeriesRule>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        var result = await _liveTvManager.GetSeriesTimers(new SeriesTimerQuery(), cancellationToken).ConfigureAwait(false);

        // The public DTO carries no guide series id, so rules are identified by their API id alone.
        return result.Items
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .Select(t => new SeriesRule(t.Id, null, t.Name))
            .ToList();
    }

    public async Task<SeriesRuleCreation> CreateSeriesTimerAsync(string programId, IReadOnlyDictionary<string, JsonElement>? overrides, CancellationToken cancellationToken)
    {
        try
        {
            var before = (await GetSeriesTimersAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => r.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rule = await _liveTvManager.GetNewTimerDefaults(programId, cancellationToken).ConfigureAwait(false);
            ApplyOverrides(rule, overrides);

            await _liveTvManager.CreateSeriesTimer(rule, cancellationToken).ConfigureAwait(false);

            // The server returns no id, so the new rule is the one that wasn't there before.
            var created = (await GetSeriesTimersAsync(cancellationToken).ConfigureAwait(false))
                .Where(r => !before.Contains(r.Id))
                .ToList();
            return new SeriesRuleCreation((created.Count == 1 ? created[0] : created.FirstOrDefault(r => r.Name == rule.Name))?.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not create a series rule for program {ProgramId}", programId);
            return SeriesRuleCreation.Failed(ex.Message);
        }
    }

    // The client's settings map names the rule's properties directly (RecordNewOnly, KeepUpTo, ...).
    private void ApplyOverrides(SeriesTimerInfoDto rule, IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        if (overrides == null)
        {
            return;
        }

        foreach (var (name, value) in overrides)
        {
            var property = typeof(SeriesTimerInfoDto).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is not { CanWrite: true } || property.Name is "Id" or "ServerId" or "ProgramId")
            {
                continue;
            }

            try
            {
                property.SetValue(rule, value.Deserialize(property.PropertyType));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Ignored series rule setting {Setting}", name);
            }
        }
    }

    public async Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken)
    {
        try
        {
            var defaults = await _liveTvManager.GetNewTimerDefaults(programId, cancellationToken).ConfigureAwait(false);

            // The defaults come back as a series timer. The timer DTO shares its base, so the
            // shared fields carry over.
            var timer = JsonSerializer.Deserialize<TimerInfoDto>(JsonSerializer.Serialize<BaseTimerInfoDto>(defaults));
            if (timer == null)
            {
                return ScheduleResult.Failed;
            }

            await _liveTvManager.CreateTimer(timer, cancellationToken).ConfigureAwait(false);
            var created = await FindTimerAsync(programId, cancellationToken).ConfigureAwait(false);
            return ScheduleResult.CreatedWith(created?.TimerId, created?.ApiId);
        }
        catch (ArgumentException)
        {
            // The server refuses a program that already has an active timer.
            return ScheduleResult.AlreadyScheduled;
        }
        catch (Exception ex)
        {
            // The reason reaches the client, which shows it (a tuner conflict, say).
            _logger.LogWarning(ex, "Could not schedule program {ProgramId}", programId);
            return ScheduleResult.FailedWith(ex.Message);
        }
    }

    // The timer just created, by its program. Null when the lookup fails; the recording is scheduled regardless.
    private async Task<ScheduledTimer?> FindTimerAsync(string programId, CancellationToken cancellationToken)
    {
        try
        {
            return (await GetTimersAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(t => string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase)
                    && t.Status != nameof(RecordingStatus.Cancelled));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not look up the timer for program {ProgramId}", programId);
            return null;
        }
    }

    public LibraryItem? FindItemByPath(string path)
    {
        var item = _libraryManager.FindByPath(path, false);
        return item == null ? null : ToLibraryItem(item);
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> FindRenamedSiblings(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            return Directory.EnumerateFiles(directory)
                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)
                    && MimeTypes.GetMimeType(f).StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public bool IsVisibleTo(Guid itemId, Guid userId)
    {
        var user = UserReflection.ResolveUser(_services, userId);
        return user != null && IsVisibleTo(itemId, user);
    }

    public ISet<Guid> VisibleTo(IReadOnlyCollection<Guid> itemIds, Guid userId)
    {
        var user = itemIds.Count == 0 ? null : UserReflection.ResolveUser(_services, userId);
        return user == null
            ? new HashSet<Guid>()
            : itemIds.Where(id => IsVisibleTo(id, user)).ToHashSet();
    }

    public IReadOnlyList<string>? GetTags(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item == null ? null : item.Tags ?? Array.Empty<string>();
    }

    public async Task<bool> SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return false;
        }

        var tags = item.Tags ?? Array.Empty<string>();
        var has = tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        if (has == present)
        {
            return true;
        }

        item.Tags = present
            ? tags.Append(tag).ToArray()
            : tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public DateTime? GetFileWrittenAt(Guid itemId)
    {
        var path = _libraryManager.GetItemById(itemId)?.Path;
        try
        {
            return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Deleting any other kind of video can take its whole folder with it.
    public bool DeleteItem(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return true;
        }

        if (item is not Episode)
        {
            return false;
        }

        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
        return true;
    }

    // A finished recording's DTO type is Episode, Movie or Video, so only its place under the
    // recording locations tells it apart from any other library item.
    public bool? IsLiveTvRecording(Guid itemId)
    {
        try
        {
            if (_libraryManager.GetItemById(itemId) is not { IsFolder: false } item)
            {
                return false;
            }

            var locations = RecordingLocations();
            if (!string.IsNullOrEmpty(item.Path) && locations.Any(l => IsUnderLocation(item.Path, l)))
            {
                return true;
            }

            // Elsewhere means "not a recording" only once the recording locations are known to
            // the library; with none of them resolving, this server's setup can't be read.
            return RecordingFolderIds(locations).Length == 0 ? null : false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check whether item {ItemId} is a Live TV recording", itemId);
            return null;
        }
    }

    /// <summary>Whether a file path lies inside a folder, ignoring case and either separator.</summary>
    internal static bool IsUnderLocation(string path, string location)
    {
        var folder = location.TrimEnd('/', '\\');
        return folder.Length > 0
            && path.Length > folder.Length
            && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && path[folder.Length] is '/' or '\\';
    }

    // GetRecordingFolders builds its folder infos from the configured paths and never sets their
    // ItemId, so the paths are all there is to go on.
    private string[] RecordingLocations() =>
        _recordingsManager.GetRecordingFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // The library folders at the recording locations, found the way LiveTvManager finds them.
    private Guid[] RecordingFolderIds(IEnumerable<string> locations) =>
        locations
            .Select(l => _libraryManager.FindByPath(l, true))
            .Where(f => f != null)
            .Select(f => f!.Id)
            .Distinct()
            .ToArray();

    public IReadOnlyList<LibraryItem> FindItemsWithTagPrefix(string prefix)
    {
        var folders = RecordingFolderIds(RecordingLocations());
        if (folders.Length == 0)
        {
            return Array.Empty<LibraryItem>();
        }

        // Tags only match whole values in a query, so the recordings are listed and filtered here.
        return _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            AncestorIds = folders,
            IsFolder = false,
            IsVirtualItem = false,
        }).Items
            .Where(i => (i.Tags ?? Array.Empty<string>()).Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(ToLibraryItem)
            .ToList();
    }

    private bool IsVisibleTo(Guid itemId, object user)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null || !UserReflection.IsVisibleStandaloneTo(item, user))
        {
            return false;
        }

        return item is not LiveTvProgram program
            || (_libraryManager.GetItemById(program.ChannelId) is { } channel
                && UserReflection.IsVisibleStandaloneTo(channel, user));
    }

    private GuideProgram ToGuideProgram(LiveTvProgram program, Dictionary<Guid, BaseItem?> channels)
    {
        if (!channels.TryGetValue(program.ChannelId, out var channel))
        {
            channel = program.ChannelId == Guid.Empty ? null : _libraryManager.GetItemById(program.ChannelId);
            channels[program.ChannelId] = channel;
        }

        return new GuideProgram
        {
            Id = program.Id.ToString("N"),
            ItemId = program.Id,
            IsMovie = program.IsMovie,
            Overview = program.Overview,
            ProductionYear = program.ProductionYear,
            ContentIds = program.ProviderIds
                .Where(p => p.Key.Equals("Imdb", StringComparison.OrdinalIgnoreCase)
                    || p.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)
                    || p.Key.Equals("Tvdb", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value),
            SeriesId = program.ExternalSeriesId,
            SeriesName = program.Name,
            Season = program.ParentIndexNumber,
            Episode = program.IndexNumber,
            EpisodeTitle = program.EpisodeTitle,
            StartDate = program.StartDate,
            EndDate = program.EndDate ?? program.StartDate,
            ChannelName = channel?.Name,
            ChannelNumber = (channel as LiveTvChannel)?.Number,
            IsHD = channel?.IsHD ?? false,
        };
    }

    private static LibraryItem ToLibraryItem(BaseItem item)
    {
        return new LibraryItem
        {
            Id = item.Id,
            Path = item.Path,
            DateCreated = item.DateCreated,
            Tags = item.Tags ?? Array.Empty<string>(),
        };
    }

    private void OnTimerEvent(object? sender, EventArgs e) => ScheduleChanged?.Invoke(this, EventArgs.Empty);

    // A service that doesn't implement ISupportsNewTimerIds raises these without an id. The
    // schedule still changed, so only the id-specific event is skipped.
    private void OnTimerCreated(object? sender, GenericEventArgs<TimerEventInfo> e)
    {
        if (!string.IsNullOrEmpty(e.Argument?.Id))
        {
            TimerCreated?.Invoke(this, new ScheduledTimer
            {
                TimerId = e.Argument.Id,
                ProgramId = e.Argument.ProgramId?.ToString("N"),
            });
        }

        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimerCancelled(object? sender, GenericEventArgs<TimerEventInfo> e)
    {
        if (!string.IsNullOrEmpty(e.Argument?.Id))
        {
            TimerCancelled?.Invoke(this, e.Argument.Id);
        }

        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Task?.ScheduledTask?.Key, RefreshGuideTaskKey, StringComparison.Ordinal))
        {
            GuideRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { IsVirtualItem: false, IsFolder: false })
        {
            ItemAdded?.Invoke(this, ToLibraryItem(e.Item));
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { IsVirtualItem: false, IsFolder: false })
        {
            ItemUpdated?.Invoke(this, ToLibraryItem(e.Item));
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item != null)
        {
            ItemRemoved?.Invoke(this, e.Item.Id);
        }
    }
}
