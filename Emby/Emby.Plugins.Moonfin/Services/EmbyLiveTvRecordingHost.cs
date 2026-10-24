using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Tasks;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>The Emby implementation of <see cref="ILiveTvRecordingHost"/>.</summary>
    public sealed class EmbyLiveTvRecordingHost : ILiveTvRecordingHost, IDisposable
    {
        private const string RefreshGuideTaskKey = "RefreshGuide";

        private readonly ILiveTvManager _liveTvManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ITaskManager _taskManager;
        private readonly ILogger _logger;

        // Emby announces each recording as it starts and ends, so the file a timer is writing is
        // known without asking the DVR on every sample.
        private readonly ConcurrentDictionary<string, ActiveRecordingInfo> _active = new(StringComparer.OrdinalIgnoreCase);

        public EmbyLiveTvRecordingHost(
            ILiveTvManager liveTvManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ITaskManager taskManager,
            ILogger logger)
        {
            _liveTvManager = liveTvManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _taskManager = taskManager;
            _logger = logger;

            _liveTvManager.TimerCreated += OnTimerCreated;
            _liveTvManager.TimerCancelled += OnTimerCancelled;
            _liveTvManager.SeriesTimerCreated += OnSeriesTimerEvent;
            _liveTvManager.SeriesTimerCancelled += OnSeriesTimerEvent;
            _liveTvManager.RecordingStarted += OnRecordingStarted;
            _liveTvManager.RecordingEnded += OnRecordingEnded;
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
            _liveTvManager.SeriesTimerCreated -= OnSeriesTimerEvent;
            _liveTvManager.SeriesTimerCancelled -= OnSeriesTimerEvent;
            _liveTvManager.RecordingStarted -= OnRecordingStarted;
            _liveTvManager.RecordingEnded -= OnRecordingEnded;
            _taskManager.TaskCompleted -= OnTaskCompleted;
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemUpdated -= OnItemUpdated;
            _libraryManager.ItemRemoved -= OnItemRemoved;
        }

        public Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<ScheduledTimer> timers = _liveTvManager.GetTimers(new TimerQuery()).Items
                .Where(t => !string.IsNullOrEmpty(t.Id))
                .Select(t => new ScheduledTimer
                {
                    ApiId = t.Id,
                    TimerId = t.Id,
                    ProgramId = t.ProgramId,
                    ChannelName = t.ChannelName,
                    StartDate = t.StartDate.UtcDateTime,
                    EndDate = t.EndDate.UtcDateTime,
                    PrePaddingSeconds = t.PrePaddingSeconds,
                    PostPaddingSeconds = t.PostPaddingSeconds,
                    Status = t.Status.ToString(),
                })
                .ToList();
            return Task.FromResult(timers);
        }

        public string? GetActiveRecordingPath(string timerId)
        {
            if (_active.TryGetValue(timerId, out var known) && !string.IsNullOrEmpty(known.Path))
            {
                return known.Path;
            }

            // A recording that began before the plugin started was never announced to it.
            try
            {
                var current = _liveTvManager.GetActiveRecordingInfo(timerId);
                if (current != null && !string.IsNullOrEmpty(current.Path))
                {
                    _active[timerId] = current;
                    return current.Path;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("{0}", "Could not look up the active recording for timer " + timerId + ": " + ex.Message);
            }

            return null;
        }

        public ActiveRecordingTimer? GetActiveRecordingTimer(string path)
        {
            var info = _active.Values.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
            var timer = info?.Timer;
            if (timer == null)
            {
                return null;
            }

            // The series id comes from the guide program, so it matches the ids later airings carry.
            return new ActiveRecordingTimer
            {
                RetryCount = timer.RetryCount,
                StartDate = timer.StartDate.UtcDateTime,
                EndDate = timer.EndDate.UtcDateTime,
                PrePaddingSeconds = timer.PrePaddingSeconds,
                PostPaddingSeconds = timer.PostPaddingSeconds,
                SeriesId = SeriesIdOf(info!.Program),
                Name = info.Program != null ? SeriesNameOf(info.Program) : timer.Name,
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
                return info.Exists ? info.Length : (long?)null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public GuideProgram? GetProgram(string programId)
        {
            return long.TryParse(programId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && _libraryManager.GetItemById(id) is LiveTvProgram program
                ? ToGuideProgram(program, new Dictionary<long, LiveTvChannel?>())
                : null;
        }

        public IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(LiveTvProgram) },
                MinStartDate = new DateTimeOffset(DateTime.SpecifyKind(after, DateTimeKind.Utc)),
                ParentIndexNumber = episode.IsMovie ? null : episode.Season,
                IndexNumber = episode.IsMovie ? null : episode.Episode,
                IsMovie = episode.IsMovie ? true : null,
            };

            if (!episode.IsMovie && episode.SeriesId != null && long.TryParse(episode.SeriesId, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId))
            {
                query.SeriesIds = new[] { seriesId };
            }
            else
            {
                query.Name = episode.SeriesName;
            }

            var channels = new Dictionary<long, LiveTvChannel?>();
            return _libraryManager.GetItemList(query)
                .OfType<LiveTvProgram>()
                .Select(p => ToGuideProgram(p, channels))
                .ToList();
        }

        public IReadOnlyList<GuideProgram> FindProgramsByName(string name, DateTime after)
        {
            var channels = new Dictionary<long, LiveTvChannel?>();
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(LiveTvProgram) },
                MinStartDate = new DateTimeOffset(DateTime.SpecifyKind(after, DateTimeKind.Utc)),
                Name = name,
            })
                .OfType<LiveTvProgram>()
                .Select(p => ToGuideProgram(p, channels))
                .ToList();
        }

        public Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken)
        {
            try
            {
                var defaults = _liveTvManager.GetNewTimerDefaults(programId, cancellationToken);

                // The defaults come back as a series timer, which shares its base with a timer.
                var timer = JsonSerializer.Deserialize<TimerInfoDto>(JsonSerializer.Serialize<BaseTimerInfoDto>(defaults));
                if (timer == null)
                {
                    return Task.FromResult(ScheduleResult.Failed);
                }

                _liveTvManager.CreateTimer(timer);
                return Task.FromResult(ScheduleResult.Created);
            }
            catch (ArgumentException)
            {
                return Task.FromResult(ScheduleResult.AlreadyScheduled);
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}", "Could not schedule program " + programId + ": " + ex.Message);
                return Task.FromResult(ScheduleResult.Failed);
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
                        && (MimeTypes.GetMimeType(f) ?? string.Empty).StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        public bool IsVisibleTo(Guid itemId, Guid userId)
        {
            var user = _userManager.GetUserById(userId);
            return user != null && IsVisibleTo(itemId, user);
        }

        public ISet<Guid> VisibleTo(IReadOnlyCollection<Guid> itemIds, Guid userId)
        {
            var user = itemIds.Count == 0 ? null : _userManager.GetUserById(userId);
            return user == null
                ? new HashSet<Guid>()
                : new HashSet<Guid>(itemIds.Where(id => IsVisibleTo(id, user)));
        }

        public IReadOnlyList<string>? GetTags(Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            return item == null ? null : item.Tags ?? Array.Empty<string>();
        }

        public Task<bool> SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return Task.FromResult(false);
            }

            var tags = item.Tags ?? Array.Empty<string>();
            var has = tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            if (has != present)
            {
                item.SetTags(present
                    ? tags.Append(tag)
                    : tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }

            return Task.FromResult(true);
        }

        public DateTime? GetFileWrittenAt(Guid itemId)
        {
            var path = _libraryManager.GetItemById(itemId)?.Path;
            try
            {
                return path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
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

        private static string? SeriesIdOf(LiveTvProgram? program) =>
            program != null && program.SeriesId > 0 ? program.SeriesId.ToString(CultureInfo.InvariantCulture) : null;

        private static string SeriesNameOf(LiveTvProgram program) =>
            string.IsNullOrWhiteSpace(program.SeriesName) ? program.Name : program.SeriesName;

        private bool IsVisibleTo(Guid itemId, User user)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null || !item.IsVisibleStandalone(user))
            {
                return false;
            }

            return item is not LiveTvProgram program
                || (_libraryManager.GetItemById(program.ParentId) is { } channel && channel.IsVisibleStandalone(user));
        }

        // A program's parent item is its channel.
        private GuideProgram ToGuideProgram(LiveTvProgram program, Dictionary<long, LiveTvChannel?> channels)
        {
            if (!channels.TryGetValue(program.ParentId, out var channel))
            {
                channel = program.ParentId > 0 ? _libraryManager.GetItemById(program.ParentId) as LiveTvChannel : null;
                channels[program.ParentId] = channel;
            }

            return new GuideProgram
            {
                Id = program.InternalId.ToString(CultureInfo.InvariantCulture),
                ItemId = program.Id,
                IsMovie = program.IsMovie,
                Overview = program.Overview,
                ProductionYear = program.ProductionYear,
                ContentIds = program.ProviderIds
                    .Where(p => p.Key.Equals("Imdb", StringComparison.OrdinalIgnoreCase)
                        || p.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)
                        || p.Key.Equals("Tvdb", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(p => p.Key, p => p.Value),
                SeriesId = SeriesIdOf(program),
                SeriesName = SeriesNameOf(program),
                Season = program.ParentIndexNumber,
                Episode = program.IndexNumber,
                StartDate = program.StartDate.UtcDateTime,
                EndDate = (program.EndDate ?? program.StartDate).UtcDateTime,
                ChannelName = channel?.Name,
                ChannelNumber = channel?.Number,
                IsHD = channel?.IsHD ?? false,
            };
        }

        private static LibraryItem ToLibraryItem(BaseItem item)
        {
            return new LibraryItem
            {
                Id = item.Id,
                Path = item.Path,
                DateCreated = item.DateCreated.UtcDateTime,
                Tags = item.Tags ?? Array.Empty<string>(),
            };
        }

        private void OnSeriesTimerEvent(object? sender, EventArgs e) => ScheduleChanged?.Invoke(this, EventArgs.Empty);

        private void OnTimerCreated(object? sender, GenericEventArgs<TimerEventInfo> e)
        {
            var timer = e.Argument?.Timer;
            if (!string.IsNullOrEmpty(timer?.Id))
            {
                TimerCreated?.Invoke(this, new ScheduledTimer { TimerId = timer!.Id, ProgramId = timer.ProgramId });
            }

            ScheduleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnTimerCancelled(object? sender, GenericEventArgs<TimerEventInfo> e)
        {
            var timerId = e.Argument?.Timer?.Id;
            if (!string.IsNullOrEmpty(timerId))
            {
                TimerCancelled?.Invoke(this, timerId!);
            }

            ScheduleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnRecordingStarted(object? sender, GenericEventArgs<ActiveRecordingInfo> e)
        {
            if (e.Argument?.Timer?.Id is { Length: > 0 } timerId)
            {
                _active[timerId] = e.Argument;
            }

            ScheduleChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnRecordingEnded(object? sender, GenericEventArgs<ActiveRecordingInfo> e)
        {
            if (e.Argument?.Timer?.Id is { Length: > 0 } timerId)
            {
                _active.TryRemove(timerId, out _);
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
            if (e.Item is { IsVirtualItem: false } && e.Item is not Folder)
            {
                ItemAdded?.Invoke(this, ToLibraryItem(e.Item));
            }
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is { IsVirtualItem: false } && e.Item is not Folder)
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
}
