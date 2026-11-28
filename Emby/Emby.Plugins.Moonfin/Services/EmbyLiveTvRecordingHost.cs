using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
            _userManager.UserDeleted += OnUserDeleted;
        }

        public event EventHandler? ScheduleChanged;

        public event EventHandler<ScheduledTimer>? TimerCreated;

        public event EventHandler<string>? TimerCancelled;

        public event EventHandler? GuideRefreshed;

        public event EventHandler<LibraryItem>? ItemAdded;

        public event EventHandler<LibraryItem>? ItemUpdated;

        public event EventHandler<Guid>? ItemRemoved;

        public event EventHandler<Guid>? UserDeleted;

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
            _userManager.UserDeleted -= OnUserDeleted;
        }

        // Emby's CancelTimer is synchronous.
        public Task CancelTimerAsync(string apiId, CancellationToken cancellationToken)
        {
            _liveTvManager.CancelTimer(apiId);
            return Task.CompletedTask;
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
                    SeriesTimerId = string.IsNullOrEmpty(t.SeriesTimerId) ? null : t.SeriesTimerId,
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

        public Task<IReadOnlyList<SeriesRule>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SeriesRule>>(ListSeriesRules());
        }

        private List<SeriesRule> ListSeriesRules() =>
            _liveTvManager.GetSeriesTimers(new SeriesTimerQuery()).Items
                .Where(t => !string.IsNullOrEmpty(t.Id))
                .Select(t => new SeriesRule(
                    t.Id,
                    string.IsNullOrEmpty(t.SeriesId) ? null : t.SeriesId,
                    t.Name,
                    string.IsNullOrEmpty(t.ProgramId) ? null : t.ProgramId,
                    ChannelGuidOf(t.ChannelId)))
                .ToList();

        // Rules and programs carry the channel as its library Guid ("N"), as on Jellyfin, so a rule
        // with nothing scheduled can be judged by its channel. The DTO holds Emby's numeric id.
        private string? ChannelGuidOf(string? channelId) =>
            long.TryParse(channelId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
            && _libraryManager.GetItemById(id) is LiveTvChannel channel
                ? channel.Id.ToString("N")
                : null;

        public async Task<SeriesRuleCreation> CreateSeriesTimerAsync(string programId, IReadOnlyDictionary<string, JsonElement>? overrides, CancellationToken cancellationToken)
        {
            try
            {
                var before = new HashSet<string>(ListSeriesRules().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                var defaults = _liveTvManager.GetNewTimerDefaults(programId, cancellationToken);
                ApplyOverrides(defaults, overrides);

                var created = await _liveTvManager.CreateSeriesTimer(ToSeriesTimerInfo(defaults), cancellationToken).ConfigureAwait(false);

                // The API id of the new rule is the one that wasn't there before. The id the
                // service returned is the fallback when the list can't tell.
                var added = ListSeriesRules().Where(r => !before.Contains(r.Id)).ToList();
                var rule = added.Count == 1
                    ? added[0]
                    : added.FirstOrDefault(r => r.SeriesId != null && r.SeriesId == defaults.SeriesId)
                        ?? added.FirstOrDefault(r => r.Name == defaults.Name);
                return new SeriesRuleCreation(rule?.Id ?? (string.IsNullOrEmpty(created?.Id) ? null : created!.Id), Accepted: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The reason reaches the client, which opens its Premiere dialog on a Premiere refusal.
                _logger.Warn("{0}", "Could not create a series rule for program " + programId + ": " + ex.Message);
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

            foreach (var pair in overrides)
            {
                var property = typeof(SeriesTimerInfoDto).GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property == null || !property.CanWrite || property.Name is "Id" or "ServerId" or "ProgramId")
                {
                    continue;
                }

                try
                {
                    property.SetValue(rule, JsonSerializer.Deserialize(pair.Value.GetRawText(), property.PropertyType));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    _logger.Warn("{0}", "Ignored series rule setting " + pair.Key + ": " + ex.Message);
                }
            }
        }

        // Emby creates a series timer from its own SeriesTimerInfo, while the defaults come as a DTO.
        private static SeriesTimerInfo ToSeriesTimerInfo(SeriesTimerInfoDto dto) => new SeriesTimerInfo
        {
            Id = dto.Id,
            ChannelId = dto.ChannelId,
            ChannelIds = dto.ChannelIds,
            ParentFolderId = long.TryParse(dto.ParentFolderId, NumberStyles.None, CultureInfo.InvariantCulture, out var folder) ? folder : 0,
            ProgramId = dto.ProgramId,
            Name = dto.Name,
            Overview = dto.Overview,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            RecordAnyTime = dto.RecordAnyTime,
            KeepUpTo = dto.KeepUpTo,
            KeepUntil = dto.KeepUntil,
            SkipEpisodesInLibrary = dto.SkipEpisodesInLibrary,
            MatchExistingItemsWithAnyLibrary = dto.MatchExistingItemsWithAnyLibrary,
            RecordNewOnly = dto.RecordNewOnly,
            Days = dto.Days,
            Priority = dto.Priority,
            PrePaddingSeconds = dto.PrePaddingSeconds,
            PostPaddingSeconds = dto.PostPaddingSeconds,
            IsPrePaddingRequired = dto.IsPrePaddingRequired,
            IsPostPaddingRequired = dto.IsPostPaddingRequired,
            SeriesId = dto.SeriesId,
            MaxRecordingSeconds = dto.MaxRecordingSeconds,
            Keywords = dto.Keywords,
        };

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
                var timerId = FindTimerId(programId);
                return Task.FromResult(ScheduleResult.CreatedWith(timerId, timerId));
            }
            catch (ArgumentException)
            {
                return Task.FromResult(ScheduleResult.AlreadyScheduled);
            }
            catch (Exception ex)
            {
                // The reason reaches the client, which opens its Premiere dialog on a Premiere refusal.
                _logger.Warn("{0}", "Could not schedule program " + programId + ": " + ex.Message);
                return Task.FromResult(ScheduleResult.FailedWith(ex.Message));
            }
        }

        // The timer just created, by its program. Null when the lookup fails; the recording is scheduled regardless.
        private string? FindTimerId(string programId)
        {
            try
            {
                return _liveTvManager.GetTimers(new TimerQuery()).Items
                    .FirstOrDefault(t => string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase)
                        && t.Status != RecordingStatus.Cancelled && !string.IsNullOrEmpty(t.Id))?.Id;
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}", "Could not look up the timer for program " + programId + ": " + ex.Message);
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

        public ServerUser? GetUser(Guid userId)
        {
            try
            {
                var user = _userManager.GetUserById(userId);
                return user == null ? null : ToServerUser(user);
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}", "Could not read user " + userId + ": " + ex.Message);
                return null;
            }
        }

        public bool? UserExists(Guid userId)
        {
            try
            {
                return _userManager.GetUserById(userId) != null;
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}", "Could not check whether user " + userId + " exists: " + ex.Message);
                return null;
            }
        }

        // The image tag comes from the user's DTO; a DTO that can't be built costs only the image.
        private ServerUser ToServerUser(User user)
        {
            string? imageTag = null;
            try
            {
                imageTag = _userManager.GetUserDto(user).PrimaryImageTag;
            }
            catch (Exception ex)
            {
                _logger.Warn("{0}", "Could not read the image of user " + user.Id + ": " + ex.Message);
            }

            return new ServerUser(user.Id, user.Name ?? string.Empty, imageTag, user.Policy?.IsDisabled == true);
        }

        // Emby has no recording folder list to place an item under, so the recordings the asking
        // user can see are listed for just this item: found means it is one. Listed without a user
        // the query may miss items, hence the user.
        public bool? IsLiveTvRecording(Guid itemId, Guid userId)
        {
            try
            {
                var user = _userManager.GetUserById(userId);
                if (user == null || _libraryManager.GetItemById(itemId) is not { } item)
                {
                    return false;
                }

                var found = _liveTvManager.GetRecordings(new InternalItemsQuery(user) { Recursive = true, ItemIds = new[] { item.InternalId } }, CancellationToken.None).Items;
                return found.Any(r => Guid.TryParse(r.Guid, out var id) && id == itemId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn("{0}", "Could not check whether item " + itemId + " is a Live TV recording: " + ex.Message);
                return null;
            }
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
                ChannelId = channel?.Id.ToString("N"),
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

        private void OnUserDeleted(object? sender, GenericEventArgs<User> e)
        {
            if (e.Argument != null)
            {
                UserDeleted?.Invoke(this, e.Argument.Id);
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
