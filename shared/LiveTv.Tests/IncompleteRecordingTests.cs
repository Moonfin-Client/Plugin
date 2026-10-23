using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Moonfin.LiveTv.Tests;

internal sealed class PlainStateFile : IStateFile
{
    public static readonly PlainStateFile Instance = new();

    public T? Read<T>(string path, Func<string, T?> parse)
        where T : class => File.Exists(path) ? parse(File.ReadAllText(path)) : null;

    public void Write(string path, string contents) => File.WriteAllText(path, contents);
}

internal enum LogKind
{
    Info,
    Warn,
    Error,
}

/// <summary>Keeps every message, for tests that check a decision is explained in the log.</summary>
internal sealed class ListRecordingLog : IRecordingLog
{
    public List<(LogKind Kind, string Message)> Entries { get; } = new();

    public void Info(string template, params object?[] args) => Entries.Add((LogKind.Info, RecordingLogText.Format(template, args)));

    public void Warn(Exception? exception, string template, params object?[] args) => Entries.Add((LogKind.Warn, RecordingLogText.Format(template, args)));

    public void Error(Exception? exception, string template, params object?[] args) => Entries.Add((LogKind.Error, RecordingLogText.Format(template, args)));
}

internal sealed class NoRecordingLog : IRecordingLog
{
    public static readonly NoRecordingLog Instance = new();

    public void Info(string template, params object?[] args)
    {
    }

    public void Warn(Exception? exception, string template, params object?[] args)
    {
    }

    public void Error(Exception? exception, string template, params object?[] args)
    {
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>An in-memory server: timers, recordings whose files grow, a guide and a library.</summary>
internal sealed class FakeRecordingHost : ILiveTvRecordingHost
{
#pragma warning disable CS0067 // The service subscribes through the watcher, which these tests don't run.
    public event EventHandler? ScheduleChanged;
    public event EventHandler<ScheduledTimer>? TimerCreated;
    public event EventHandler<string>? TimerCancelled;
    public event EventHandler? GuideRefreshed;
    public event EventHandler<LibraryItem>? ItemAdded;
    public event EventHandler<LibraryItem>? ItemUpdated;
    public event EventHandler<Guid>? ItemRemoved;
#pragma warning restore CS0067

    public List<ScheduledTimer> Timers { get; } = new();

    public Dictionary<string, string> ActivePaths { get; } = new();

    public Dictionary<string, ActiveRecordingTimer> ActiveTimers { get; } = new();

    public Dictionary<string, long> Files { get; } = new();

    public Dictionary<string, DateTime> LastWrite { get; } = new();

    public Func<DateTime>? Clock { get; set; }

    public List<GuideProgram> Programs { get; } = new();

    public List<LibraryItem> Items { get; } = new();

    public List<string> Scheduled { get; } = new();

    public List<Guid> Deleted { get; } = new();

    public Action<ScheduledTimer>? TimerScheduled { get; set; }

    public Func<string, IReadOnlyList<string>>? RenamedSiblingLookup { get; set; }

    public bool TimersUnavailable { get; set; }

    public bool TimerBackendTimesOut { get; set; }

    public bool GuideBackendTimesOut { get; set; }

    public TaskCompletionSource? GuideReadAttempted { get; set; }

    public bool TagWritesFail { get; set; }

    public bool DeletesFail { get; set; }

    public bool SchedulingFails { get; set; }

    public TaskCompletionSource? TimerReadGate { get; set; }

    public int ProgramLookups { get; set; }

    public HashSet<string> HiddenPrograms { get; } = new();

    public async Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken)
    {
        if (TimerReadGate != null) await TimerReadGate.Task.WaitAsync(cancellationToken);
        if (TimerBackendTimesOut) throw new TaskCanceledException("DVR backend timed out");
        if (TimersUnavailable) throw new InvalidOperationException("Live TV is not ready");
        return Timers.ToList();
    }

    public string? GetActiveRecordingPath(string timerId) => ActivePaths.GetValueOrDefault(timerId);

    public ActiveRecordingTimer? GetActiveRecordingTimer(string path) => ActiveTimers.GetValueOrDefault(path);

    public long? GetFileLength(string path) => Files.TryGetValue(path, out var size) ? size : null;

    public GuideProgram? GetProgram(string programId)
    {
        ProgramLookups++;
        return Programs.FirstOrDefault(p => p.Id == programId);
    }

    public IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after)
    {
        GuideReadAttempted?.TrySetResult();
        if (GuideBackendTimesOut) throw new TaskCanceledException("Guide backend timed out");
        return Programs.Where(p => p.StartDate > after).ToList();
    }

    public void RefreshGuide() => GuideRefreshed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<GuideProgram> FindProgramsByName(string name, DateTime after) =>
        Programs.Where(p => p.StartDate > after && EpisodeKey.NormalizeName(p.SeriesName) == EpisodeKey.NormalizeName(name)).ToList();

    public Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken)
    {
        if (SchedulingFails) return Task.FromResult(ScheduleResult.Failed);
        Scheduled.Add(programId);
        var program = Programs.First(p => p.Id == programId);
        var timer = Timers.FirstOrDefault(t => t.ProgramId == programId && t.Status == "Cancelled") ?? new ScheduledTimer
        {
            ApiId = "api-" + programId,
            TimerId = "t-" + programId,
            ProgramId = programId,
            StartDate = program.StartDate,
            EndDate = program.EndDate,
            Status = "New",
        };
        timer.Status = "New";
        timer.IsManual = true;
        if (!Timers.Contains(timer)) Timers.Add(timer);
        TimerCreated?.Invoke(this, timer);
        TimerScheduled?.Invoke(timer);
        return Task.FromResult(ScheduleResult.Created);
    }

    public LibraryItem? FindItemByPath(string path) => Items.FirstOrDefault(i => i.Path == path);

    public bool FileExists(string path) => Files.ContainsKey(path);

    public IReadOnlyList<string> FindRenamedSiblings(string path) => RenamedSiblingLookup?.Invoke(path) ?? Array.Empty<string>();

    public bool IsVisibleTo(Guid itemId, Guid userId) => !HiddenPrograms.Contains(itemId.ToString("N"));

    public ISet<Guid> VisibleTo(IReadOnlyCollection<Guid> itemIds, Guid userId) => itemIds.ToHashSet();

    public IReadOnlyList<string>? GetTags(Guid itemId) => Items.FirstOrDefault(i => i.Id == itemId)?.Tags;

    public void CancelTimer(ScheduledTimer timer, bool apiId = false)
    {
        Timers.Remove(timer);
        ActivePaths.Remove(timer.TimerId);
        TimerCancelled?.Invoke(this, apiId ? timer.ApiId! : timer.TimerId);
        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<bool> SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken)
    {
        if (TagWritesFail)
        {
            throw new IOException("The item could not be saved");
        }

        var item = Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return Task.FromResult(false);
        }

        var tags = item.Tags.Where(t => t != tag).ToList();
        if (present)
        {
            tags.Add(tag);
        }

        item.Tags = tags;
        return Task.FromResult(true);
    }

    public DateTime? GetFileWrittenAt(Guid itemId) =>
        Items.FirstOrDefault(i => i.Id == itemId)?.Path is { } path && Files.ContainsKey(path)
            ? LastWrite.GetValueOrDefault(path, DateTime.MinValue)
            : null;

    public bool DeleteItem(Guid itemId)
    {
        if (DeletesFail)
        {
            throw new IOException("The file is in use");
        }

        Items.RemoveAll(i => i.Id == itemId);
        Deleted.Add(itemId);
        return true;
    }

    public void StartRecording(ScheduledTimer timer, string path, int? season, int? episode)
    {
        timer.Status = "InProgress";
        ActivePaths[timer.TimerId] = path;
        ActiveTimers[path] = new ActiveRecordingTimer
        {
            StartDate = timer.StartDate,
            EndDate = timer.EndDate,
            SeriesId = "SH001",
            Name = "Storage Wars",
            SeasonNumber = season,
            EpisodeNumber = episode,
        };
        Files.TryAdd(path, 0);
    }

    public void StopRecording(ScheduledTimer timer)
    {
        ActivePaths.Remove(timer.TimerId);
        timer.Status = "Completed";
    }

    /// <summary>Grows every file that is being written.</summary>
    public void Write()
    {
        foreach (var path in ActivePaths.Values)
        {
            Files[path] += 1_000_000;
            if (Clock != null)
            {
                LastWrite[path] = Clock();
            }
        }
    }

    public LibraryItem AddItem(string path)
    {
        var item = new LibraryItem { Id = Guid.NewGuid(), Path = path };
        Items.Add(item);
        return item;
    }
}

public sealed class IncompleteRecordingTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-incomplete-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(Start.AddHours(-1));
    private readonly FakeRecordingHost _host = new();
    private readonly IncompleteRecordingSettings _settings = new() { AutoRecordComplete = true };
    private readonly ListRecordingLog _log = new();

    public IncompleteRecordingTests() => _host.Clock = () => _clock.UtcNow;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string StorePath => Path.Combine(_dir, "store.json");

    // A new instance is a new server process.
    private IncompleteRecordingService NewService() =>
        new(_host, _log, PlainStateFile.Instance, () => _clock.UtcNow, StorePath, () => _settings);

    private void AssertLogged(LogKind kind, string text) =>
        Assert.Contains(_log.Entries, e => e.Kind == kind && e.Message.Contains(text, StringComparison.Ordinal));

    private int CountLogged(LogKind kind, string text) =>
        _log.Entries.Count(e => e.Kind == kind && e.Message.Contains(text, StringComparison.Ordinal));

    private void ListenForTimerEvents(IncompleteRecordingService service)
    {
        _host.TimerCreated += (_, timer) => service.MarkTimerCreated(timer);
        _host.TimerCancelled += (_, id) => service.MarkTimerCancelled(id);
    }

    private IncompleteRecordingStoreData ReadStore() =>
        JsonSerializer.Deserialize<IncompleteRecordingStoreData>(File.ReadAllText(StorePath))!;

    private GuideProgram AddAiring(string id, DateTime start, int? season = 3, int? episode = 7, bool hd = false)
    {
        var program = new GuideProgram
        {
            Id = id,
            ItemId = Guid.TryParse(id, out var itemId) ? itemId : Guid.Empty,
            SeriesId = "SH001",
            SeriesName = "Storage Wars",
            Season = season,
            Episode = episode,
            StartDate = start,
            EndDate = start.AddMinutes(20),
            IsHD = hd,
        };
        _host.Programs.Add(program);
        return program;
    }

    // Records a 20-minute show without padding, writing only for the first given minutes, and
    // runs the service once a minute until the recording has been judged.
    private async Task<IncompleteRecordingService> RecordShow(int writtenMinutes, int? season = 3, int? episode = 7)
    {
        var timer = new ScheduledTimer
        {
            TimerId = "t1",
            ProgramId = "p1",
            StartDate = Start,
            EndDate = Start.AddMinutes(20),
            Status = "New",
        };
        _host.Timers.Add(timer);

        var service = NewService();
        _clock.UtcNow = Start;
        while (_clock.UtcNow <= timer.EndDate)
        {
            var minute = (_clock.UtcNow - Start).TotalMinutes;
            if (minute < writtenMinutes && !_host.ActivePaths.ContainsKey(timer.TimerId))
            {
                _host.StartRecording(timer, "/rec/a.ts", season, episode);
            }
            else if (minute >= writtenMinutes && _host.ActivePaths.ContainsKey(timer.TimerId))
            {
                _host.StopRecording(timer);
            }

            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        return service;
    }

    // Runs one timer through its window, writing for the first given minutes, until it is judged.
    private async Task Record(IncompleteRecordingService service, ScheduledTimer timer, string path, int writtenMinutes)
    {
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, path, 3, 7);
        while (_clock.UtcNow <= timer.EndDate)
        {
            if ((_clock.UtcNow - timer.StartDate).TotalMinutes >= writtenMinutes)
            {
                _host.StopRecording(timer);
            }

            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);
    }

    private GuideProgram AddMovie(string id, DateTime start, string description = "A distinctive movie plot.")
    {
        var program = AddAiring(id, start, season: null, episode: null);
        program.IsMovie = true;
        program.SeriesName = "The Movie";
        program.Overview = description;
        return program;
    }

    [Fact]
    public void MovieKeysRequirePositiveClassificationAndNonemptyTitleAndDescription()
    {
        var movie = AddMovie("p", Start);
        var key = movie.Key!;
        movie.SeriesName = "  THE   MOVIE  ";
        movie.Overview = " a DISTINCTIVE movie plot. ";
        Assert.True(key.Matches(movie.Key!));
        movie.Overview = "A different movie.";
        Assert.False(key.Matches(movie.Key!));
        movie.Overview = "  ";
        Assert.Null(movie.Key);
        movie.Overview = "A distinctive movie plot.";
        movie.SeriesName = " ";
        Assert.Null(movie.Key);
        movie.SeriesName = "The Movie";
        movie.IsMovie = false;
        Assert.Null(movie.Key);
        movie.Season = 0;
        movie.Episode = 0;
        Assert.False(key.Matches(movie.Key!));
    }

    [Fact]
    public void ConflictingMovieYearsAndContentIdsAreRejected()
    {
        var first = AddMovie("a", Start);
        var other = AddMovie("b", Start.AddDays(1));
        first.ProductionYear = 1980;
        other.ProductionYear = 2020;
        Assert.False(first.Key!.Matches(other.Key!));
        other.ProductionYear = null;
        Assert.True(first.Key!.Matches(other.Key!));
        first.ContentIds["Imdb"] = "tt1";
        other.ContentIds["imdb"] = "tt2";
        Assert.False(first.Key!.Matches(other.Key!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteMoviesRecoverAcrossRestartAndAreNeverDeleted(bool delete)
    {
        _settings.DeleteIncompleteCopies = delete;
        AddMovie("p1", Start);
        AddMovie("p2", Start.AddDays(1));
        AddMovie("wrong", Start.AddHours(1), "Different plot.");
        var original = _host.AddItem("/rec/a.ts");
        await RecordShow(10, season: null, episode: null);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);

        var service = NewService();
        var timer = _host.Timers.Single(t => t.ProgramId == "p2");
        var replacement = _host.AddItem("/rec/movie.ts");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, replacement.Path!, null, null);
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);
        Assert.Empty(_host.Deleted);
        Assert.Contains(original, _host.Items);
        Assert.Contains(IncompleteRecordingRules.Tag, original.Tags);
        AssertLogged(LogKind.Info, delete
            ? "Kept the incomplete copies of The Movie: movie copies are never deleted"
            : "Kept the incomplete copies of The Movie: deleting incomplete copies is turned off");
        Assert.Contains(replacement, _host.Items);
        Assert.Empty(replacement.Tags);
        Assert.Equal(CompleteShowingState.Done, ReadStore().Requests.Single().State);
    }

    [Fact]
    public async Task ACompleteDifferentMovieCannotDeleteTheOriginal()
    {
        _settings.DeleteIncompleteCopies = true;
        AddMovie("p1", Start);
        var original = _host.AddItem("/rec/a.ts");
        await RecordShow(10, season: null, episode: null);
        var wrong = AddMovie("wrong", Start.AddDays(1), "A different story.");
        var timer = new ScheduledTimer
        {
            TimerId = "wrong-timer",
            ProgramId = wrong.Id,
            StartDate = wrong.StartDate,
            EndDate = wrong.EndDate,
            Status = "New"
        };
        _host.Timers.Add(timer);
        _clock.UtcNow = wrong.StartDate;
        _host.StartRecording(timer, "/rec/wrong.ts", null, null);
        var service = NewService();
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);
        Assert.Empty(_host.Deleted);
        Assert.Contains(IncompleteRecordingRules.Tag, original.Tags);
        Assert.NotEqual(CompleteShowingState.Done, ReadStore().Requests.Single().State);
    }

    [Fact]
    public async Task ExplicitMovieRequestUsesTheNormalRecoveryLifecycle()
    {
        var movie = AddMovie(Guid.NewGuid().ToString("N"), Start);
        var next = AddMovie(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start;
        var (request, refusal) = await NewService().RequestCompleteShowingAsync(movie.Id, User, CancellationToken.None);
        Assert.Equal(CompleteShowingRefusal.None, refusal);
        Assert.True(request!.Explicit);
        Assert.True(request.Episode.IsMovie);
        Assert.Equal(new[] { next.Id }, _host.Scheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownEpisodeAndMovieOptionsShowTheNextAccessibleMatchingAiring(bool movie)
    {
        var source = movie ? AddMovie(Guid.NewGuid().ToString("N"), Start)
            : AddAiring(Guid.NewGuid().ToString("N"), Start);
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        var empty = (await service.GetCompleteShowingOptionsAsync(source.Id, User));
        Assert.True(empty.CanSearch);
        Assert.Null(empty.Showing);
        var hidden = movie ? AddMovie(Guid.NewGuid().ToString("N"), Start.AddHours(1))
            : AddAiring(Guid.NewGuid().ToString("N"), Start.AddHours(1));
        _host.HiddenPrograms.Add(hidden.Id);
        var next = movie ? AddMovie(Guid.NewGuid().ToString("N"), Start.AddDays(1))
            : AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        var options = (await service.GetCompleteShowingOptionsAsync(source.Id, User));
        Assert.True(options.CanSearch);
        Assert.Equal(next.Id, options.Showing!.ProgramId);
        Assert.Equal(next.StartDate, options.Showing.StartDate);
        Assert.Empty(_host.Scheduled);
    }

    private (GuideProgram Source, GuideProgram Future) AddUnnumberedShowings()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start, null, null);
        source.SeriesName = "Stephen King: A Necessary Evil";
        source.Overview = "Interviews explore the author's work.";
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1), null, null);
        future.SeriesName = source.SeriesName;
        future.Overview = source.Overview;
        _clock.UtcNow = Start.AddMinutes(5);
        return (source, future);
    }

    [Fact]
    public async Task AnUnnumberedSuggestionSchedulesOneAiringWithoutRecoveryOrDeletion()
    {
        var (source, future) = AddUnnumberedShowings();
        var service = NewService();
        var options = (await service.GetCompleteShowingOptionsAsync(source.Id, User));
        Assert.False(options.CanSearch);
        Assert.Equal(future.Id, options.Showing!.ProgramId);
        Assert.Empty(_host.Scheduled);
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, options.Showing, User, CancellationToken.None));
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, options.Showing, User, CancellationToken.None));
        Assert.Equal(new[] { future.Id }, _host.Scheduled);
        await service.RunAsync(CancellationToken.None);
        Assert.Empty(ReadStore().Requests);
        Assert.Empty(_host.Deleted);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("time")]
    [InlineData("past")]
    [InlineData("hidden")]
    [InlineData("missing")]
    public async Task AChangedOrUnavailableSuggestionIsNotScheduled(string change)
    {
        var (source, future) = AddUnnumberedShowings();
        var service = NewService();
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        switch (change)
        {
            case "description": future.Overview = "Changed plot."; break;
            case "time": future.StartDate = future.StartDate.AddHours(1); break;
            case "past": _clock.UtcNow = future.StartDate; break;
            case "hidden": _host.HiddenPrograms.Add(future.Id); break;
            case "missing": _host.Programs.Remove(future); break;
        }

        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task SuggestionsRequireDescriptionAccessAndAFutureMatchInTheCurrentGuide()
    {
        var (source, future) = AddUnnumberedShowings();
        var service = NewService();
        _host.HiddenPrograms.Add(source.Id);
        Assert.Null((await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing);
        _host.HiddenPrograms.Clear();
        _host.HiddenPrograms.Add(future.Id);
        Assert.Null((await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing);
        _host.HiddenPrograms.Clear();
        source.Overview = future.Overview = " ";
        Assert.Null((await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing);
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task VisibilityIsCheckedOnTheProgramsItemNotItsId()
    {
        // Emby's program ids are numbers, so only the item id says who can see a program.
        var source = AddAiring("1001", Start);
        source.ItemId = Guid.NewGuid();
        var future = AddAiring("1002", Start.AddDays(1));
        future.ItemId = Guid.NewGuid();
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();

        Assert.Equal("1002", (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing?.ProgramId);

        _host.HiddenPrograms.Add(future.ItemId.ToString("N"));
        Assert.Null((await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing);

        _host.HiddenPrograms.Add(source.ItemId.ToString("N"));
        Assert.False((await service.GetCompleteShowingOptionsAsync(source.Id, User)).CanSearch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A distinctive plot.")]
    public async Task NumberedMoviesPreserveExistingEpisodeIdentity(string? description)
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var legacy = source.Key!;
        source.IsMovie = true;
        source.Overview = description;
        Assert.False(source.Key!.IsMovie);
        Assert.True(legacy.Matches(source.Key));
        Assert.Equal(legacy.Id, source.Key.Id);
        var next = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        next.IsMovie = true;
        next.Overview = description;
        _clock.UtcNow = Start;
        var service = NewService();
        var first = await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        var second = await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        Assert.Equal(first.Request!.Id, second.Request!.Id);
        Assert.Single(ReadStore().Requests);
        Assert.Equal(new[] { next.Id }, _host.Scheduled);
    }

    [Fact]
    public async Task ConfirmedKnownAiringIsScheduledExactlyAndRevalidated()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var hidden = AddAiring(Guid.NewGuid().ToString("N"), Start.AddHours(1));
        _host.HiddenPrograms.Add(hidden.Id);
        var next = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        Assert.Equal(next.Id, offered.ProgramId);
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        Assert.Equal(new[] { next.Id }, _host.Scheduled);
        Assert.Equal(next.Id, ReadStore().Requests.Single().ScheduledProgramId);
        _host.HiddenPrograms.Add(next.Id);
        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
    }

    [Fact]
    public async Task AShowingAnotherUserAlreadyScheduledIsOfferedAsScheduledAndAdopted()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _host.Timers.Add(new ScheduledTimer { TimerId = "other", ProgramId = later.Id, StartDate = later.StartDate, EndDate = later.EndDate, Status = "New" });
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();

        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;

        Assert.Equal(later.Id, offered.ProgramId);
        Assert.True(offered.AlreadyScheduled);
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task AnExistingBookingIsOfferedAndADifferentShowingIsNotConfirmedInItsPlace()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var booked = AddAiring(Guid.NewGuid().ToString("N"), Start.AddHours(1));
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        Assert.Equal(booked.Id, (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!.ProgramId);
        _host.HiddenPrograms.Add(booked.Id);
        Assert.Null((await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing);
        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, SuggestedShowing.FromProgram(later), User, CancellationToken.None));
        var joined = await service.RequestCompleteShowingAsync(source.Id, Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(joined.Request);
        Assert.Equal(User, joined.Request!.RequestedByUserId);
        Assert.Single(_host.Scheduled);
    }

    [Fact]
    public async Task AFailedConfirmedBookingCannotCreateBackgroundRetryIntent()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        _host.SchedulingFails = true;
        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        Assert.Empty(ReadStore().Requests);
        _host.SchedulingFails = false;
        _clock.Advance(TimeSpan.FromMinutes(15));
        await NewService().RunAsync(CancellationToken.None);
        Assert.Empty(_host.Scheduled);
        Assert.Empty(ReadStore().Requests);
    }

    [Fact]
    public async Task JoiningAnAutomaticRequestDoesNotRestrictItsChannels()
    {
        var source = AddAiring("p1", Start);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        var service = await RecordShow(10);
        Assert.Null(ReadStore().Requests.Single().RequestedByUserId);
        var visibleSource = AddAiring(Guid.NewGuid().ToString("N"), Start);
        _host.HiddenPrograms.Add(future.Id);
        var joined = await service.RequestCompleteShowingAsync(visibleSource.Id, User, CancellationToken.None);
        Assert.NotNull(joined.Request);
        Assert.Null(joined.Request!.RequestedByUserId);
        Assert.False(await service.ScheduleSuggestedShowingAsync(visibleSource.Id, SuggestedShowing.FromProgram(future), User, CancellationToken.None));
        Assert.Null(ReadStore().Requests.Single().RequestedByUserId);
        Assert.Single(_host.Scheduled);
    }

    [Fact]
    public async Task AFailedConfirmationRestoresAnExistingWaitingRequest()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        var original = await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _host.SchedulingFails = true;
        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, SuggestedShowing.FromProgram(future), Guid.NewGuid(), CancellationToken.None));
        var saved = ReadStore().Requests.Single();
        Assert.Equal(original.Request!.Id, saved.Id);
        Assert.Equal(User, saved.RequestedByUserId);
        Assert.Null(saved.PendingProgramId);
        Assert.Equal(CompleteShowingState.Waiting, saved.State);
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task NumberedMovieSnapshotsUseTheExistingEpisodeKeyWithoutADescription()
    {
        var source = AddAiring("p1", Start);
        var legacy = source.Key!;
        source.IsMovie = true;
        var next = AddAiring("p2", Start.AddDays(1));
        next.IsMovie = true;
        await RecordShow(10);
        Assert.Equal(new[] { next.Id }, _host.Scheduled);
        Assert.Equal(legacy.Id, ReadStore().Records.Single().Episode!.Id);
        Assert.False(ReadStore().Records.Single().Episode!.IsMovie);
    }

    [Fact]
    public async Task AChangedPendingEpisodeIsNotScheduledAfterRestart()
    {
        var source = AddAiring("source", Start);
        var pending = AddAiring("pending", Start.AddHours(1));
        var next = AddAiring("next", Start.AddDays(1));
        var data = new IncompleteRecordingStoreData();
        data.Requests.Add(new CompleteShowingRequest
        {
            Episode = source.Key!,
            OpenedAt = Start,
            PendingProgramId = pending.Id,
        });
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(data));
        pending.Episode = 99;
        _clock.UtcNow = Start.AddMinutes(5);
        await NewService().RunAsync(CancellationToken.None);
        Assert.Equal(new[] { next.Id }, _host.Scheduled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AStaleConfirmationCannotRecreateACancelledTimerButANewConfirmationCan(bool optionsFirst, bool retainedTimer)
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var next = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        ListenForTimerEvents(service);
        await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        var timer = _host.Timers.Single();
        if (retainedTimer)
        {
            timer.Status = "Cancelled";
            timer.IsManual = true;
            service.MarkScheduleChanged();
        }
        else _host.CancelTimer(timer);
        if (optionsFirst) Assert.Equal(next.Id, (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!.ProgramId);
        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        await service.RunAsync(CancellationToken.None);
        Assert.Single(_host.Scheduled);
        Assert.Contains(ReadStore().Requests, r => r.State == CompleteShowingState.CancelledByUser);
        var fresh = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        Assert.Equal(next.Id, fresh.ProgramId);
        Assert.NotEqual(offered.CancellationVersion, fresh.CancellationVersion);
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, fresh, User, CancellationToken.None));
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, fresh, User, CancellationToken.None));
        Assert.Equal(new[] { next.Id, next.Id }, _host.Scheduled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AutomaticSeriesSkipsRemainEligibleForRecovery(bool requestAlreadyScheduled, bool pending)
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var next = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        if (requestAlreadyScheduled)
        {
            _host.SchedulingFails = pending;
            await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
            _host.SchedulingFails = false;
        }
        var timer = _host.Timers.FirstOrDefault() ?? new ScheduledTimer
        {
            TimerId = "series-skip",
            ProgramId = next.Id,
            StartDate = next.StartDate,
            EndDate = next.EndDate,
        };
        if (!_host.Timers.Contains(timer)) _host.Timers.Add(timer);
        timer.Status = "Cancelled";
        timer.IsManual = false;
        service.MarkScheduleChanged();
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing;
        Assert.Equal(next.Id, offered!.ProgramId);
        if (requestAlreadyScheduled)
        {
            await service.RunAsync(CancellationToken.None);
        }
        else
        {
            await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        }
        Assert.Equal("New", timer.Status);
        Assert.Equal(CompleteShowingState.Scheduled, ReadStore().Requests.Single().State);
        Assert.DoesNotContain(ReadStore().Requests, r => r.State == CompleteShowingState.CancelledByUser);
    }

    [Fact]
    public async Task ACanceledTimerReloadIsImmediatelyRetriedEvenWithAPreviouslyWarmCache()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var next = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        await service.GetCompleteShowingOptionsAsync(source.Id, User);
        service.MarkScheduleChanged();
        _host.TimerReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var aborted = service.GetCompleteShowingOptionsAsync(source.Id, User, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        _host.TimerReadGate = null;
        var timer = new ScheduledTimer
        {
            TimerId = "already-booked",
            ProgramId = next.Id,
            StartDate = next.StartDate,
            EndDate = next.EndDate,
            Status = "New",
        };
        _host.Timers.Add(timer);
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        Assert.Empty(_host.Scheduled);
        Assert.Equal(next.Id, ReadStore().Requests.Single().ScheduledProgramId);
    }

    [Fact]
    public async Task AStaleConfirmationOfACancelledShowingIsRefusedAndNotRecreated()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var first = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        var different = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(2));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        ListenForTimerEvents(service);
        await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        var stale = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        _host.CancelTimer(_host.Timers.Single());
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, SuggestedShowing.FromProgram(different), User, CancellationToken.None));
        Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, stale, User, CancellationToken.None));
        Assert.Equal(new[] { first.Id, different.Id }, _host.Scheduled);
        Assert.Equal(different.Id, ReadStore().Requests.Last().ScheduledProgramId);
    }

    [Fact]
    public async Task OptionsWaitCanBeCancelledWhileAWorkerHoldsTheLock()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        _clock.UtcNow = Start.AddMinutes(5);
        _host.TimerReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = NewService();
        var worker = service.RunAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var options = service.GetCompleteShowingOptionsAsync(source.Id, User, cancelled.Token);
        cancelled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => options);
        }
        finally
        {
            _host.TimerReadGate.SetResult();
            await worker;
        }
    }

    [Fact]
    public async Task PendingOptionsAndSelectedReplacementDoNotConflict()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var pending = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(2));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        _host.SchedulingFails = true;
        await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        var earlier = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        Assert.Equal(pending.Id, (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!.ProgramId);
        _host.SchedulingFails = false;
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, SuggestedShowing.FromProgram(earlier), User, CancellationToken.None));
        Assert.Equal(new[] { earlier.Id }, _host.Scheduled);
        Assert.Equal(earlier.Id, ReadStore().Requests.Single().ScheduledProgramId);
    }

    [Fact]
    public async Task ResponseUsesActualBookingAfterSourceEndsAndDoesNotInventOneOnFailure()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var next = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(25);
        var service = NewService();
        var booked = await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        Assert.Equal(CompleteShowingState.Scheduled, booked.Request!.State);
        Assert.Equal(next.StartDate, booked.Request.ScheduledStart);
        _host.HiddenPrograms.Add(next.Id);
        var joined = await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        Assert.Equal(next.StartDate, joined.Request!.ScheduledStart);
        Assert.Null(joined.Request.ScheduledChannelName);
        _host.HiddenPrograms.Clear();
        var other = AddAiring(Guid.NewGuid().ToString("N"), Start, episode: 88);
        AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1), episode: 88);
        _host.SchedulingFails = true;
        var failed = await service.RequestCompleteShowingAsync(other.Id, User, CancellationToken.None);
        Assert.Equal(CompleteShowingState.Waiting, failed.Request!.State);
        Assert.Null(failed.Request.ScheduledStart);
        Assert.Null(failed.Request.ScheduledChannelName);
    }

    [Fact]
    public async Task OptionsDoesNotRefetchEachGuideCandidateToCheckVisibility()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        for (var i = 1; i <= 10; i++)
        {
            var airing = AddAiring(Guid.NewGuid().ToString("N"), Start.AddHours(i));
            _host.HiddenPrograms.Add(airing.Id);
        }
        var visible = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var offered = await NewService().GetCompleteShowingOptionsAsync(source.Id, User);
        Assert.Equal(visible.Id, offered.Showing!.ProgramId);
        Assert.Equal(1, _host.ProgramLookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompletedUncertainSuggestionCannotDeleteOrCompleteTheOriginal(bool futureMarkedMovie)
    {
        _settings.DeleteIncompleteCopies = true;
        var (source, future) = AddUnnumberedShowings();
        var original = _host.AddItem("/rec/a.ts");
        var guideOriginal = AddAiring("p1", Start, null, null);
        guideOriginal.SeriesName = source.SeriesName;
        guideOriginal.Overview = source.Overview;
        var service = NewService();
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));
        service = await RecordShow(10, season: null, episode: null);
        Assert.Null(ReadStore().Records.Single().Episode);
        future.IsMovie = futureMarkedMovie;
        var timer = _host.Timers.Single(t => t.ProgramId == future.Id);
        var replacement = _host.AddItem("/rec/different-broadcast.ts");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, replacement.Path!, null, null);
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);
        Assert.Empty(_host.Deleted);
        Assert.Empty(ReadStore().Requests);
        Assert.False(ReadStore().Records.Single().Dismissed);
        Assert.Contains(IncompleteRecordingRules.Tag, original.Tags);
        Assert.Contains(original, _host.Items);
    }

    [Fact]
    public async Task AnExplicitRequestDoesNotPickAnAiringTheUserCancelled()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _host.Timers.Add(new ScheduledTimer
        {
            TimerId = "manually-cancelled",
            ProgramId = future.Id,
            StartDate = future.StartDate,
            EndDate = future.EndDate,
            Status = "Cancelled",
            IsManual = true,
        });
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();

        await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        await service.RunAsync(CancellationToken.None);

        var request = ReadStore().Requests.Single();
        Assert.Equal(CompleteShowingState.Waiting, request.State);
        Assert.Null(request.PendingProgramId);
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task ALiveTimerWinsOverAnOlderCancelledOneForTheSameShowing()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        var offered = (await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing!;
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, offered, User, CancellationToken.None));

        _host.Timers.Insert(0, new ScheduledTimer
        {
            TimerId = "cancelled-earlier",
            ProgramId = future.Id,
            StartDate = future.StartDate,
            EndDate = future.EndDate,
            Status = "Cancelled",
            IsManual = true,
        });
        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(CompleteShowingState.Scheduled, ReadStore().Requests.Single().State);
    }

    [Theory]
    [InlineData("options")]
    [InlineData("request")]
    [InlineData("suggestion")]
    public async Task ATimerBackendTimeoutIsAnUnavailableReadRatherThanACallerCancellation(string operation)
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        _host.TimerBackendTimesOut = true;
        var service = NewService();
        switch (operation)
        {
            case "options":
                Assert.Null((await service.GetCompleteShowingOptionsAsync(source.Id, User)).Showing);
                break;
            case "request":
                var requested = await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
                Assert.Equal(CompleteShowingState.Waiting, requested.Request!.State);
                Assert.Null(requested.Request.ScheduledStart);
                break;
            default:
                Assert.False(await service.ScheduleSuggestedShowingAsync(source.Id, SuggestedShowing.FromProgram(future), User, CancellationToken.None));
                break;
        }
        Assert.Empty(_host.Scheduled);
        _host.TimerBackendTimesOut = false;
        service.MarkScheduleChanged();
        Assert.True(await service.ScheduleSuggestedShowingAsync(source.Id, SuggestedShowing.FromProgram(future), User, CancellationToken.None));
        Assert.Equal(new[] { future.Id }, _host.Scheduled);
    }

    [Fact]
    public async Task AnUnownedCancellationExceptionDoesNotEndTheWatcher()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        _clock.UtcNow = Start.AddMinutes(5);
        var service = NewService();
        await service.RequestCompleteShowingAsync(source.Id, User, CancellationToken.None);
        var future = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _host.GuideBackendTimesOut = true;
        _host.GuideReadAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.TimerScheduled = _ => scheduled.TrySetResult();
        using var watcher = new IncompleteRecordingWatcher(service, _host, NoRecordingLog.Instance);
        using var stopping = new CancellationTokenSource();
        var running = watcher.RunAsync(stopping.Token);
        try
        {
            await _host.GuideReadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            _host.GuideBackendTimesOut = false;
            _host.RefreshGuide();
            await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.Equal(new[] { future.Id }, _host.Scheduled);
        }
        finally
        {
            stopping.Cancel();
            await running;
        }
        Assert.True(running.IsCompletedSuccessfully);
    }

    // Rules

    [Theory]
    [InlineData(32, 37, true)]
    [InlineData(35, 37, false)]
    [InlineData(33.2, 37, true)]
    [InlineData(0, 0, false)]
    public void ThresholdIsNinetyPercentOfThePaddedSchedule(double recorded, double expected, bool incomplete)
    {
        Assert.Equal(incomplete, IncompleteRecordingRules.IsIncomplete(recorded, expected, 90));
    }

    [Fact]
    public void OneFileMustCoverTheThresholdOnItsOwn()
    {
        static RecordingFragment File(double minutes) => new() { CoveredSeconds = minutes * 60 };

        Assert.Equal(RecordingVerdict.Incomplete, IncompleteRecordingRules.Classify(new[] { File(29), File(29) }, 60, 90));
        Assert.Equal(RecordingVerdict.Complete, IncompleteRecordingRules.Classify(new[] { File(5), File(55) }, 60, 90));
    }

    [Fact]
    public void EpisodeKeyNeedsSeasonAndEpisodeNumbers()
    {
        Assert.Null(EpisodeKey.Create("SH1", "News", null, 4, null));
        Assert.Null(EpisodeKey.Create("SH1", "News", 1, null, null));
        Assert.NotNull(EpisodeKey.Create(null, "Storage Wars", 1, 2, null));
    }

    [Fact]
    public void TitlesAreComparedOnlyWhenBothHaveOne()
    {
        var untitled = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, null)!;
        var pilot = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Pilot")!;
        var finale = EpisodeKey.Create("SH1", "Storage Wars", 3, 7, "Finale")!;

        Assert.True(untitled.Matches(pilot));
        Assert.False(pilot.Matches(finale));
    }

    [Fact]
    public void ChoosesTheEarliestLaterShowingPreferringHd()
    {
        var key = EpisodeKey.Create("SH001", "Storage Wars", 3, 7, null)!;
        var programs = new[]
        {
            AddAiring("past", Start.AddHours(-1), hd: true),
            AddAiring("other-episode", Start.AddHours(1), episode: 8),
            AddAiring("sd", Start.AddHours(2)),
            AddAiring("hd", Start.AddHours(2), hd: true),
            AddAiring("later", Start.AddHours(5)),
        };

        Assert.Equal("hd", IncompleteRecordingRules.ChooseShowing(key, programs, Start)?.Id);
    }

    [Theory]
    [InlineData(CompleteShowingState.Scheduled, "scheduled")]
    [InlineData(CompleteShowingState.Done, "recorded")]
    [InlineData(CompleteShowingState.Waiting, "notFound")]
    [InlineData(CompleteShowingState.GaveUp, "notFound")]
    [InlineData(CompleteShowingState.CancelledByUser, null)]
    public void RequestStatesCollapseToWhatClientsShow(string state, string? shown)
    {
        Assert.Equal(shown, IncompleteRecordingRules.DisplayState(new CompleteShowingRequest { State = state }));
    }

    // Service

    [Fact]
    public async Task ACutShortRecordingIsTaggedAndTheLaterShowingScheduled()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");

        var service = await RecordShow(writtenMinutes: 10);

        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);
        var state = Assert.Single(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
        Assert.Equal("scheduled", state.State);
        Assert.Equal(20, state.ExpectedMinutes);
    }

    [Fact]
    public async Task AFullRecordingIsNotFlagged()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");

        var service = await RecordShow(writtenMinutes: 20);

        Assert.Empty(item.Tags);
        Assert.Empty(_host.Scheduled);
        Assert.Empty(await service.GetStatesAsync(null, User, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutTheSettingTheRecordingIsOnlyFlagged()
    {
        _settings.AutoRecordComplete = false;
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");

        await RecordShow(writtenMinutes: 10);

        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);
        Assert.Empty(_host.Scheduled);
        AssertLogged(LogKind.Info, "Not looking for a later showing of Storage Wars S3E7: automatically recording a complete showing is turned off");
    }

    [Fact]
    public async Task AShowingAlreadyScheduledIsNotScheduledAgain()
    {
        AddAiring("p2", Start.AddDays(1));
        _host.Timers.Add(new ScheduledTimer { TimerId = "t2", ProgramId = "p2", StartDate = Start.AddDays(1), EndDate = Start.AddDays(1).AddMinutes(20), Status = "New" });

        await RecordShow(writtenMinutes: 10);

        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task ARemovedTagDismissesTheRecordAndIsNotPutBack()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        item.Tags = Array.Empty<string>();
        await service.OnItemUpdatedAsync(item, CancellationToken.None);
        await service.RunAsync(CancellationToken.None);

        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    [Fact]
    public async Task KeepAsCompleteRemovesTheTag()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        Assert.True(await service.DismissAsync(item.Id, User, CancellationToken.None));

        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    [Fact]
    public async Task AFailedTagRemovalOnKeepAsCompleteIsRetried()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        _host.TagWritesFail = true;
        Assert.True(await service.DismissAsync(item.Id, User, CancellationToken.None));

        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);

        _host.TagWritesFail = false;
        await NewService().RunAsync(CancellationToken.None);

        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ACompleteShowingDeletesTheIncompleteCopyOnlyWhenTurnedOn(bool turnedOn, bool keptAsComplete)
    {
        _settings.DeleteIncompleteCopies = turnedOn;
        AddAiring("p2", Start.AddDays(1));
        var incomplete = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);
        if (keptAsComplete)
        {
            await service.DismissAsync(incomplete.Id, User, CancellationToken.None);
        }

        var timer = _host.Timers.Single(t => t.ProgramId == "p2");
        var complete = _host.AddItem("/rec/b.ts");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, complete.Path!, 3, 7);
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(turnedOn && !keptAsComplete ? new[] { incomplete.Id } : Array.Empty<Guid>(), _host.Deleted);
        AssertLogged(LogKind.Info, keptAsComplete
            ? "Kept 1 copy(ies) of Storage Wars S3E7 treated as complete"
            : turnedOn
            ? $"Deleted incomplete copy {incomplete.Id} of Storage Wars S3E7: a complete copy recorded"
            : "Kept the incomplete copies of Storage Wars S3E7: deleting incomplete copies is turned off");
    }

    [Fact]
    public async Task ACopyThisServiceDeletedIsNotLoggedAsRemovedOutsideMoonfin()
    {
        _settings.DeleteIncompleteCopies = true;
        AddAiring("p2", Start.AddDays(1));
        var incomplete = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        var timer = _host.Timers.Single(t => t.ProgramId == "p2");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, "/rec/b.ts", 3, 7);
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);
        await service.OnItemRemovedAsync(incomplete.Id, CancellationToken.None);

        Assert.Equal(new[] { incomplete.Id }, _host.Deleted);
        Assert.Equal(0, CountLogged(LogKind.Info, "outside Moonfin"));
    }

    [Fact]
    public async Task AFailedDeleteIsLoggedAsAnErrorAndTheCopyStaysTagged()
    {
        _settings.DeleteIncompleteCopies = true;
        AddAiring("p2", Start.AddDays(1));
        var incomplete = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        var timer = _host.Timers.Single(t => t.ProgramId == "p2");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, "/rec/b.ts", 3, 7);
        _host.DeletesFail = true;
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);

        Assert.Contains(IncompleteRecordingRules.Tag, incomplete.Tags);
        AssertLogged(LogKind.Error, $"Could not delete incomplete copy {incomplete.Id} of Storage Wars S3E7");
    }

    // Jellyfin derives an item's id from its path, so a showing recorded where the incomplete
    // file was, before the library dropped the old item, is the same item.
    [Fact]
    public async Task ACompleteShowingRecordedAtTheIncompletePathIsKeptAndUntagged()
    {
        _settings.DeleteIncompleteCopies = true;
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        var timer = _host.Timers.Single(t => t.ProgramId == "p2");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, item.Path!, 3, 7);
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);

        Assert.Empty(_host.Deleted);
        Assert.Empty(item.Tags);
        Assert.Empty(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
        AssertLogged(LogKind.Info, "Kept /rec/a.ts for Storage Wars S3E7: the complete recording was written over the incomplete copy's file");
    }

    [Fact]
    public async Task AFailedTagRemovalOnTheCompleteCopyIsRetried()
    {
        _settings.DeleteIncompleteCopies = true;
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        var timer = _host.Timers.Single(t => t.ProgramId == "p2");
        _clock.UtcNow = timer.StartDate;
        _host.StartRecording(timer, item.Path!, 3, 7);
        while (_clock.UtcNow <= timer.EndDate)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        _host.TagWritesFail = true;
        await service.RunAsync(CancellationToken.None);

        Assert.Empty(_host.Deleted);
        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);

        _host.TagWritesFail = false;
        await NewService().RunAsync(CancellationToken.None);

        Assert.Empty(_host.Deleted);
        Assert.Empty(item.Tags);
    }

    // A post-processing script remuxes the short recording to a.mkv. The complete re-recording
    // goes to a.ts again and the script rewrites a.mkv, the same item, while it is judged.
    [Fact]
    public async Task ACompleteCopyRemuxedOverTheIncompleteRemuxIsKept()
    {
        _settings.DeleteIncompleteCopies = true;
        AddAiring("p2", Start.AddDays(1));
        var old = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);
        _host.Items.Remove(old);
        _host.Files.Remove(old.Path!);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);
        var remux = _host.AddItem("/rec/a.mkv");
        remux.DateCreated = _clock.UtcNow;
        _host.Files[remux.Path!] = 1;
        _host.LastWrite[remux.Path!] = _clock.UtcNow;
        _host.RenamedSiblingLookup = path => path == "/rec/a.ts" ? new[] { "/rec/a.mkv" } : Array.Empty<string>();
        await service.OnItemAddedAsync(remux, CancellationToken.None);
        Assert.Contains(IncompleteRecordingRules.Tag, remux.Tags);

        await Record(service, _host.Timers.Single(t => t.ProgramId == "p2"), "/rec/a.ts", writtenMinutes: 20);

        Assert.Empty(_host.Deleted);
        Assert.Empty(remux.Tags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACopyThatLostItsTagOrChangedSinceIsNotDeleted(bool tagLost)
    {
        _settings.DeleteIncompleteCopies = true;
        AddAiring("p2", Start.AddDays(1));
        var incomplete = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);
        var timer = _host.Timers.Single(t => t.ProgramId == "p2");

        // Without telling the service, as when the plugin was off or its save failed.
        if (tagLost)
        {
            incomplete.Tags = Array.Empty<string>();
        }
        else
        {
            _host.LastWrite[incomplete.Path!] = timer.StartDate.AddMinutes(1);
        }

        await Record(service, timer, "/rec/b.ts", writtenMinutes: 20);

        Assert.Empty(_host.Deleted);
    }

    [Fact]
    public async Task ARecordingTheUserStoppedIsNotFlaggedOrRecordedAgain()
    {
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");
        var timer = new ScheduledTimer { TimerId = "t1", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" };
        _host.Timers.Add(timer);
        var service = NewService();
        ListenForTimerEvents(service);
        _clock.UtcNow = Start;
        _host.StartRecording(timer, item.Path!, 3, 7);

        for (var minute = 0; minute <= 25; minute++)
        {
            if (minute == 5)
            {
                _host.CancelTimer(timer);
            }

            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Empty(item.Tags);
        Assert.Empty(_host.Scheduled);
    }

    [Fact]
    public async Task AnAiringTheUserCancelledIsNeverScheduledAgain()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddDays(2));
        _host.Timers.Add(new ScheduledTimer { TimerId = "t2", ProgramId = "p2", StartDate = Start.AddDays(1), EndDate = Start.AddDays(1).AddMinutes(20), Status = "Cancelled" });

        await RecordShow(writtenMinutes: 10);

        Assert.Equal(new[] { "p3" }, _host.Scheduled);
    }

    [Fact]
    public async Task AnAiringASeriesRuleAlreadyRecordsIsFollowedInsteadOfDoubled()
    {
        AddAiring("p2", Start.AddDays(1));
        var covered = AddAiring("p3", Start.AddDays(2));
        _host.Timers.Add(new ScheduledTimer { TimerId = "t3", ProgramId = "p3", StartDate = covered.StartDate, EndDate = covered.EndDate, Status = "New" });
        var item = _host.AddItem("/rec/a.ts");

        var service = await RecordShow(writtenMinutes: 10);

        Assert.Empty(_host.Scheduled);
        var state = Assert.Single(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
        Assert.Equal(covered.StartDate, state.ScheduledStart);
    }

    [Fact]
    public async Task AFileSizeThatCantBeReadIsNotCountedAsNotRecording()
    {
        var item = _host.AddItem("/rec/a.ts");
        var timer = new ScheduledTimer { TimerId = "t1", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" };
        _host.Timers.Add(timer);
        var service = NewService();
        _clock.UtcNow = Start;
        _host.StartRecording(timer, item.Path!, 3, 7);
        long size = 0;

        while (_clock.UtcNow <= timer.EndDate)
        {
            size += 1_000_000;
            var minute = (_clock.UtcNow - Start).TotalMinutes;
            if (minute is >= 8 and < 12)
            {
                _host.Files.Remove(item.Path!);
            }
            else
            {
                _host.Files[item.Path!] = size;
            }

            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);

        Assert.Empty(item.Tags);
    }

    [Fact]
    public async Task WithDetectionOffNothingIsTaggedOrScheduled()
    {
        _settings.Enabled = false;
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");

        var service = await RecordShow(writtenMinutes: 10);

        Assert.Empty(item.Tags);
        Assert.Empty(_host.Scheduled);
        Assert.True(service.NextRunAt() > _clock.UtcNow);
    }

    [Fact]
    public async Task AShortRecordingAfterACompleteCopyOpensNoNewRequest()
    {
        AddAiring("p2", Start.AddDays(1));
        var later = AddAiring("p3", Start.AddDays(2));
        AddAiring("p4", Start.AddDays(3));
        var service = await RecordShow(writtenMinutes: 10);
        await Record(service, _host.Timers.Single(t => t.ProgramId == "p2"), "/rec/b.ts", writtenMinutes: 20);

        var rule = new ScheduledTimer { TimerId = "t3", ProgramId = "p3", StartDate = later.StartDate, EndDate = later.EndDate, Status = "New" };
        _host.Timers.Add(rule);
        service.MarkScheduleChanged();
        await Record(service, rule, "/rec/c.ts", writtenMinutes: 10);

        Assert.Equal(new[] { "p2" }, _host.Scheduled);
    }

    [Fact]
    public async Task ARecordingWithoutEpisodeNumbersExplainsWhyNoShowingIsFollowed()
    {
        AddAiring("p2", Start.AddDays(1), season: null, episode: null);

        await RecordShow(writtenMinutes: 10, season: null, episode: null);

        Assert.Empty(_host.Scheduled);
        AssertLogged(LogKind.Info, "Not looking for a later showing of Storage Wars: it has no season and episode numbers");
    }

    [Fact]
    public async Task AMissingLaterShowingIsLoggedOnlyOnce()
    {
        var service = await RecordShow(writtenMinutes: 10);
        _clock.Advance(IncompleteRecordingRules.RequestSearchFallback);
        await service.RunAsync(CancellationToken.None);
        _clock.Advance(IncompleteRecordingRules.RequestSearchFallback);
        await service.RunAsync(CancellationToken.None);

        const string message = "No later showing of Storage Wars S3E7 in the guide yet";
        Assert.Equal(1, CountLogged(LogKind.Info, message));
        Assert.Equal(0, CountLogged(LogKind.Warn, message));
    }

    [Fact]
    public async Task GivingUpAfterShowingsThatNeverRecordIsAWarning()
    {
        var first = AddAiring("p2", Start.AddDays(1));
        var second = AddAiring("p3", Start.AddDays(2));
        var service = await RecordShow(writtenMinutes: 10);
        Assert.Equal(new[] { "p2" }, _host.Scheduled);

        _clock.UtcNow = first.EndDate.AddHours(2);
        await service.RunAsync(CancellationToken.None);
        AssertLogged(LogKind.Warn, "The later showing of Storage Wars S3E7 at");
        Assert.Equal(new[] { "p2", "p3" }, _host.Scheduled);

        _clock.UtcNow = second.EndDate.AddHours(2);
        await service.RunAsync(CancellationToken.None);
        AssertLogged(LogKind.Warn, "Gave up on Storage Wars S3E7");
    }

    [Fact]
    public async Task AProgramWithoutEpisodeNumbersGetsNoRequest()
    {
        var program = AddAiring(Guid.NewGuid().ToString("N"), Start, season: null, episode: null);

        var (request, refusal) = await NewService().RequestCompleteShowingAsync(program.Id, User, CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(CompleteShowingRefusal.NoEpisodeNumbers, refusal);
    }

    [Fact]
    public async Task AnIdThatIsNotAGuideProgramIsNotFound()
    {
        var (request, refusal) = await NewService().RequestCompleteShowingAsync(Guid.NewGuid().ToString("N"), User, CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(CompleteShowingRefusal.NotFound, refusal);
    }

    [Fact]
    public async Task TheStoreSurvivesARestart()
    {
        var item = _host.AddItem("/rec/a.ts");
        await RecordShow(writtenMinutes: 10);

        Assert.Single(await NewService().GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingADeletedOneOffTimerClosesItsRequest(bool apiId)
    {
        AddAiring("p2", Start.AddDays(1));
        var service = await RecordShow(writtenMinutes: 10);
        ListenForTimerEvents(service);

        _host.CancelTimer(_host.Timers.Single(t => t.ProgramId == "p2"), apiId);
        await service.RunAsync(CancellationToken.None);
        service.MarkGuideRefreshed();
        await service.RunAsync(CancellationToken.None);

        Assert.Single(_host.Scheduled);
        Assert.Equal(CompleteShowingState.CancelledByUser, Assert.Single(ReadStore().Requests).State);
        await NewService().RunAsync(CancellationToken.None);
        Assert.Single(_host.Scheduled);
    }

    [Fact]
    public async Task CancellationBeforeTheCreatedTimerEntersTheCacheIsRetained()
    {
        var original = AddAiring(Guid.NewGuid().ToString("N"), Start);
        AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        var service = NewService();
        ListenForTimerEvents(service);
        _host.TimerScheduled = timer => _host.CancelTimer(timer);

        await service.RequestCompleteShowingAsync(original.Id, User, CancellationToken.None);
        await service.RunAsync(CancellationToken.None);

        Assert.Single(_host.Scheduled);
        Assert.Equal(CompleteShowingState.CancelledByUser, Assert.Single(ReadStore().Requests).State);
    }

    [Fact]
    public async Task RemovingATimerWithoutCancellationStillSearchesForTheShowing()
    {
        AddAiring("p2", Start.AddDays(1));
        var service = await RecordShow(writtenMinutes: 10);
        _host.Timers.RemoveAll(t => t.ProgramId == "p2");

        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(2, _host.Scheduled.Count);
        Assert.Equal(CompleteShowingState.Scheduled, Assert.Single(ReadStore().Requests).State);
    }

    [Fact]
    public async Task ARemuxBesideTheRecordingAttachesToTheIncompleteRecording()
    {
        Directory.CreateDirectory(_dir);
        var original = Path.Combine(_dir, "show.ts");
        var remux = Path.Combine(_dir, "show.mkv");
        var record = new IncompleteRecord
        {
            JudgedAt = Start,
            Fragments = new() { new RecordingFragment { Path = original, FirstSampleAt = Start.AddMinutes(-20), Ended = true } },
        };
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new IncompleteRecordingStoreData { Records = new() { record } }));
        _clock.UtcNow = Start;
        _host.RenamedSiblingLookup = _ => new[] { remux };
        var item = _host.AddItem(remux);
        item.DateCreated = Start;

        await NewService().OnItemAddedAsync(item, CancellationToken.None);

        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);
        Assert.Contains(item.Id, Assert.Single(ReadStore().Records).ItemIds);
    }

    [Fact]
    public async Task ARecordingStartedLateIsIncompleteEvenIfItEndsBetweenSamples()
    {
        var service = NewService();
        var timer = new ScheduledTimer
        {
            TimerId = "t1",
            ProgramId = "p1",
            Status = "New",
            StartDate = Start,
            EndDate = Start.AddMinutes(20),
        };
        _clock.UtcNow = Start.AddMinutes(15);
        _host.Timers.Add(timer);
        service.MarkTimerCreated(timer);
        var item = _host.AddItem("/rec/a.ts");

        _clock.Advance(TimeSpan.FromSeconds(30));
        _host.StartRecording(timer, item.Path!, 3, 7);
        _host.Write();
        await service.RunAsync(CancellationToken.None);
        _clock.UtcNow = Start.AddMinutes(21);
        _host.StopRecording(timer);
        await service.RunAsync(CancellationToken.None);

        Assert.Equal(RecordingVerdict.Incomplete, Assert.Single(ReadStore().Snapshots).Verdict);
        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecordingEndingDuringASamplingGapRemainsUnknown(bool restart)
    {
        var timer = new ScheduledTimer
        {
            TimerId = "t1",
            ProgramId = "p1",
            Status = "New",
            StartDate = Start,
            EndDate = Start.AddMinutes(20),
        };
        _host.Timers.Add(timer);
        var item = _host.AddItem("/rec/full.ts");
        var service = NewService();
        _clock.UtcNow = Start;
        _host.StartRecording(timer, item.Path!, 3, 7);
        for (var minute = 0; minute <= 10; minute++)
        {
            _host.Write();
            await service.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _clock.UtcNow = Start.AddMinutes(21);
        _host.StopRecording(timer);
        if (restart)
        {
            service.Dispose();
            service = NewService();
        }

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(item.Tags);
        Assert.Empty(_host.Scheduled);
        var snapshot = Assert.Single(ReadStore().Snapshots);
        Assert.Equal(RecordingVerdict.Unknown, snapshot.Verdict);
        Assert.Equal(10 * 60, Assert.Single(snapshot.Fragments).UnknownSeconds);
    }

    [Fact]
    public async Task AnUpdateRaisedBeforeTheTagWasAddedDoesNotDismiss()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);
        var raisedBeforeTagging = new LibraryItem { Id = item.Id, Path = item.Path, Tags = Array.Empty<string>() };

        await service.OnItemUpdatedAsync(raisedBeforeTagging, CancellationToken.None);

        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);
        Assert.Single(await service.GetStatesAsync(new[] { item.Id }, User, CancellationToken.None));
    }

    // Deleted, the record waits out the attach window. Remuxed, it lives on through the new file.
    // Either way the server can write a later recording of the episode to the original path.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALaterRecordingAtTheSamePathIsNotAttached(bool remuxed)
    {
        var old = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);
        _host.Items.Remove(old);
        _host.Files.Remove(old.Path!);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);
        if (remuxed)
        {
            var remux = _host.AddItem("/rec/a.mkv");
            remux.DateCreated = _clock.UtcNow;
            _host.RenamedSiblingLookup = _ => new[] { remux.Path! };
            await service.OnItemAddedAsync(remux, CancellationToken.None);
            Assert.Contains(IncompleteRecordingRules.Tag, remux.Tags);
            _host.RenamedSiblingLookup = null;
        }

        _clock.Advance(remuxed ? TimeSpan.FromDays(2) : TimeSpan.FromHours(2));
        var timer = new ScheduledTimer
        {
            TimerId = "t2",
            ProgramId = "p2",
            StartDate = _clock.UtcNow,
            EndDate = _clock.UtcNow.AddMinutes(20),
            Status = "New",
        };
        _host.Timers.Add(timer);
        service.MarkScheduleChanged();
        await service.RunAsync(CancellationToken.None);
        _host.StartRecording(timer, "/rec/a.ts", 3, 7);
        var rerecording = _host.AddItem("/rec/a.ts");
        rerecording.DateCreated = _clock.UtcNow;

        await service.OnItemAddedAsync(rerecording, CancellationToken.None);

        Assert.Empty(rerecording.Tags);
    }

    [Fact]
    public async Task ARecordingFileAddedLateIsStillAttached()
    {
        var service = await RecordShow(writtenMinutes: 10);
        _clock.Advance(TimeSpan.FromDays(2));
        var item = _host.AddItem("/rec/a.ts");
        item.DateCreated = Start;

        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.Contains(IncompleteRecordingRules.Tag, item.Tags);
    }

    [Fact]
    public async Task ATimerEventWithoutAnIdIsIgnored()
    {
        var service = NewService();

        service.MarkTimerCreated(new ScheduledTimer { TimerId = null!, ProgramId = "p1" });
        service.MarkTimerCancelled(null!);
        await service.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ScheduledShowingsSurviveAFailedTimerLoad()
    {
        AddAiring("p2", Start.AddDays(1));
        AddAiring("p3", Start.AddHours(2));
        await RecordShow(writtenMinutes: 10);
        _host.Programs.RemoveAll(p => p.Id == "p3");
        AddAiring("p3", _clock.UtcNow.AddHours(2));
        _host.TimersUnavailable = true;

        var restarted = NewService();
        await restarted.RunAsync(CancellationToken.None);

        var request = Assert.Single(ReadStore().Requests);
        Assert.Equal(CompleteShowingState.Scheduled, request.State);
        Assert.Single(_host.Scheduled);
    }

    [Fact]
    public async Task OnlyLibraryEventsARecordCanConcernAreHandled()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = await RecordShow(writtenMinutes: 10);

        Assert.True(service.MayConcernItem(item.Id));
        Assert.False(service.MayConcernItem(Guid.NewGuid()));
        Assert.True(service.MayConcernAddedItem(new LibraryItem { Path = "/rec/a.mkv" }));
        Assert.False(service.MayConcernAddedItem(new LibraryItem { Path = "/movies/other.mkv" }));
    }

    [Fact]
    public async Task TheNextRunIsWorkedOutByTheLastPass()
    {
        var timer = new ScheduledTimer { TimerId = "t1", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" };
        _host.Timers.Add(timer);
        var service = NewService();

        await service.RunAsync(CancellationToken.None);
        Assert.Equal(_clock.UtcNow + IncompleteRecordingRules.SafetyNetInterval, service.NextRunAt());

        _clock.UtcNow = Start.AddMinutes(1);
        _host.StartRecording(timer, "/rec/a.ts", 3, 7);
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(_clock.UtcNow + IncompleteRecordingRules.SampleInterval, service.NextRunAt());
    }
}
