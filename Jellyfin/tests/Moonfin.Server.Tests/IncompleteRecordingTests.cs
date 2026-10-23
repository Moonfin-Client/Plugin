using System.Text.Json;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

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

    public List<GuideProgram> Programs { get; } = new();

    public List<LibraryItem> Items { get; } = new();

    public List<string> Scheduled { get; } = new();

    public List<Guid> Deleted { get; } = new();

    public Action<ScheduledTimer>? TimerScheduled { get; set; }

    public Func<string, IReadOnlyList<string>>? RenamedSiblingLookup { get; set; }

    public bool TimersUnavailable { get; set; }

    public Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken) =>
        TimersUnavailable
            ? Task.FromException<IReadOnlyList<ScheduledTimer>>(new InvalidOperationException("Live TV is not ready"))
            : Task.FromResult<IReadOnlyList<ScheduledTimer>>(Timers.ToList());

    public string? GetActiveRecordingPath(string timerId) => ActivePaths.GetValueOrDefault(timerId);

    public ActiveRecordingTimer? GetActiveRecordingTimer(string path) => ActiveTimers.GetValueOrDefault(path);

    public long? GetFileLength(string path) => Files.TryGetValue(path, out var size) ? size : null;

    public GuideProgram? GetProgram(string programId) => Programs.FirstOrDefault(p => p.Id == programId);

    public IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after) =>
        Programs.Where(p => p.StartDate > after).ToList();

    public Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken)
    {
        Scheduled.Add(programId);
        var program = Programs.First(p => p.Id == programId);
        var timer = new ScheduledTimer
        {
            ApiId = "api-" + programId,
            TimerId = "t-" + programId,
            ProgramId = programId,
            StartDate = program.StartDate,
            EndDate = program.EndDate,
            Status = "New",
        };
        Timers.Add(timer);
        TimerCreated?.Invoke(this, timer);
        TimerScheduled?.Invoke(timer);
        return Task.FromResult(ScheduleResult.Created);
    }

    public LibraryItem? FindItemByPath(string path) => Items.FirstOrDefault(i => i.Path == path);

    public bool FileExists(string path) => Files.ContainsKey(path);

    public IReadOnlyList<string> FindRenamedSiblings(string path) => RenamedSiblingLookup?.Invoke(path) ?? Array.Empty<string>();

    public bool IsVisibleTo(Guid itemId, Guid userId) => true;

    public IReadOnlySet<Guid> VisibleTo(IReadOnlyCollection<Guid> itemIds, Guid userId) => itemIds.ToHashSet();

    public IReadOnlyList<string>? GetTags(Guid itemId) => Items.FirstOrDefault(i => i.Id == itemId)?.Tags;

    public void CancelTimer(ScheduledTimer timer, bool apiId = false)
    {
        Timers.Remove(timer);
        ActivePaths.Remove(timer.TimerId);
        TimerCancelled?.Invoke(this, apiId ? timer.ApiId! : timer.TimerId);
        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken)
    {
        var item = Items.First(i => i.Id == itemId);
        var tags = item.Tags.Where(t => t != tag).ToList();
        if (present)
        {
            tags.Add(tag);
        }

        item.Tags = tags;
        return Task.CompletedTask;
    }

    public void DeleteItem(Guid itemId)
    {
        Items.RemoveAll(i => i.Id == itemId);
        Deleted.Add(itemId);
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
        new(_host, new NoOpLogger<IncompleteRecordingService>(), _clock, StorePath, () => _settings);

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
    public async Task ARemuxBesideItsMetadataAttachesToTheIncompleteRecording()
    {
        Directory.CreateDirectory(_dir);
        var original = Path.Combine(_dir, "show.ts");
        var remux = Path.Combine(_dir, "show.mkv");
        File.WriteAllText(remux, "video");
        File.WriteAllText(Path.Combine(_dir, "show.nfo"), "metadata");
        File.WriteAllText(Path.Combine(_dir, "show.srt"), "subtitles");
        var record = new IncompleteRecord
        {
            JudgedAt = Start,
            Fragments = new() { new RecordingFragment { Path = original, FirstSampleAt = Start.AddMinutes(-20), Ended = true } },
        };
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new IncompleteRecordingStoreData { Records = new() { record } }));
        _clock.UtcNow = Start;
        using var context = new RecordingHostTestContext();
        _host.RenamedSiblingLookup = context.Host.FindRenamedSiblings;
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

    // Deleted, the record waits out the attach window; remuxed, it lives on through the new file.
    // Either way Jellyfin can write a later recording of the episode to the original path.
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
