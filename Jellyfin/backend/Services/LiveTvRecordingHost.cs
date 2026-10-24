using System.Text.Json;
using Jellyfin.Data.Events;
using Jellyfin.Data.Enums;
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

    public void Dispose()
    {
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
            return ScheduleResult.Created;
        }
        catch (ArgumentException)
        {
            // The server refuses a program that already has an active timer.
            return ScheduleResult.AlreadyScheduled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not schedule program {ProgramId}", programId);
            return ScheduleResult.Failed;
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
