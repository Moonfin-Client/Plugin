using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Moonfin.LiveTv.Tests;

/// <summary>
/// Counts saves and behaves like each server's AtomicFile: writes go to a temp file that is moved
/// over the target, and a read that can't use the main file falls back to the .bak and copies it
/// over the main file (promotion). The last contents written are also kept in memory, so tests can
/// check the store without opening a file a background save may be replacing.
/// </summary>
internal sealed class CountingStateFile : IStateFile
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);
    private int _writes;

    public int Writes => Volatile.Read(ref _writes);

    /// <summary>The contents last written to a path, or null when nothing was written to it yet.</summary>
    public string? LastWritten(string path)
    {
        lock (_gate)
        {
            return _written.TryGetValue(path, out var contents) ? contents : null;
        }
    }

    public T? Read<T>(string path, Func<string, T?> parse)
        where T : class
    {
        if (TryParse(path, parse, out var fromMain))
        {
            return fromMain;
        }

        var backup = path + ".bak";
        if (TryParse(backup, parse, out var fromBackup))
        {
            File.Copy(backup, path, overwrite: true);
            return fromBackup;
        }

        return null;
    }

    /// <summary>Every write fails as storage that went away does.</summary>
    public bool FailWrites { get; set; }

    public void Write(string path, string contents)
    {
        if (FailWrites)
        {
            throw new IOException("storage unavailable");
        }

        Interlocked.Increment(ref _writes);
        lock (_gate)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    break;
                }
                // A test may be reading the file this moment.
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 100)
                {
                    Thread.Sleep(5);
                }
            }

            _written[path] = contents;
        }
    }

    /// <summary>Reads a file without blocking a writer, retrying while one is replacing it.</summary>
    public static string ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 100)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static bool TryParse<T>(string path, Func<string, T?> parse, out T? result)
        where T : class
    {
        result = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            result = parse(ReadShared(path));
            return result != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class RecordingOwnershipTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static readonly Guid Jordan = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Alex = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Sam = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moonfin-owners-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(Start.AddHours(-1));
    private readonly FakeRecordingHost _host = new();
    private readonly ListRecordingLog _log = new();
    private readonly CountingStateFile _stateFile = new();
    private bool _enabled = true;

    public RecordingOwnershipTests()
    {
        _host.Clock = () => _clock.UtcNow;
        _host.Users.Add(new ServerUser(Jordan, "Jordan", "jordan-tag", false));
        _host.Users.Add(new ServerUser(Alex, "Alex", "alex-tag", false));
        _host.Users.Add(new ServerUser(Sam, "Sam", null, false));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A background save from a stopped watcher; the temp folder is left.
        }
    }

    private string StorePath => Path.Combine(_dir, RecordingOwnershipService.StoreFileName);

    private string IncompleteStorePath => Path.Combine(_dir, "incomplete.json");

    // A new instance is a new server process.
    private RecordingOwnershipService NewService() =>
        new(_host, _log, _stateFile, () => _clock.UtcNow, StorePath, () => _enabled);

    // The store as last saved: from the state file's in-memory copy, so a poll never holds the
    // file open while a watcher's background save replaces it; from disk before the first save.
    private OwnershipStoreData ReadStore() =>
        JsonSerializer.Deserialize<OwnershipStoreData>(_stateFile.LastWritten(StorePath) ?? CountingStateFile.ReadShared(StorePath))!;

    private GuideProgram AddProgram(string id, DateTime start, string series = "Bonanza", int? season = 1, int? episode = 1)
    {
        var program = new GuideProgram
        {
            Id = id,
            ItemId = Guid.TryParse(id, out var itemId) ? itemId : Guid.NewGuid(),
            SeriesId = "SH001",
            SeriesName = series,
            Season = season,
            Episode = episode,
            StartDate = start,
            EndDate = start.AddMinutes(20),
        };
        _host.Programs.Add(program);
        return program;
    }

    private ScheduledTimer TimerOf(string programId) => _host.Timers.Single(t => t.ProgramId == programId);

    private static IReadOnlyList<string> TagsOf(LibraryItem item) => item.Tags.ToList();

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }

    private static async Task<T> Bounded<T>(Task<T> task, string what)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Bound));
        Assert.True(finished == task, what + " did not finish within " + Bound.TotalSeconds + " s (deadlock?)");
        return await task;
    }

    private sealed class RunningWatcher : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly RecordingOwnershipWatcher _watcher;
        private readonly Task _run;

        public RecordingOwnershipWatcher Watcher => _watcher;

        public RunningWatcher(FakeRecordingHost host, RecordingOwnershipService service, TimeSpan? followUp, Func<TimeSpan, CancellationToken, Task>? followUpDelay = null)
        {
            _watcher = followUpDelay != null
                ? new RecordingOwnershipWatcher(host, service, NoRecordingLog.Instance, followUpDelay)
                : followUp is { } delay
                    ? new RecordingOwnershipWatcher(host, service, NoRecordingLog.Instance, delay)
                    : new RecordingOwnershipWatcher(host, service, NoRecordingLog.Instance);

            // Subscribes to the host's events before its first await.
            _run = _watcher.RunAsync(_cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await Task.WhenAny(_run, Task.Delay(Bound));
            _watcher.Dispose();
            _cts.Dispose();
        }
    }

    // Starts a watcher and waits until its first sync has listed the timers (and, while something
    // records, listed them again for its id map) and released the service's lock.
    private async Task<RunningWatcher> StartWatcher(
        RecordingOwnershipService service,
        TimeSpan? followUp = null,
        int expectedReads = 1,
        Func<TimeSpan, CancellationToken, Task>? followUpDelay = null)
    {
        var before = _host.TimerReads;
        var watcher = new RunningWatcher(_host, service, followUp, followUpDelay);
        await WaitFor(() => _host.TimerReads >= before + expectedReads, "the watcher's first sync");

        // The fake counts a listing as it starts, so the id map is only known once it is swapped in.
        if (expectedReads > 1)
        {
            await WaitFor(() => watcher.Watcher.ServiceIdListings >= 1, "the watcher's id listing");
        }

        await Bounded(service.GetMineAsync(Jordan, CancellationToken.None), "a call after the first sync");
        return watcher;
    }

    // Plan: T1 plugin list

    [Fact]
    public async Task RecordProgram_CreatesTimerAndOwnsIt()
    {
        AddProgram("p1", Start);
        var service = NewService();

        var outcome = await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal("api-p1", outcome!.Id);
        Assert.False(outcome.Joined);
        Assert.Equal(new[] { new OwnerInfo(Jordan, "Jordan", "jordan-tag") }, outcome.Owners);
        Assert.Equal(new[] { "p1" }, _host.Scheduled);
        Assert.Single(_host.Timers);
        Assert.Equal(new[] { "api-p1" }, (await service.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);

        var saved = ReadStore();
        Assert.Equal(1, saved.Version);
        Assert.Equal(new[] { Jordan }, saved.Timers["api-p1"]);
        Assert.Equal("t-p1", saved.TimerServiceIds["api-p1"]);
        Assert.Equal("livetv-recording-owners.json", RecordingOwnershipService.StoreFileName);
    }

    [Fact]
    public async Task RecordProgram_ExistingTimer_JoinsWithoutSecondTimer()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);

        var outcome = await service.RecordProgramAsync("p1", Alex, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Joined);
        Assert.Equal("api-p1", outcome.Id);
        Assert.Single(_host.Timers);
        Assert.Single(_host.Scheduled);
        Assert.Equal(new[] { Jordan, Alex }, outcome.Owners.Select(o => o.UserId));
    }

    [Fact]
    public async Task RecordSeries_ExistingRule_JoinsAndIgnoresSettings()
    {
        AddProgram("p1", Start);
        var service = NewService();
        var newOnly = new Dictionary<string, JsonElement> { ["RecordNewOnly"] = JsonSerializer.SerializeToElement(true) };
        var everything = new Dictionary<string, JsonElement> { ["RecordNewOnly"] = JsonSerializer.SerializeToElement(false) };

        var created = await service.RecordSeriesAsync("p1", Jordan, newOnly, CancellationToken.None);

        // The server makes the rule's timers.
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-s1", TimerId = "t-s1", ProgramId = "p1", SeriesTimerId = created!.Id, StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });

        var joined = await service.RecordSeriesAsync("p1", Alex, everything, CancellationToken.None);

        Assert.False(created.Joined);
        Assert.NotNull(joined);
        Assert.True(joined!.Joined);
        Assert.Equal(created.Id, joined.Id);
        Assert.Single(_host.SeriesRules);

        // Only the creating call's settings ever reached the server: RecordNewOnly stays true.
        var creation = Assert.Single(_host.SeriesCreations);
        Assert.True(creation.Overrides!["RecordNewOnly"].GetBoolean());
        Assert.Equal(new[] { Jordan, Alex }, joined.Owners.Select(o => o.UserId));
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Timers["api-s1"]);
    }

    [Fact]
    public async Task Sync_TimerMovesBetweenRules_OwnersOnlyAdded()
    {
        AddProgram("p1", Start);
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza"));
        _host.SeriesRules.Add(new SeriesRule("rule-b", null, "Bonanza"));
        var timer = new ScheduledTimer { ApiId = "api-1", TimerId = "t-1", ProgramId = "p1", SeriesTimerId = "rule-a", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" };
        _host.Timers.Add(timer);
        // A rule can only be claimed by someone who can see what it records, so rule-b needs a timer.
        AddProgram("p2", Start.AddDays(1));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-2", TimerId = "t-2", ProgramId = "p2", SeriesTimerId = "rule-b", StartDate = Start.AddDays(1), EndDate = Start.AddDays(1).AddMinutes(20), Status = "New" });
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Series, "rule-a", Jordan, CancellationToken.None);
        await service.ClaimAsync(OwnedKind.Series, "rule-b", Alex, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Timers["api-1"]);

        timer.SeriesTimerId = "rule-b";
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Timers["api-1"]);

        timer.SeriesTimerId = "rule-a";
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Timers["api-1"]);
    }

    [Fact]
    public async Task ItemAdded_MatchesRecordedPath_CopiesOwnersAndTags()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        _host.StartRecording(TimerOf("p1"), "/rec/a.ts", 1, 1);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal("/rec/a.ts", ReadStore().TimerPaths["api-p1"]);

        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.Equal(new[] { "Recorded by Jordan", "Recorded by Alex" }, TagsOf(item));
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { item.Id }, (await service.GetMineAsync(Alex, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task ItemRemovedThenReAdded_WithinSevenDays_RestoresOwners()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Alex, CancellationToken.None);

        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);

        _clock.Advance(TimeSpan.FromDays(6));
        await service.SyncAsync(CancellationToken.None);
        await NewService().OnItemAddedAsync(item, CancellationToken.None);

        var restarted = NewService();
        Assert.Equal(new[] { item.Id }, (await restarted.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[item.Id]);
        Assert.Empty(ReadStore().Removed);
    }

    [Fact]
    public async Task ItemRemoved_AfterSevenDays_Forgotten()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await service.SyncAsync(CancellationToken.None);
        Assert.Empty(ReadStore().Removed);

        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Empty(ReadStore().Items);
    }

    [Fact]
    public async Task ItemRemoved_AfterSevenDays_NoSync_NotRestored()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);

        // The server was down past the retention: its startup scan re-adds the item before any sync.
        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var back = new LibraryItem { Id = item.Id, Path = item.Path };
        _host.Items.Add(back);
        var tagWrites = _host.TagWrites.Count;
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        var saved = ReadStore();
        Assert.Empty(saved.Items);
        Assert.Empty(saved.Removed);
        Assert.Equal(tagWrites, _host.TagWrites.Count);
        Assert.Empty(back.Tags);
    }

    // Fix 2: a rerun recorded to a deleted recording's file has the same item id.

    // Jordan's recording of the episode at /rec/bonanza.ts, then deleted; returns the item.
    private async Task<LibraryItem> DeletedJordanRecording(RecordingOwnershipService service)
    {
        var old = _host.AddItem("/rec/bonanza.ts");
        await service.ClaimAsync(OwnedKind.Item, old.Id.ToString(), Jordan, CancellationToken.None);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(old));
        _host.Items.Remove(old);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);
        Assert.True(ReadStore().Removed.ContainsKey(old.Id));
        return old;
    }

    // The rerun's new library item: same path, so the same id, with fresh metadata.
    private LibraryItem Rerun(LibraryItem old)
    {
        var item = new LibraryItem { Id = old.Id, Path = old.Path };
        _host.Items.Add(item);
        return item;
    }

    [Fact]
    public async Task ItemReAdded_ByRerunTimerOnSamePath_TakesTimerOwnersNotTombstone()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);

        _clock.Advance(TimeSpan.FromDays(2));
        AddProgram("p1", _clock.UtcNow, episode: 5);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        _host.StartRecording(TimerOf("p1"), old.Path!, 1, 5);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(old.Path, ReadStore().TimerPaths["api-p1"]);

        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        var saved = ReadStore();
        Assert.Equal(new[] { Alex }, saved.Items[item.Id]);
        Assert.Contains("Recorded by Alex", TagsOf(item));
        Assert.DoesNotContain("Recorded by Jordan", TagsOf(item));
        Assert.False(saved.Removed.ContainsKey(item.Id));
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task RerunAttachedBySync_DiscardsTombstone_LaterReAddRestoresNewOwnersOnly()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);

        AddProgram("p1", Start, episode: 5);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        var timer = TimerOf("p1");
        _host.StartRecording(timer, old.Path!, 1, 5);

        // No ItemAdded: the sync finds the item by the file it captures.
        var item = Rerun(old);
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Equal(new[] { Alex }, saved.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
        Assert.False(saved.Removed.ContainsKey(item.Id));

        // The recording finishes and its timer is forgotten; then the item leaves and comes back.
        _host.StopRecording(timer);
        _host.Timers.Remove(timer);
        await service.SyncAsync(CancellationToken.None);
        Assert.Empty(ReadStore().Timers);
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        var back = Rerun(old);
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.Equal(new[] { Alex }, ReadStore().Items[back.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(back));
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task Rerun_FinishedTimerOfDeletedRecordingOnSamePath_DoesNotOwnIt()
    {
        // Jordan's recording finishes and is deleted before a sync forgets its timer.
        AddProgram("p0", _clock.UtcNow);
        var service = NewService();
        await service.RecordProgramAsync("p0", Jordan, CancellationToken.None);
        var first = TimerOf("p0");
        _host.StartRecording(first, "/rec/bonanza.ts", 1, 5);
        await service.SyncAsync(CancellationToken.None);
        var old = _host.AddItem("/rec/bonanza.ts");
        await service.OnItemAddedAsync(old, CancellationToken.None);
        _host.StopRecording(first);

        // A restart in between, so the removal can't tell the timer recorded this item.
        service = NewService();
        _host.Items.Remove(old);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(old.Path, ReadStore().TimerPaths["api-p0"]);

        // Alex records the rerun to the same file.
        AddProgram("p1", _clock.UtcNow, episode: 5);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        _host.StartRecording(TimerOf("p1"), old.Path!, 1, 5);
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Equal(new[] { Alex }, saved.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
        Assert.Empty(saved.Removed);
        Assert.False(saved.Timers.ContainsKey("api-p0"));
    }

    // The recording timer's owners replace the owners of the item it writes.

    // A timer nobody owns through Moonfin (an official client, say) recording to a path now.
    private ScheduledTimer UnownedTimerRecording(string path, string id = "x")
    {
        var program = AddProgram("p-" + id, _clock.UtcNow, episode: 5);
        var timer = new ScheduledTimer { ApiId = "api-" + id, TimerId = "t-" + id, ProgramId = program.Id, StartDate = _clock.UtcNow, EndDate = _clock.UtcNow.AddMinutes(20), Status = "New" };
        _host.Timers.Add(timer);
        _host.StartRecording(timer, path, 1, 5);
        return timer;
    }

    // A timer a user owns through Moonfin, recording to a path now.
    private async Task<ScheduledTimer> OwnedTimerRecording(RecordingOwnershipService service, string programId, Guid owner, string path)
    {
        AddProgram(programId, _clock.UtcNow);
        await service.RecordProgramAsync(programId, owner, CancellationToken.None);
        var timer = TimerOf(programId);
        _host.StartRecording(timer, path, 1, 1);
        return timer;
    }

    // Jordan claims a recording, then the item leaves.
    private async Task<LibraryItem> ClaimedThenRemoved(RecordingOwnershipService service, string path)
    {
        var item = _host.AddItem(path);
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        return item;
    }

    [Fact]
    public async Task Rerun_ByUnownedTimer_OnTombstonedPath_EndsWithNoOwners()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);
        _clock.Advance(TimeSpan.FromDays(2));
        UnownedTimerRecording(old.Path!);

        // The item comes back before a sync has seen the timer record, so it is restored its old owners.
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));

        // The sync that sees it recording puts that right.
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.False(saved.Items.ContainsKey(item.Id));
        Assert.Empty(saved.Removed);
        Assert.Empty(TagsOf(item));
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Equal(old.Path, saved.TimerPaths["api-x"]);
    }

    [Fact]
    public async Task Rerun_ByUnownedTimer_SyncFindsItemFirst_DropsTombstoneAndOwners()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);
        UnownedTimerRecording(old.Path!);

        var item = Rerun(old);
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Items);
        Assert.Empty(saved.Removed);
        Assert.Empty(TagsOf(item));
    }

    [Fact]
    public async Task Rerun_ByOwnedTimer_ItemAddedBeforePathNoted_SyncReplacesTheTombstonedOwners()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);
        await OwnedTimerRecording(service, "p1", Alex, old.Path!);

        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
        Assert.Empty(ReadStore().Removed);
    }

    // The recording was deleted and its path recorded again while the feature was off, so the
    // removal was never heard: the new item has the same id and the owners are still in the store.

    [Fact]
    public async Task StaleItemOwners_SwitchWasOff_UnownedRerunClearsThem()
    {
        var service = NewService();
        var item = _host.AddItem("/rec/bonanza.ts");
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);

        UnownedTimerRecording(item.Path!);
        await service.SyncAsync(CancellationToken.None);

        Assert.Empty(ReadStore().Items);
        Assert.Empty(TagsOf(item));
    }

    [Fact]
    public async Task StaleItemOwners_SwitchWasOff_OwnedRerunReplacesNotMerges()
    {
        var service = NewService();
        var item = _host.AddItem("/rec/bonanza.ts");
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);

        await OwnedTimerRecording(service, "p1", Alex, item.Path!);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
    }

    [Fact]
    public async Task MissingItem_OnSync_BecomesTombstone()
    {
        var service = NewService();
        var item = _host.AddItem("/rec/a.ts");
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        // The removal event is ignored while the feature is off; the first sync after it is
        // turned back on finds the item gone.
        _enabled = false;
        await service.SyncAsync(CancellationToken.None);
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        Assert.True(ReadStore().Items.ContainsKey(item.Id));
        _enabled = true;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Items);
        Assert.Equal(new[] { Jordan }, saved.Removed[item.Id].Owners);
        Assert.Equal(_clock.UtcNow, saved.Removed[item.Id].RemovedAt);

        // It comes back within the retention with its owners.
        var back = Rerun(item);
        await service.OnItemAddedAsync(back, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[back.Id]);
    }

    [Fact]
    public async Task ReturnedFile_NoTimerRecordingIt_RestoresOwners()
    {
        var service = NewService();
        var old = await ClaimedThenRemoved(service, "/rec/a.ts");

        // The storage drops out, the scan removes the item, and the same file comes back a day later.
        _clock.Advance(TimeSpan.FromDays(1));
        var back = Rerun(old);
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[back.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(back));
        Assert.Empty(ReadStore().Removed);

        // Nothing is recording to the path, so syncs leave it be.
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[back.Id]);
    }

    [Fact]
    public async Task ReturnedFile_TimersUnavailable_StillRestoresOwners()
    {
        var service = NewService();
        var first = await ClaimedThenRemoved(service, "/rec/a.ts");
        var second = await ClaimedThenRemoved(service, "/rec/b.ts");

        // The startup scan re-adds them before Live TV is ready.
        _host.TimersUnavailable = true;
        _clock.Advance(TimeSpan.FromHours(1));
        var firstBack = Rerun(first);
        var secondBack = Rerun(second);
        await service.OnItemAddedAsync(firstBack, CancellationToken.None);
        await service.OnItemAddedAsync(secondBack, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[firstBack.Id]);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[secondBack.Id]);
        Assert.Empty(ReadStore().Removed);
    }

    [Fact]
    public async Task ManyReturnedFiles_ListNoTimers()
    {
        var service = NewService();
        var removed = new List<LibraryItem>();
        for (var i = 0; i < 20; i++)
        {
            removed.Add(await ClaimedThenRemoved(service, "/rec/r" + i + ".ts"));
        }

        _clock.Advance(TimeSpan.FromHours(1));
        var reads = _host.TimerReads;
        foreach (var item in removed)
        {
            await service.OnItemAddedAsync(Rerun(item), CancellationToken.None);
        }

        Assert.Equal(reads, _host.TimerReads);
        Assert.Equal(20, ReadStore().Items.Count);
    }

    [Fact]
    public async Task CancelledMidway_ItemArrivesWithinAllowance_TakesOwners()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal("/rec/a.ts", ReadStore().TimerPaths["api-p1"]);

        // The cancel is raised while the recording runs; the item reaches the library later.
        await service.OnTimerCancelledAsync(timer.TimerId, "/rec/a.ts", CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Timers["api-p1"]);
        _clock.Advance(RecordingOwnershipService.PostRecordingAllowance - TimeSpan.FromMinutes(1));
        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);

        var saved = ReadStore();
        Assert.Equal(new[] { Jordan }, saved.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
        Assert.Empty(saved.Timers);
        Assert.Empty(saved.TimerPaths);
        Assert.Empty(saved.TimerPathsUntil);
    }

    [Fact]
    public async Task CancelledMidway_ItemArrivesAfterAllowance_NoOwners()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        await service.OnTimerCancelledAsync(timer.TimerId, "/rec/a.ts", CancellationToken.None);

        _clock.Advance(RecordingOwnershipService.PostRecordingAllowance + TimeSpan.FromMinutes(1));
        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.False(saved.Items.ContainsKey(item.Id));
        Assert.Empty(TagsOf(item));
        Assert.Empty(saved.Timers);
        Assert.Empty(saved.TimerPaths);
    }

    [Fact]
    public async Task CancelledUnownedTimer_ItemInLibrary_LosesItsOwners()
    {
        var service = NewService();
        var item = _host.AddItem("/rec/a.ts");
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        var timer = UnownedTimerRecording(item.Path!);

        await service.OnTimerCancelledAsync(timer.TimerId, item.Path, CancellationToken.None);

        Assert.Empty(ReadStore().Items);
        Assert.Empty(TagsOf(item));
        Assert.Empty(ReadStore().TimerPaths);
    }

    [Fact]
    public async Task JoinWhileRecording_ItemGainsOwner()
    {
        var service = NewService();
        await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);

        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan", "Recorded by Alex" }, TagsOf(item));
    }

    [Fact]
    public async Task ClaimItemWhileRecording_AddsToTimer_SurvivesNextSync()
    {
        var service = NewService();
        await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);

        var owners = await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Alex, CancellationToken.None);
        Assert.Equal(new[] { Jordan, Alex }, owners!.Select(o => o.UserId));
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Timers["api-p1"]);

        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan", "Recorded by Alex" }, TagsOf(item));
    }

    [Fact]
    public async Task ClaimItemWhileRecording_BeforeAnySyncNotedIt_SurvivesNextSync()
    {
        var service = NewService();
        var item = _host.AddItem("/rec/a.ts");
        UnownedTimerRecording(item.Path!);

        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Sam, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Sam }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Sam" }, TagsOf(item));
    }

    [Fact]
    public async Task ClaimItemWhileRecording_ByUnownedTimer_TimerBecomesTheClaimersAndSurvivesNextSync()
    {
        var service = NewService();
        var item = _host.AddItem("/rec/a.ts");
        UnownedTimerRecording(item.Path!);
        await service.SyncAsync(CancellationToken.None);

        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Sam, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Sam }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { Sam }, ReadStore().Timers["api-x"]);
    }

    [Fact]
    public async Task ClaimAfterRecordingEnded_NotUndoneBySync()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);

        _host.StopRecording(timer);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Empty(ReadStore().TimerPaths);

        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Alex, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan", "Recorded by Alex" }, TagsOf(item));
    }

    [Fact]
    public async Task FinishedTimer_ItemWithinAllowance_TakesOwnersOnce()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        _host.StopRecording(timer);
        _host.Timers.Remove(timer);
        await service.SyncAsync(CancellationToken.None);
        Assert.True(ReadStore().TimerPathsUntil.ContainsKey("api-p1"));

        _clock.Advance(RecordingOwnershipService.PostRecordingAllowance - TimeSpan.FromMinutes(1));
        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
        Assert.Empty(ReadStore().Timers);
        Assert.Empty(ReadStore().TimerPaths);
    }

    [Fact]
    public async Task FinishedTimer_ItemPastAllowance_NotClaimedAndTimerForgotten()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        _host.StopRecording(timer);
        _host.Timers.Remove(timer);
        await service.SyncAsync(CancellationToken.None);

        _clock.Advance(RecordingOwnershipService.PostRecordingAllowance + TimeSpan.FromMinutes(1));
        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.False(saved.Items.ContainsKey(item.Id));
        Assert.Empty(saved.Timers);
        Assert.Empty(saved.TimerPaths);
        Assert.Empty(saved.TimerPathsUntil);
    }

    [Fact]
    public async Task DeletedWhileRecording_TimerForgottenOnceItsNoteLapses()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);

        // Deleted while still recording: the timer is kept, then finishes with no item.
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        Assert.True(ReadStore().Timers.ContainsKey("api-p1"));
        _host.StopRecording(timer);
        await service.SyncAsync(CancellationToken.None);
        Assert.True(ReadStore().Timers.ContainsKey("api-p1"));

        // The allowance runs from the end of the timer's window.
        _clock.Advance(TimeSpan.FromHours(3));
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Timers);
        Assert.Empty(saved.TimerPaths);
        Assert.Equal(new[] { Jordan }, saved.Removed[item.Id].Owners);
    }

    [Fact]
    public async Task UnownedTimerRecordingTheSamePath_SupersedesAStoppedOwnedTimersNote()
    {
        var service = NewService();
        var timer = await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);

        // The recording is deleted, its timer stops, and something else records the path again.
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        _host.StopRecording(timer);
        await service.SyncAsync(CancellationToken.None);
        Assert.True(ReadStore().TimerPathsUntil.ContainsKey("api-p1"));
        UnownedTimerRecording("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);

        var back = Rerun(item);
        await service.OnItemAddedAsync(back, CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Items);
        Assert.Empty(saved.Removed);
        Assert.False(saved.Timers.ContainsKey("api-p1"));
        Assert.Empty(TagsOf(back));
    }

    [Fact]
    public async Task OldStore_TimerPathsWithoutExpiry_Loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            StorePath,
            "{\"Version\":1,\"Timers\":{\"api-p1\":[\"" + Jordan + "\"]},\"TimerPaths\":{\"api-p1\":\"/rec/a.ts\"}}");
        var service = NewService();

        // The timer isn't listed, so the first sync gives its file one allowance from now.
        await service.SyncAsync(CancellationToken.None);
        var saved = ReadStore();
        Assert.Equal("/rec/a.ts", saved.TimerPaths["api-p1"]);
        Assert.Equal(_clock.UtcNow + RecordingOwnershipService.PostRecordingAllowance, saved.TimerPathsUntil["api-p1"]);
        Assert.Equal(new[] { Jordan }, saved.Timers["api-p1"]);

        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Empty(ReadStore().TimerPaths);
    }

    [Fact]
    public async Task OldStore_TimerPathsWithoutExpiry_LapsAfterOneAllowance()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            StorePath,
            "{\"Version\":1,\"Timers\":{\"api-p1\":[\"" + Jordan + "\"]},\"TimerPaths\":{\"api-p1\":\"/rec/a.ts\"}}");
        var service = NewService();
        await service.SyncAsync(CancellationToken.None);

        _clock.Advance(RecordingOwnershipService.PostRecordingAllowance + TimeSpan.FromMinutes(1));
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Timers);
        Assert.Empty(saved.TimerPaths);
        Assert.Empty(saved.TimerPathsUntil);
    }

    [Fact]
    public async Task StoreWithRecordingNotes_KeepsVersionAndWritesPaths()
    {
        var service = NewService();
        await OwnedTimerRecording(service, "p1", Jordan, "/rec/a.ts");
        UnownedTimerRecording("/rec/b.ts");
        await service.SyncAsync(CancellationToken.None);

        var json = JsonNode.Parse(_stateFile.LastWritten(StorePath)!)!.AsObject();
        Assert.Equal(1, json["Version"]!.GetValue<int>());
        Assert.Equal("/rec/a.ts", json["TimerPaths"]!["api-p1"]!.GetValue<string>());
    }

    // Compat fix I2: the store's Version is honoured.

    [Fact]
    public async Task FutureVersionStore_IsReadButNeverOverwritten()
    {
        Directory.CreateDirectory(_dir);
        var json = "{\"Version\":2,\"Timers\":{\"api-p1\":[\"" + Jordan + "\"]},\"NewThing\":1}";
        File.WriteAllText(StorePath, json);
        var before = File.ReadAllBytes(StorePath);
        AddProgram("p1", Start);
        AddProgram("p2", Start.AddHours(1), episode: 2);
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-p1", TimerId = "t-p1", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        var service = NewService();

        // Read: Jordan's timer is known.
        Assert.Equal(new[] { "api-p1" }, (await service.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);

        // Changes work in memory but never reach the file.
        Assert.NotNull(await service.RecordProgramAsync("p2", Alex, CancellationToken.None));
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { "api-p2" }, (await service.GetMineAsync(Alex, CancellationToken.None)).TimerIds);

        Assert.Equal(0, _stateFile.Writes);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("newer than this plugin"));
    }

    [Fact]
    public async Task FutureVersionStore_UnparseableByThisVersion_NeitherPromotedNorRebuilt()
    {
        // The newer plugin changed a member's type, so this version can't parse its file; the
        // .bak is the v1 file the newer plugin's first save left, which this version could parse.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{\"Version\":2,\"Timers\":{\"api-p1\":{\"owners\":[\"" + Jordan + "\"]}}}");
        File.WriteAllText(StorePath + ".bak", "{\"Version\":1,\"Timers\":{\"api-p1\":[\"" + Jordan + "\"]}}");
        var main = File.ReadAllBytes(StorePath);
        var backup = File.ReadAllBytes(StorePath + ".bak");
        var tagged = _host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan" };
        AddProgram("p2", Start);
        var service = NewService();

        // Owners are unavailable, not rebuilt from tags or taken from the .bak.
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);

        // Changes work in memory only, and write no tags.
        Assert.NotNull(await service.RecordProgramAsync("p2", Alex, CancellationToken.None));
        await service.ClaimAsync(OwnedKind.Item, tagged.Id.ToString(), Alex, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(0, _stateFile.Writes);
        Assert.Empty(_host.TagWrites);
        Assert.Equal(main, File.ReadAllBytes(StorePath));
        Assert.Equal(backup, File.ReadAllBytes(StorePath + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("can't read"));
    }

    [Fact]
    public async Task CorruptStore_GoodBackup_IsPromoted()
    {
        // The control for the test above: a v1 main file that is only damaged still recovers from the .bak.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ \"Version\": 1, \"Tim");
        File.WriteAllText(StorePath + ".bak", "{\"Version\":1,\"Timers\":{\"api-p1\":[\"" + Jordan + "\"]}}");

        var service = NewService();

        Assert.Equal(new[] { "api-p1" }, (await service.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
        Assert.Equal(File.ReadAllText(StorePath + ".bak"), File.ReadAllText(StorePath));
    }

    [Fact]
    public async Task UnknownMembers_SurviveRoundTrip()
    {
        Directory.CreateDirectory(_dir);
        var gone = Guid.NewGuid();
        var json = "{\"Version\":1,\"Timers\":{},\"NewThing\":{\"a\":[1,2]},"
            + "\"Removed\":{\"" + gone + "\":{\"Owners\":[\"" + Jordan + "\"],\"RemovedAt\":\"2026-11-14T18:00:00Z\",\"Reason\":\"nas\"}}}";
        File.WriteAllText(StorePath, json);
        AddProgram("p1", Start);
        var service = NewService();

        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);

        Assert.True(_stateFile.Writes > 0);
        var saved = JsonNode.Parse(File.ReadAllText(StorePath))!;
        Assert.Equal(1, saved["Version"]!.GetValue<int>());
        Assert.Equal("[1,2]", saved["NewThing"]!["a"]!.ToJsonString());
        Assert.Equal("nas", saved["Removed"]![gone.ToString()]!["Reason"]!.GetValue<string>());
        Assert.Equal(Alex.ToString(), saved["Timers"]!["api-p1"]![0]!.GetValue<string>());
    }

    // Compat fix M7: cancels of timers nobody owns don't list the timers each time.

    [Fact]
    public async Task CancelOfUnownedTimers_DoesNotListTimersPerEvent()
    {
        AddProgram("p1", Start);
        var service = NewService();

        // Nothing owned: no listing at all.
        var reads = _host.TimerReads;
        for (var i = 1; i <= 5; i++)
        {
            await service.OnTimerCancelledAsync("t-gone" + i, CancellationToken.None);
        }

        Assert.Equal(reads, _host.TimerReads);

        // One owned timer, both ids known: a series rule deleted elsewhere cancels five timers nobody owns.
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        reads = _host.TimerReads;
        for (var i = 1; i <= 5; i++)
        {
            await service.OnTimerCancelledAsync("t-s" + i, CancellationToken.None);
        }

        Assert.Equal(reads, _host.TimerReads);
        Assert.Equal(new[] { Jordan }, ReadStore().Timers["api-p1"]);

        // The owned timer's own cancel, by its service id, still forgets its owners.
        _host.Timers.Clear();
        await service.OnTimerCancelledAsync("t-p1", CancellationToken.None);
        Assert.Empty(ReadStore().Timers);
    }

    [Fact]
    public async Task CancelOfUnownedTimers_AfterRestartWithUnmappedOwnedTimer_ListsOnce()
    {
        // An owned timer whose two ids are one (Emby), so no service id is stored for it.
        AddProgram("p1", Start);
        _host.Timers.Add(new ScheduledTimer { ApiId = "e-1", TimerId = "e-1", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        await NewService().ClaimAsync(OwnedKind.Timer, "e-1", Jordan, CancellationToken.None);
        Assert.False(ReadStore().TimerServiceIds.ContainsKey("e-1"));

        // A restart forgets the id map; then a rule deletion cancels five timers nobody owns.
        _host.Timers.Clear();
        var service = NewService();
        var reads = _host.TimerReads;
        for (var i = 1; i <= 5; i++)
        {
            await service.OnTimerCancelledAsync("e-s" + i, CancellationToken.None);
        }

        Assert.Equal(reads + 1, _host.TimerReads);
        Assert.Equal(new[] { Jordan }, ReadStore().Timers["e-1"]);

        // The owned timer's own cancel still resolves without a listing.
        await service.OnTimerCancelledAsync("e-1", CancellationToken.None);
        Assert.Equal(reads + 1, _host.TimerReads);
        Assert.Empty(ReadStore().Timers);
    }

    [Fact]
    public async Task UserRenamed_RewritesTags()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));

        var renamed = new ServerUser(Jordan, "Grandpa", "jordan-tag", false);
        _host.Users[_host.Users.FindIndex(u => u.Id == Jordan)] = renamed;
        await service.OnUserUpdatedAsync(renamed, CancellationToken.None);

        Assert.Equal(new[] { "Recorded by Grandpa" }, TagsOf(item));
        Assert.Equal(new[] { item.Id }, (await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task UserDeleted_RemovedFromOwnersAndTags()
    {
        AddProgram("p1", Start);
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza"));
        var item = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        await service.ClaimAsync(OwnedKind.Series, "rule-a", Jordan, CancellationToken.None);
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Alex, CancellationToken.None);

        _host.Users.RemoveAll(u => u.Id == Jordan);
        await service.OnUserDeletedAsync(Jordan, CancellationToken.None);

        var mine = await service.GetMineAsync(Jordan, CancellationToken.None);
        Assert.Empty(mine.TimerIds);
        Assert.Empty(mine.SeriesTimerIds);
        Assert.Empty(mine.ItemIds);
        var saved = ReadStore();
        Assert.Equal(new[] { Alex }, saved.Timers["api-p1"]);
        Assert.False(saved.SeriesTimers.ContainsKey("rule-a"));
        Assert.Equal(new[] { Alex }, saved.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
    }

    [Fact]
    public async Task CorruptStoreAndBackup_RebuildsItemsFromTags()
    {
        var tagged = _host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan", "Recorded by Nobody", "Favourite" };
        var shared = _host.AddItem("/rec/b.ts");
        shared.Tags = new[] { "recorded by alex", "Recorded by Sam" };
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json");
        File.WriteAllText(StorePath + ".bak", "also not json");

        var service = NewService();
        var lookup = await service.LookupAsync(Array.Empty<string>(), Array.Empty<string>(), new[] { tagged.Id, shared.Id }, Sam, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, lookup.Items[tagged.Id].Select(o => o.UserId));
        Assert.Equal(new[] { Alex, Sam }, lookup.Items[shared.Id].Select(o => o.UserId));
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("no single user is named Nobody", StringComparison.Ordinal));
        Assert.Equal(new[] { tagged.Id }, ReadStore().Items.Where(e => e.Value.Contains(Jordan)).Select(e => e.Key));

        // The unusable files were set aside before the rebuilt store replaced them.
        Assert.Single(Directory.GetFiles(_dir, RecordingOwnershipService.StoreFileName + ".corrupt-*"));
        Assert.Single(Directory.GetFiles(_dir, RecordingOwnershipService.StoreFileName + ".bak.corrupt-*"));
    }

    [Fact]
    public async Task Lookup_HidesItemsCallerCannotSee_AndDisabledUsers()
    {
        var visible = _host.AddItem("/rec/a.ts");
        var hidden = _host.AddItem("/rec/b.ts");
        var shownProgram = AddProgram("p1", Start);
        var hiddenProgram = AddProgram("p2", Start.AddHours(1));
        var controlProgram = AddProgram("p3", Start.AddHours(2));
        var service = NewService();
        foreach (var item in new[] { visible, hidden })
        {
            await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
            await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Alex, CancellationToken.None);
        }

        await service.RecordProgramAsync(shownProgram.Id, Alex, CancellationToken.None);
        await service.RecordProgramAsync(hiddenProgram.Id, Jordan, CancellationToken.None);
        await service.RecordProgramAsync(controlProgram.Id, Jordan, CancellationToken.None);

        _host.HiddenItems.Add(hidden.Id);
        _host.HiddenPrograms.Add(hiddenProgram.ItemId.ToString("N"));
        _host.Users[_host.Users.FindIndex(u => u.Id == Alex)] = new ServerUser(Alex, "Alex", "alex-tag", IsDisabled: true);

        var lookup = await service.LookupAsync(
            new[] { "api-p1", "api-p2", "api-p3", "api-unknown" },
            Array.Empty<string>(),
            new[] { visible.Id, hidden.Id, Guid.NewGuid() },
            Sam,
            CancellationToken.None);

        Assert.Equal(new[] { visible.Id }, lookup.Items.Keys);
        Assert.Equal(new[] { Jordan }, lookup.Items[visible.Id].Select(o => o.UserId));

        // api-p1's only owner is disabled, so it has none to show; api-p2's program is hidden.
        // api-p3 is the control: an enabled owner's timer on a visible program.
        Assert.Equal(new[] { "api-p3" }, lookup.Timers.Keys);
        Assert.Equal(new[] { Jordan }, lookup.Timers["api-p3"].Select(o => o.UserId));

        // Disabled owners stay owners; they are only left out of what clients are shown.
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[visible.Id]);
    }

    [Fact]
    public async Task Disabled_ReturnsEmptyAndIgnoresEvents()
    {
        AddProgram("p1", Start);
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza"));
        var owned = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Item, owned.Id.ToString(), Jordan, CancellationToken.None);
        var writes = _stateFile.Writes;
        var tagWrites = _host.TagWrites.Count;
        var reads = _host.TimerReads;

        _enabled = false;
        Assert.Null(await service.RecordProgramAsync("p1", Jordan, CancellationToken.None));
        Assert.Null(await service.RecordSeriesAsync("p1", Jordan, null, CancellationToken.None));
        Assert.Null(await service.ClaimAsync(OwnedKind.Series, "rule-a", Jordan, CancellationToken.None));
        Assert.Null(await service.ClaimAsync(OwnedKind.Item, owned.Id.ToString(), Alex, CancellationToken.None));
        var lookup = await service.LookupAsync(new[] { "api-p1" }, new[] { "rule-a" }, new[] { owned.Id }, Jordan, CancellationToken.None);
        Assert.Empty(lookup.Items);
        Assert.Empty(lookup.Timers);
        Assert.Empty(lookup.SeriesTimers);
        var mine = await service.GetMineAsync(Jordan, CancellationToken.None);
        Assert.Empty(mine.ItemIds);
        Assert.Empty(service.OwnersOfItems(new[] { owned.Id }));
        Assert.False(service.HasRecordingInProgress);

        await service.SyncAsync(CancellationToken.None);
        await service.OnItemRemovedAsync(owned.Id, CancellationToken.None);
        await service.OnItemAddedAsync(_host.AddItem("/rec/b.ts"), CancellationToken.None);
        await service.OnTimerCancelledAsync("t-p1", CancellationToken.None);
        await service.OnUserUpdatedAsync(new ServerUser(Jordan, "Grandpa", "jordan-tag", false), CancellationToken.None);
        await service.OnUserDeletedAsync(Jordan, CancellationToken.None);
        service.AddTimerOwners("api-p1", new[] { Jordan });

        Assert.Empty(_host.Scheduled);
        Assert.Empty(_host.SeriesCreations);
        Assert.Equal(reads, _host.TimerReads);
        Assert.Equal(writes, _stateFile.Writes);
        Assert.Equal(tagWrites, _host.TagWrites.Count);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(owned));

        // Turned back on, nothing was lost while it was off.
        _enabled = true;
        Assert.Equal(new[] { owned.Id }, (await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    // Plan: incomplete recordings integration

    private IncompleteRecordingService NewIncompleteService(IncompleteRecordingSettings settings, IRecordingOwners owners) =>
        new(_host, _log, PlainStateFile.Instance, () => _clock.UtcNow, IncompleteStorePath, () => settings, owners);

    private IncompleteRecordingStoreData ReadIncompleteStore() =>
        JsonSerializer.Deserialize<IncompleteRecordingStoreData>(CountingStateFile.ReadShared(IncompleteStorePath))!;

    // The incomplete tests' Storage Wars airing, which FakeRecordingHost.StartRecording describes.
    private GuideProgram AddAiring(string id, DateTime start) => AddProgram(id, start, "Storage Wars", 3, 7);

    [Fact]
    public async Task AutomaticRequest_TakesIncompleteItemOwners()
    {
        AddAiring("p1", Start);
        AddAiring("p2", Start.AddDays(1));
        var item = _host.AddItem("/rec/a.ts");
        var owners = NewService();
        await owners.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        var timer = new ScheduledTimer { TimerId = "t1", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" };
        _host.Timers.Add(timer);
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings { AutoRecordComplete = true }, owners);

        // A 20-minute show written for its first 10 minutes, run once a minute until judged.
        _clock.UtcNow = Start;
        while (_clock.UtcNow <= timer.EndDate)
        {
            var minute = (_clock.UtcNow - Start).TotalMinutes;
            if (minute < 10 && !_host.ActivePaths.ContainsKey(timer.TimerId))
            {
                _host.StartRecording(timer, "/rec/a.ts", 3, 7);
            }
            else if (minute >= 10 && _host.ActivePaths.ContainsKey(timer.TimerId))
            {
                _host.StopRecording(timer);
            }

            _host.Write();
            await incomplete.RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(new[] { "p2" }, _host.Scheduled);
        var request = ReadIncompleteStore().Requests.Single();
        Assert.Null(request.RequestedByUserId);
        Assert.Equal(new[] { Jordan }, request.OwnerUserIds);

        await owners.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { "api-p2" }, (await owners.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
    }

    [Fact]
    public async Task ExplicitRequest_SecondUserJoins_BothOwnTheTimer()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);

        var first = await incomplete.RequestCompleteShowingAsync(source.Id, Jordan, CancellationToken.None);
        var second = await incomplete.RequestCompleteShowingAsync(source.Id, Alex, CancellationToken.None);

        Assert.Equal(first.Request!.Id, second.Request!.Id);
        Assert.Equal(new[] { later.Id }, _host.Scheduled);
        Assert.Equal(new[] { Jordan, Alex }, second.Request.OwnerUserIds);
        Assert.Equal(new[] { Jordan }, incomplete.OtherOwners(second.Request, Alex, true).Select(o => o.UserId));

        await owners.SyncAsync(CancellationToken.None);
        var timerId = "api-" + later.Id;
        var lookup = await owners.LookupAsync(new[] { timerId }, Array.Empty<string>(), Array.Empty<Guid>(), Sam, CancellationToken.None);
        Assert.Equal(new[] { Jordan, Alex }, lookup.Timers[timerId].Select(o => o.UserId));
    }

    // Compat fix M6: a user lookup that fails or can't tell is not a deleted user.

    [Fact]
    public async Task GetUserUnavailable_DoesNotPruneRequestOwners()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);
        await incomplete.RequestCompleteShowingAsync(source.Id, Jordan, CancellationToken.None);

        // A server whose user lookup can't be bound answers "can't tell" for everyone.
        _host.UserLookupUnknown = true;
        var second = await incomplete.RequestCompleteShowingAsync(source.Id, Alex, CancellationToken.None);

        Assert.Equal(new[] { Jordan, Alex }, second.Request!.OwnerUserIds);
        Assert.Equal(new[] { Jordan, Alex }, ReadIncompleteStore().Requests.Single().OwnerUserIds);
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Timers["api-" + later.Id]);

        _host.UserLookupUnknown = false;
        await owners.SyncAsync(CancellationToken.None);
        var timerId = "api-" + later.Id;
        var lookup = await owners.LookupAsync(new[] { timerId }, Array.Empty<string>(), Array.Empty<Guid>(), Sam, CancellationToken.None);
        Assert.Equal(new[] { Jordan, Alex }, lookup.Timers[timerId].Select(o => o.UserId));
    }

    [Fact]
    public async Task DeletedUser_StillPrunedFromRequestOwners()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);
        await incomplete.RequestCompleteShowingAsync(source.Id, Jordan, CancellationToken.None);

        _host.Users.RemoveAll(u => u.Id == Jordan);
        var second = await incomplete.RequestCompleteShowingAsync(source.Id, Alex, CancellationToken.None);

        Assert.Equal(new[] { Alex }, second.Request!.OwnerUserIds);
        Assert.Equal(new[] { Alex }, ReadIncompleteStore().Requests.Single().OwnerUserIds);
        Assert.Contains(Alex, ReadStore().Timers["api-" + later.Id]);
    }

    [Fact]
    public async Task GetUserThrows_CompleteShowingStillSucceeds()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);
        await incomplete.RequestCompleteShowingAsync(source.Id, Jordan, CancellationToken.None);

        _host.UserLookupThrows = true;
        var second = await incomplete.RequestCompleteShowingAsync(source.Id, Alex, CancellationToken.None);

        Assert.Equal(CompleteShowingRefusal.None, second.Refusal);
        Assert.Equal(new[] { Jordan, Alex }, second.Request!.OwnerUserIds);
        Assert.Empty(incomplete.OtherOwners(second.Request, Alex, true));
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Timers["api-" + later.Id]);
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("still exists"));

        // The owners endpoints carry on too: owners whose user can't be read are left out, not an error.
        var lookup = await owners.LookupAsync(new[] { "api-" + later.Id }, Array.Empty<string>(), Array.Empty<Guid>(), Sam, CancellationToken.None);
        Assert.Empty(lookup.Timers);
    }

    [Fact]
    public async Task OldStore_RequestedByUserId_SeedsOwnerList()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        var data = new IncompleteRecordingStoreData();
        data.Requests.Add(new CompleteShowingRequest { Episode = source.Key!, OpenedAt = Start, Explicit = true, RequestedByUserId = Jordan });

        // A file from before requests had owners: no OwnerUserIds at all.
        var json = JsonSerializer.SerializeToNode(data)!;
        foreach (var request in json["Requests"]!.AsArray())
        {
            Assert.True(request!.AsObject().Remove("OwnerUserIds"));
        }

        Directory.CreateDirectory(_dir);
        File.WriteAllText(IncompleteStorePath, json.ToJsonString());
        _clock.UtcNow = Start.AddMinutes(5);
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);

        await incomplete.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { later.Id }, _host.Scheduled);
        var saved = ReadIncompleteStore().Requests.Single();
        Assert.Equal(new[] { Jordan }, saved.OwnerUserIds);
        Assert.Equal(new[] { Jordan }, incomplete.OtherOwners(saved, Alex, true).Select(o => o.UserId));
        await owners.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { "api-" + later.Id }, (await owners.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
    }

    // Bug 1 regression cases

    // 1
    [Fact]
    public async Task ItemAddedBeforePathCaptured_SyncAttachesOwnersAndTags()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        _host.StartRecording(TimerOf("p1"), "/rec/a.ts", 1, 1);

        // Jellyfin adds the item when the recording starts, before any sync saw the file.
        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Empty(item.Tags);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);

        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    // 2
    [Fact]
    public async Task RecordNow_ItemExistsAtSchedule_RecordProgramAttachesOwners()
    {
        AddProgram("p1", Start);
        LibraryItem? item = null;
        _host.TimerScheduled = timer =>
        {
            _host.StartRecording(timer, "/rec/a.ts", 1, 1);
            item = _host.AddItem("/rec/a.ts");
        };
        var service = NewService();

        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item!.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    // 3
    [Fact]
    public async Task CancelWithActivePath_ItemExists_TakesOwnersAndTimerIsForgotten()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        var timer = TimerOf("p1");
        timer.Status = "InProgress";
        LibraryItem item;
        await using (await StartWatcher(service, expectedReads: 2))
        {
            Assert.False(ReadStore().TimerPaths.ContainsKey("api-p1"));

            // The server raises the cancel (service id) while the recording still runs.
            _host.ActivePaths[timer.TimerId] = "/rec/a.ts";
            item = _host.AddItem("/rec/a.ts");
            _host.RaiseTimerCancelled(timer.TimerId);

            await WaitFor(() => item.Tags.Contains("Recorded by Jordan"), "the cancelled recording's owners");
            await Bounded(service.GetMineAsync(Jordan, CancellationToken.None), "a call after the cancel");
            var saved = ReadStore();
            Assert.Equal(new[] { Jordan }, saved.Items[item.Id]);

            // The item took the owners once and the timer, cancelled, is done with.
            Assert.Empty(saved.Timers);
            Assert.Empty(saved.TimerPaths);
        }

        _host.Timers.Remove(timer);
        _host.ActivePaths.Remove(timer.TimerId);
        await service.SyncAsync(CancellationToken.None);

        var after = ReadStore();
        Assert.Equal(new[] { Jordan }, after.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    // 4
    [Fact]
    public async Task CancelWithActivePath_NoItemYet_LaterItemAddedTakesOwners()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);

        await service.OnTimerCancelledAsync("t-p1", "/rec/a.ts", CancellationToken.None);

        var saved = ReadStore();
        Assert.Equal(new[] { Jordan }, saved.Timers["api-p1"]);
        Assert.Equal("/rec/a.ts", saved.TimerPaths["api-p1"]);

        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    // 5
    [Fact]
    public async Task CancelWithNoPath_ForgetsOwners()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);

        await service.OnTimerCancelledAsync("t-p1", null, CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Timers);
        Assert.Empty(saved.TimerServiceIds);
        Assert.Empty((await service.GetMineAsync(Alex, CancellationToken.None)).TimerIds);
    }

    // 6
    [Fact]
    public async Task CancelEventWithApiId_WatcherMapsServiceIdAndCapturesPath()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        var timer = TimerOf("p1");
        timer.Status = "InProgress";
        await using (var watcher = await StartWatcher(service, expectedReads: 2))
        {
            // The id map is filled right after the second listing, outside the service's lock.
            await WaitFor(() => watcher.Watcher.KnowsServiceId("api-p1"), "the watcher's id map");

            // Only the service id knows the file, and the event carries the API id.
            _host.ActivePaths[timer.TimerId] = "/rec/a.ts";
            Assert.Null(_host.GetActiveRecordingPath(timer.ApiId!));
            _host.RaiseTimerCancelled(timer.ApiId!);

            await WaitFor(() => File.Exists(StorePath) && ReadStore().TimerPaths.ContainsKey("api-p1"), "the cancelled timer's file");
            Assert.Equal("/rec/a.ts", ReadStore().TimerPaths["api-p1"]);
            Assert.Equal(new[] { Jordan }, ReadStore().Timers["api-p1"]);
        }

        var item = _host.AddItem("/rec/a.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    // 7
    [Fact]
    public async Task SyncAttachesOwnersBeforeForgettingFinishedTimer()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        var timer = TimerOf("p1");
        _host.StartRecording(timer, "/rec/a.ts", 1, 1);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal("/rec/a.ts", ReadStore().TimerPaths["api-p1"]);

        // The recording finished and left the listing; its item is there but was never attached.
        _host.StopRecording(timer);
        _host.Timers.Remove(timer);
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.Empty(saved.Timers);
        Assert.Equal(new[] { Jordan }, saved.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    // 8
    [Fact]
    public async Task JoinInProgress_ItemGainsOnlyNewOwnerWithOneTagWrite()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        _host.StartRecording(TimerOf("p1"), "/rec/a.ts", 1, 1);
        var item = _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));

        var before = _host.TagWrites.Count;
        var joined = await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        Assert.True(joined!.Joined);
        Assert.Equal(new[] { (item.Id, "Recorded by Alex", true) }, _host.TagWrites.Skip(before));
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[item.Id]);

        before = _host.TagWrites.Count;
        await service.ClaimAsync(OwnedKind.Timer, "api-p1", Sam, CancellationToken.None);
        Assert.Equal(new[] { (item.Id, "Recorded by Sam", true) }, _host.TagWrites.Skip(before));
        Assert.Equal(new[] { Jordan, Alex, Sam }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan", "Recorded by Alex", "Recorded by Sam" }, TagsOf(item));
    }

    // 9
    [Fact]
    public async Task RepeatedSyncs_AfterAttach_NoExtraTagWritesOrSaves()
    {
        AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        _host.StartRecording(TimerOf("p1"), "/rec/a.ts", 1, 1);
        _host.AddItem("/rec/a.ts");
        await service.SyncAsync(CancellationToken.None);
        var tagWrites = _host.TagWrites.Count;
        var saves = _stateFile.Writes;

        for (var i = 0; i < 3; i++)
        {
            await service.SyncAsync(CancellationToken.None);
        }

        Assert.Equal(tagWrites, _host.TagWrites.Count);
        Assert.Equal(saves, _stateFile.Writes);
    }

    // 10
    [Fact]
    public async Task TimerCreated_WatcherSyncsAgainAfterFollowUpDelay()
    {
        var service = NewService();

        // The test decides when the follow-up delay is over, so no wall-clock time is involved.
        var requested = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        var followUpOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task FollowUpDelay(TimeSpan delay, CancellationToken ct)
        {
            requested.Enqueue(delay);
            return followUpOver.Task;
        }

        await using (await StartWatcher(service, followUpDelay: FollowUpDelay))
        {
            var reads = _host.TimerReads;
            _host.RaiseTimerCreated(new ScheduledTimer { TimerId = "t-p1", ProgramId = "p1" });

            // At once, with a follow-up asked for; idle, the next sync would be ten minutes away.
            await WaitFor(() => _host.TimerReads >= reads + 1, "the immediate sync");
            await WaitFor(() => !requested.IsEmpty, "the follow-up to be scheduled");
            Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(requested));
            await Bounded(service.GetMineAsync(Jordan, CancellationToken.None), "a call after the immediate sync");
            Assert.Equal(reads + 1, _host.TimerReads);

            // Once the delay is over, exactly one more sync.
            followUpOver.SetResult();
            await WaitFor(() => _host.TimerReads >= reads + 2, "the follow-up sync");
            await Bounded(service.GetMineAsync(Jordan, CancellationToken.None), "a call after the follow-up sync");
            Assert.Equal(reads + 2, _host.TimerReads);
        }
    }

    // 11
    [Fact]
    public async Task CancelRaisedUnderServiceLock_DoesNotDeadlock()
    {
        AddProgram("p0", Start);
        AddProgram("p1", Start.AddHours(1));
        var service = NewService();
        await service.RecordProgramAsync("p0", Jordan, CancellationToken.None);
        await using (await StartWatcher(service))
        {
            // The schedule call runs inside RecordProgramAsync, under the service's lock, and the
            // server raises another timer's cancel from it.
            _host.TimerScheduled = _ => _host.RaiseTimerCancelled("t-p0");

            var outcome = await Bounded(Task.Run(() => service.RecordProgramAsync("p1", Alex, CancellationToken.None)), "RecordProgramAsync");

            Assert.Equal("api-p1", outcome!.Id);
            await WaitFor(() => !ReadStore().Timers.ContainsKey("api-p0"), "the cancel to be handled");
            Assert.Equal(new[] { Alex }, ReadStore().Timers["api-p1"]);
        }
    }

    // Final fix wave

    [Fact]
    public async Task Lookup_ListsVisibleRecordingsOwnedOrNot_InRecordingItemIds()
    {
        var owned = _host.AddItem("/rec/owned.ts");
        var unowned = _host.AddItem("/rec/unowned.ts");
        var hidden = _host.AddItem("/rec/hidden.ts");
        var movie = _host.AddItem("/movies/film.mkv");
        _host.NotRecordings.Add(movie.Id);
        _host.HiddenItems.Add(hidden.Id);
        var service = NewService();
        await service.ClaimAsync(OwnedKind.Item, owned.Id.ToString(), Jordan, CancellationToken.None);

        var lookup = await service.LookupAsync(
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { owned.Id, unowned.Id, hidden.Id, movie.Id, Guid.NewGuid() },
            Alex,
            CancellationToken.None);

        Assert.Equal(new[] { owned.Id, unowned.Id }, lookup.RecordingItemIds);
        Assert.Equal(new[] { owned.Id }, lookup.Items.Keys);
    }

    [Fact]
    public async Task Lookup_HostCantTellRecordings_LeavesRecordingItemIdsUnknown()
    {
        var item = _host.AddItem("/rec/a.ts");
        _host.RecordingKindUnknown = true;
        var service = NewService();

        var lookup = await service.LookupAsync(Array.Empty<string>(), Array.Empty<string>(), new[] { item.Id }, Alex, CancellationToken.None);

        Assert.Null(lookup.RecordingItemIds);
    }

    [Fact]
    public async Task ClaimItem_NotARecording_ReturnsNullAndStampsNothing()
    {
        var movie = _host.AddItem("/movies/film.mkv");
        _host.NotRecordings.Add(movie.Id);
        var service = NewService();

        var owners = await service.ClaimAsync(OwnedKind.Item, movie.Id.ToString(), Jordan, CancellationToken.None);

        Assert.Null(owners);
        Assert.Empty(_host.TagWrites);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task ClaimItem_HostCantTellRecordings_IsRefusedAndStampsNothing()
    {
        var item = _host.AddItem("/rec/a.ts");
        _host.RecordingKindUnknown = true;
        var service = NewService();

        var owners = await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);

        Assert.Null(owners);
        Assert.Empty(_host.TagWrites);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task RecordProgram_ServerRefuses_CarriesTheServersReason()
    {
        AddProgram("p1", Start);
        _host.SchedulingError = "Emby Premiere required to record";
        var service = NewService();

        var attempt = await service.TryRecordProgramAsync("p1", Jordan, CancellationToken.None);

        Assert.Null(attempt.Outcome);
        Assert.Equal("Emby Premiere required to record", attempt.ErrorOr("fallback"));
        Assert.Empty(ReadStoreOrEmpty().Timers);
    }

    [Fact]
    public async Task RecordSeries_ServerRefuses_CarriesTheServersReason()
    {
        AddProgram("p1", Start);
        _host.SeriesCreationError = "Emby Premiere required to record";
        var service = NewService();

        var attempt = await service.TryRecordSeriesAsync("p1", Jordan, null, CancellationToken.None);

        Assert.Null(attempt.Outcome);
        Assert.Equal("Emby Premiere required to record", attempt.ErrorOr("fallback"));
    }

    [Fact]
    public async Task RecordProgram_FailsWithoutReason_FallsBack()
    {
        AddProgram("p1", Start);
        _host.SchedulingFails = true;
        var service = NewService();

        var attempt = await service.TryRecordProgramAsync("p1", Jordan, CancellationToken.None);

        Assert.Null(attempt.Outcome);
        Assert.Equal("fallback", attempt.ErrorOr("fallback"));
    }

    [Fact]
    public async Task GetUsersThrows_RecordStillSchedulesAndOwns()
    {
        AddProgram("p1", Start);
        _host.UserLookupThrows = true;

        // The first call of a new process loads the store, which lists the users.
        var service = NewService();
        var outcome = await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal("api-p1", outcome!.Id);
        Assert.Equal(new[] { "p1" }, _host.Scheduled);
        Assert.Equal(new[] { Jordan }, ReadStore().Timers["api-p1"]);
        Assert.Equal(new[] { "api-p1" }, (await service.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("list the server's users"));
    }

    // PR review fixes

    [Fact]
    public async Task ClaimTimer_ProgramHiddenFromCaller_ClaimsNothingAndShowsNoOwners()
    {
        var program = AddProgram("p1", Start);
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        _host.HiddenItems.Add(program.ItemId);

        var claimed = await service.ClaimAsync(OwnedKind.Timer, "api-p1", Alex, CancellationToken.None);

        Assert.Null(claimed);
        Assert.Equal(new[] { Jordan }, ReadStore().Timers["api-p1"]);
    }

    [Fact]
    public async Task ClaimSeries_NothingTheRuleRecordsIsVisibleToCaller_ClaimsNothing()
    {
        var program = AddProgram("p1", Start);
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza"));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-1", TimerId = "t-1", ProgramId = "p1", SeriesTimerId = "rule-a", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.HiddenItems.Add(program.ItemId);
        var service = NewService();

        var claimed = await service.ClaimAsync(OwnedKind.Series, "rule-a", Alex, CancellationToken.None);

        Assert.Null(claimed);
        Assert.Empty((await service.GetMineAsync(Alex, CancellationToken.None)).SeriesTimerIds);
    }

    [Fact]
    public async Task ClaimSeries_ChannelVisibleButScheduledProgramHidden_ClaimsNothing()
    {
        var program = AddProgram("p1", Start);
        var channel = Guid.NewGuid();
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza", null, channel.ToString("N")));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-1", TimerId = "t-1", ProgramId = "p1", SeriesTimerId = "rule-a", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.HiddenItems.Add(program.ItemId);
        var service = NewService();

        Assert.Null(await service.ClaimAsync(OwnedKind.Series, "rule-a", Alex, CancellationToken.None));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Error")]
    public async Task ClaimSeries_OnlyFinishedTimersWhoseProgramLeftTheGuide_JudgedByItsChannel(string status)
    {
        var channel = Guid.NewGuid();
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza", null, channel.ToString("N")));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-old", TimerId = "t-old", ProgramId = "gone", SeriesTimerId = "rule-a", StartDate = Start.AddDays(-1), EndDate = Start.AddDays(-1).AddMinutes(20), Status = status });
        var service = NewService();

        Assert.NotNull(await service.ClaimAsync(OwnedKind.Series, "rule-a", Alex, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimSeries_FinishedTimerVisibleButPendingProgramHidden_ClaimsNothing()
    {
        var past = AddProgram("p0", Start.AddDays(-1));
        var next = AddProgram("p1", Start);
        var channel = Guid.NewGuid();
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza", null, channel.ToString("N")));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-0", TimerId = "t-0", ProgramId = "p0", SeriesTimerId = "rule-a", StartDate = past.StartDate, EndDate = past.StartDate.AddMinutes(20), Status = "Completed" });
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-1", TimerId = "t-1", ProgramId = "p1", SeriesTimerId = "rule-a", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.HiddenItems.Add(next.ItemId);
        var service = NewService();

        Assert.Null(await service.ClaimAsync(OwnedKind.Series, "rule-a", Alex, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimSeries_RuleWithNoTimersAndNothingToJudgeBy_ClaimsNothing()
    {
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza"));
        var service = NewService();

        Assert.Null(await service.ClaimAsync(OwnedKind.Series, "rule-a", Alex, CancellationToken.None));
    }

    [Fact]
    public async Task ClaimSeries_RuleWithNoTimers_JudgedByItsChannel()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        _host.SeriesRules.Add(new SeriesRule("rule-a", null, "Bonanza", null, visible.ToString("N")));
        _host.SeriesRules.Add(new SeriesRule("rule-b", null, "Rawhide", null, hidden.ToString("N")));
        _host.HiddenItems.Add(hidden);
        var service = NewService();

        Assert.NotNull(await service.ClaimAsync(OwnedKind.Series, "rule-a", Alex, CancellationToken.None));
        Assert.Null(await service.ClaimAsync(OwnedKind.Series, "rule-b", Alex, CancellationToken.None));
    }

    [Fact]
    public async Task RecordProgram_OnlyACompletedTimerForIt_SchedulesInsteadOfJoining()
    {
        AddProgram("p1", Start);
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-done", TimerId = "t-done", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "Completed" });
        var service = NewService();

        var outcome = await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Joined);
        Assert.Equal(new[] { "p1" }, _host.Scheduled);
    }

    [Fact]
    public async Task RecordSeries_SameNameDifferentSeriesId_GetsItsOwnRule()
    {
        AddProgram("p1", Start);
        var other = AddProgram("p2", Start.AddHours(1));
        other.SeriesId = "SH999";
        var service = NewService();
        var first = await service.RecordSeriesAsync("p1", Jordan, null, CancellationToken.None);
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-s1", TimerId = "t-s1", ProgramId = "p1", SeriesTimerId = first!.Id, StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });

        var second = await service.RecordSeriesAsync("p2", Alex, null, CancellationToken.None);

        Assert.NotNull(second);
        Assert.False(second!.Joined);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _host.SeriesRules.Count);
        Assert.Equal(new[] { Jordan }, ReadStore().SeriesTimers[first.Id]);
    }

    [Fact]
    public async Task StoreAndBackupBothMissing_RecordingsWithOwnerTags_RebuildsFromTags()
    {
        var tagged = _host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan" };
        var service = NewService();

        var mine = await service.GetMineAsync(Jordan, CancellationToken.None);

        Assert.Equal(new[] { tagged.Id }, mine.ItemIds);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[tagged.Id]);
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("are missing"));
    }

    [Fact]
    public async Task FirstStart_NoOwnerTags_SavesNothing()
    {
        var service = NewService();

        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Equal(0, _stateFile.Writes);
    }

    [Fact]
    public async Task RebuildFails_NothingSavedOverTheFiles_NextCallRebuilds()
    {
        var tagged = _host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan" };
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json");
        File.WriteAllText(StorePath + ".bak", "also not json");
        _host.UserLookupThrows = true;
        var service = NewService();

        // A claim made while the rebuild can't run isn't saved over the files the rebuild needs.
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        await service.ClaimAsync(OwnedKind.Item, tagged.Id.ToString(), Alex, CancellationToken.None);
        Assert.Equal(0, _stateFile.Writes);
        Assert.Equal("{ not json", File.ReadAllText(StorePath));

        _host.UserLookupThrows = false;
        Assert.Equal(new[] { tagged.Id }, (await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Equal(new[] { Jordan, Alex }, ReadStore().Items[tagged.Id]);
    }

    [Fact]
    public async Task RebuildFails_RecordMadeMeanwhile_KeepsItsOwnerOnceRebuilt()
    {
        var tagged = _host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan" };
        AddProgram("p1", Start);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json");
        File.WriteAllText(StorePath + ".bak", "also not json");
        _host.UserLookupThrows = true;
        var service = NewService();

        var outcome = await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        Assert.Equal("api-p1", outcome!.Id);

        _host.UserLookupThrows = false;
        Assert.Equal(new[] { "api-p1" }, (await service.GetMineAsync(Alex, CancellationToken.None)).TimerIds);
        Assert.Equal(new[] { tagged.Id }, (await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Equal(new[] { Alex }, ReadStore().Timers["api-p1"]);
    }

    [Fact]
    public async Task SaveFails_NextSyncWritesTheClaim()
    {
        var item = _host.AddItem("/rec/a.ts");
        var service = NewService();
        await service.SyncAsync(CancellationToken.None);
        _stateFile.FailWrites = true;

        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        _stateFile.FailWrites = false;
        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
    }

    [Fact]
    public async Task RemovedAndReAdded_EventsHandledInArrivalOrder_RecordingsKeepOwners()
    {
        var service = NewService();
        var items = Enumerable.Range(0, 20).Select(i => _host.AddItem($"/rec/{i}.ts")).ToList();
        foreach (var item in items)
        {
            await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        }

        await using (var watcher = await StartWatcher(service))
        {
            // Storage blinks: each recording leaves the library and comes straight back.
            foreach (var item in items)
            {
                _host.RaiseItemRemoved(item.Id);
                _host.RaiseItemAdded(item);
            }

            await Bounded(watcher.Watcher.Idle.ContinueWith(_ => true), "the queued events");
        }

        var saved = ReadStore();
        Assert.All(items, item => Assert.Equal(new[] { Jordan }, saved.Items[item.Id]));
        Assert.Empty(saved.Removed);
    }

    [Fact]
    public async Task SuggestedShowing_ScheduleNamesNoTimer_OwnersStillReachTheTimer()
    {
        var source = AddAiring(Guid.NewGuid().ToString("N"), Start);
        var later = AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1));
        _clock.UtcNow = Start.AddMinutes(5);
        _host.ScheduleReturnsNoIds = true;
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);
        var options = await incomplete.GetCompleteShowingOptionsAsync(source.Id, Jordan);

        Assert.True(await incomplete.ScheduleSuggestedShowingAsync(source.Id, options.Showing!, Jordan, CancellationToken.None));
        await owners.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { later.Id }, _host.Scheduled);
        Assert.Equal(new[] { "api-" + later.Id }, (await owners.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
    }

    [Fact]
    public async Task RecordSeries_RuleNamedLikeTheSeriesButRecordingAnotherSeriesId_GetsItsOwnRule()
    {
        AddProgram("p1", Start);
        var other = AddProgram("p2", Start.AddHours(1));
        other.SeriesId = "SH999";
        // As on Jellyfin: the rule carries the series' name but no series id.
        _host.SeriesRules.Add(new SeriesRule("rule-x", null, "Bonanza"));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-x", TimerId = "t-x", ProgramId = "p1", SeriesTimerId = "rule-x", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        var service = NewService();

        var outcome = await service.RecordSeriesAsync("p2", Alex, null, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Joined);
        Assert.NotEqual("rule-x", outcome.Id);
        Assert.Equal(2, _host.SeriesRules.Count);
    }

    [Fact]
    public async Task UnnumberedSuggestion_TimerListedLate_OwnerStillReachesIt()
    {
        var source = AddProgram(Guid.NewGuid().ToString("N"), Start, "Stephen King: A Necessary Evil", null, null);
        source.Overview = "Interviews explore the author's work.";
        var future = AddProgram(Guid.NewGuid().ToString("N"), Start.AddDays(1), source.SeriesName!, null, null);
        future.Overview = source.Overview;
        _clock.UtcNow = Start.AddMinutes(5);
        _host.ScheduleReturnsNoIds = true;
        _host.ScheduledTimersHidden = true;
        var owners = NewService();
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), owners);
        var options = await incomplete.GetCompleteShowingOptionsAsync(source.Id, Jordan);
        Assert.False(options.CanSearch);

        Assert.True(await incomplete.ScheduleSuggestedShowingAsync(source.Id, options.Showing!, Jordan, CancellationToken.None));
        Assert.Single(ReadIncompleteStore().PendingTimerOwners);

        // The next pass that lists the timers again finds it.
        _host.RevealScheduledTimers();
        _clock.Advance(IncompleteRecordingRules.SafetyNetInterval);
        await incomplete.RunAsync(CancellationToken.None);
        await owners.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { "api-" + future.Id }, (await owners.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
        Assert.Empty(ReadIncompleteStore().PendingTimerOwners);
    }

    [Fact]
    public async Task RecordSeries_SameNameRuleWithNoTimersOnAnotherChannel_GetsItsOwnRule()
    {
        var program = AddProgram("p1", Start);
        program.ChannelId = "chan-1";
        // As on Jellyfin: no series id and nothing recorded yet, only the name, one channel and
        // the program it was made from (no longer in the guide).
        _host.SeriesRules.Add(new SeriesRule("rule-x", null, "Bonanza", "gone", "chan-2"));
        var service = NewService();

        var outcome = await service.RecordSeriesAsync("p1", Alex, null, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Joined);
        Assert.NotEqual("rule-x", outcome.Id);
        Assert.Equal(2, _host.SeriesRules.Count);
    }

    [Fact]
    public async Task RecordSeries_SameNameRuleWithNoTimersOnTheSameChannel_IsJoined()
    {
        var program = AddProgram("p1", Start);
        program.ChannelId = "chan-1";
        _host.SeriesRules.Add(new SeriesRule("rule-x", null, "Bonanza", "gone", "chan-1"));
        var service = NewService();

        var outcome = await service.RecordSeriesAsync("p1", Alex, null, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Joined);
        Assert.Equal("rule-x", outcome.Id);
    }

    [Fact]
    public async Task RecordSeries_SameNameRuleMadeFromAnotherSeriesOnAnyChannel_GetsItsOwnRule()
    {
        AddProgram("p1", Start);
        var made = AddProgram("p0", Start.AddHours(-5));
        made.SeriesId = "SH999";
        _host.SeriesRules.Add(new SeriesRule("rule-x", null, "Bonanza", "p0", null, true));
        var service = NewService();

        var outcome = await service.RecordSeriesAsync("p1", Alex, null, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Joined);
        Assert.Equal(2, _host.SeriesRules.Count);
    }

    [Fact]
    public async Task RecordSeries_RuleWithAnUnnumberedTimerAndAnotherSeriesTimer_GetsItsOwnRule()
    {
        AddProgram("p1", Start);
        var other = AddProgram("p2", Start.AddHours(1));
        other.SeriesId = "SH999";
        var unknown = AddProgram("p3", Start.AddHours(2));
        unknown.SeriesId = null;
        _host.SeriesRules.Add(new SeriesRule("rule-x", null, "Bonanza"));
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-x1", TimerId = "t-x1", ProgramId = "p1", SeriesTimerId = "rule-x", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-x3", TimerId = "t-x3", ProgramId = "p3", SeriesTimerId = "rule-x", StartDate = Start.AddHours(2), EndDate = Start.AddHours(2).AddMinutes(20), Status = "New" });
        var service = NewService();

        var outcome = await service.RecordSeriesAsync("p2", Alex, null, CancellationToken.None);

        Assert.False(outcome!.Joined);
        Assert.NotEqual("rule-x", outcome.Id);
    }

    private sealed class FlakyOwners : IRecordingOwners
    {
        private readonly IRecordingOwners _inner;

        public FlakyOwners(IRecordingOwners inner) => _inner = inner;

        public bool Fail { get; set; }

        public IReadOnlyList<Guid> OwnersOfItems(IEnumerable<Guid> itemIds) => _inner.OwnersOfItems(itemIds);

        public void AddTimerOwners(string timerId, IEnumerable<Guid> owners)
        {
            if (Fail)
            {
                throw new IOException("owners unavailable");
            }

            _inner.AddTimerOwners(timerId, owners);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuggestedShowing_HandOverFails_RetriedOnTheNextPass(bool numbered)
    {
        var source = numbered ? AddAiring(Guid.NewGuid().ToString("N"), Start) : AddProgram(Guid.NewGuid().ToString("N"), Start, "Stephen King: A Necessary Evil", null, null);
        source.Overview = "Interviews explore the author's work.";
        var future = numbered ? AddAiring(Guid.NewGuid().ToString("N"), Start.AddDays(1)) : AddProgram(Guid.NewGuid().ToString("N"), Start.AddDays(1), source.SeriesName!, null, null);
        future.Overview = source.Overview;
        _clock.UtcNow = Start.AddMinutes(5);
        var owners = NewService();
        var flaky = new FlakyOwners(owners) { Fail = true };
        var incomplete = NewIncompleteService(new IncompleteRecordingSettings(), flaky);
        var options = await incomplete.GetCompleteShowingOptionsAsync(source.Id, Jordan);

        Assert.True(await incomplete.ScheduleSuggestedShowingAsync(source.Id, options.Showing!, Jordan, CancellationToken.None));
        await owners.SyncAsync(CancellationToken.None);
        Assert.Empty((await owners.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);

        flaky.Fail = false;
        await incomplete.RunAsync(CancellationToken.None);
        await owners.SyncAsync(CancellationToken.None);

        Assert.Equal(new[] { "api-" + future.Id }, (await owners.GetMineAsync(Jordan, CancellationToken.None)).TimerIds);
    }

    [Fact]
    public async Task RebuildFails_FileReadableAgain_KeepsOwnersRecordedMeanwhile()
    {
        var tagged = _host.AddItem("/rec/a.ts");
        tagged.Tags = new[] { "Recorded by Jordan" };
        AddProgram("p1", Start);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "{ not json");
        File.WriteAllText(StorePath + ".bak", "also not json");
        _host.UserLookupThrows = true;
        var service = NewService();
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);

        // The storage comes back with a good copy before the rebuild gets to run.
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new OwnershipStoreData { Items = { [tagged.Id] = new List<Guid> { Jordan } } }));
        _host.UserLookupThrows = false;

        Assert.Equal(new[] { "api-p1" }, (await service.GetMineAsync(Alex, CancellationToken.None)).TimerIds);
        Assert.Equal(new[] { tagged.Id }, (await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Equal(new[] { Alex }, ReadStore().Timers["api-p1"]);
    }

    private OwnershipStoreData ReadStoreOrEmpty() =>
        _stateFile.LastWritten(StorePath) is { } json ? JsonSerializer.Deserialize<OwnershipStoreData>(json)! : new OwnershipStoreData();
}
