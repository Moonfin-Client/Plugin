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
        AddProgram("p1", Start, episode: 5);
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
    public async Task ItemReAdded_ByRerunBeforePathCaptured_TakesTimerOwnersNotTombstone()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);

        AddProgram("p1", Start, episode: 5);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);

        // Jellyfin adds the item as the recording starts, before any sync has seen the file.
        _host.StartRecording(TimerOf("p1"), old.Path!, 1, 5);
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        var saved = ReadStore();
        Assert.Equal(new[] { Alex }, saved.Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
        Assert.False(saved.Removed.ContainsKey(item.Id));

        // The sync that captures the file adds no one else.
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
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
        AddProgram("p0", Start.AddDays(-2));
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
        AddProgram("p1", Start, episode: 5);
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

    // Compat fix: a file written after its recording left the library is a new recording.

    [Fact]
    public async Task Rerun_ByUnownedTimer_FileWrittenAfterRemoval_OldOwnersNotRestored()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);
        var removedAt = ReadStore().Removed[old.Id].RemovedAt;

        // A timer nobody owns through Moonfin (an official client, say) records the rerun to the same file.
        _clock.Advance(TimeSpan.FromDays(2));
        AddProgram("p1", Start, episode: 5);
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-x", TimerId = "t-x", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.StartRecording(TimerOf("p1"), old.Path!, 1, 5);
        _host.Write();
        Assert.True(_host.LastWrite[old.Path!] > removedAt + RecordingOwnershipService.FileClockTolerance);
        var item = Rerun(old);
        var tagWrites = _host.TagWrites.Count;
        await service.OnItemAddedAsync(item, CancellationToken.None);

        var saved = ReadStore();
        Assert.False(saved.Items.ContainsKey(item.Id));
        Assert.False(saved.Removed.ContainsKey(item.Id));
        Assert.Empty(TagsOf(item));
        Assert.Equal(tagWrites, _host.TagWrites.Count);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Contains(_log.Entries, e => e.Message.Contains("it is a new recording"));

        // Nothing brings Jordan back later either.
        await service.SyncAsync(CancellationToken.None);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task Rerun_ItemAddedBeforeTimerSeenRecording_FileWrittenAfterRemoval_OnlyNewOwners()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);

        // Alex's timer writes the file, but the listing doesn't show it in progress yet.
        _clock.Advance(TimeSpan.FromHours(3));
        AddProgram("p1", Start, episode: 5);
        await service.RecordProgramAsync("p1", Alex, CancellationToken.None);
        _host.Files[old.Path!] = 1_000_000;
        _host.LastWrite[old.Path!] = _clock.UtcNow;
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.False(ReadStore().Items.ContainsKey(item.Id));
        Assert.False(ReadStore().Removed.ContainsKey(item.Id));

        // The next sync sees it recording and attaches Alex alone.
        _host.StartRecording(TimerOf("p1"), old.Path!, 1, 5);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Alex }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Alex" }, TagsOf(item));
    }

    [Fact]
    public async Task ReturnedFile_WrittenBeforeRemoval_RestoresOwners()
    {
        var service = NewService();
        var old = _host.AddItem("/rec/bonanza.ts");
        _host.Files[old.Path!] = 5_000_000;
        _host.LastWrite[old.Path!] = _clock.UtcNow.AddHours(-2);
        await service.ClaimAsync(OwnedKind.Item, old.Id.ToString(), Jordan, CancellationToken.None);

        // The storage drops out, the scan removes the item, and the same file comes back a day later.
        _host.Items.Remove(old);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(1));
        var back = Rerun(old);
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[back.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(back));
        Assert.Empty(ReadStore().Removed);
    }

    [Fact]
    public async Task ReturnedFile_WrittenWithinClockTolerance_RestoresOwners()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);
        var removedAt = ReadStore().Removed[old.Id].RemovedAt;

        // The storage's clock runs a little ahead of the server's.
        _host.Files[old.Path!] = 5_000_000;
        _host.LastWrite[old.Path!] = removedAt + TimeSpan.FromSeconds(30);
        _clock.Advance(TimeSpan.FromDays(1));
        var back = Rerun(old);
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[back.Id]);
    }

    [Fact]
    public async Task FileWrittenPastTolerance_NotRestored()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);
        var removedAt = ReadStore().Removed[old.Id].RemovedAt;

        _host.Files[old.Path!] = 5_000_000;
        _host.LastWrite[old.Path!] = removedAt + RecordingOwnershipService.FileClockTolerance + TimeSpan.FromSeconds(1);
        _clock.Advance(TimeSpan.FromDays(1));
        var back = Rerun(old);
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.False(ReadStore().Items.ContainsKey(back.Id));
        Assert.Empty(ReadStore().Removed);
    }

    // Fix round 1, Important 1: a finished owned timer left on the path of a deleted recording.

    // Jordan's timer records /rec/bonanza.ts two days before Start and its item is added; returns the item.
    private async Task<LibraryItem> JordanRecordedByTimer(RecordingOwnershipService service)
    {
        AddProgram("p0", Start.AddDays(-2));
        var now = _clock.UtcNow;
        _clock.UtcNow = Start.AddDays(-2).AddMinutes(-5);
        await service.RecordProgramAsync("p0", Jordan, CancellationToken.None);
        var first = TimerOf("p0");

        // Recorded during its window, as a server does.
        _clock.UtcNow = Start.AddDays(-2).AddMinutes(5);
        _host.StartRecording(first, "/rec/bonanza.ts", 1, 5);
        await service.SyncAsync(CancellationToken.None);
        var old = _host.AddItem("/rec/bonanza.ts");
        _host.LastWrite[old.Path!] = _clock.UtcNow;
        await service.OnItemAddedAsync(old, CancellationToken.None);
        _host.StopRecording(first);
        _clock.UtcNow = now;
        Assert.Equal(new[] { Jordan }, ReadStore().Items[old.Id]);
        return old;
    }

    // A rerun on the same file, recorded by a timer nobody owns through Moonfin.
    private LibraryItem UnownedRerun(LibraryItem old)
    {
        AddProgram("p1", Start, episode: 5);
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-x", TimerId = "t-x", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.StartRecording(TimerOf("p1"), old.Path!, 1, 5);
        _host.Write();
        return Rerun(old);
    }

    [Fact]
    public async Task ItemRemoved_ForgetsFinishedTimerThatRecordedIt()
    {
        var service = NewService();
        var old = await JordanRecordedByTimer(service);

        _host.Items.Remove(old);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);

        var saved = ReadStore();
        Assert.False(saved.Timers.ContainsKey("api-p0"));
        Assert.False(saved.TimerPaths.ContainsKey("api-p0"));
        Assert.Equal(new[] { Jordan }, saved.Removed[old.Id].Owners);
    }

    [Fact]
    public async Task ItemRemoved_KeepsCancelledTimerWaitingForItsOwnRecording()
    {
        // A timer cancelled mid-recording keeps its owners for its item; removing another item must not forget it.
        var service = NewService();
        var other = await JordanRecordedByTimer(service);
        AddProgram("p2", Start, episode: 6);
        await service.RecordProgramAsync("p2", Alex, CancellationToken.None);
        _host.StartRecording(TimerOf("p2"), "/rec/other.ts", 1, 6);
        await service.SyncAsync(CancellationToken.None);
        _host.LastWrite["/rec/other.ts"] = _clock.UtcNow;
        _host.CancelTimer(TimerOf("p2"));
        await service.OnTimerCancelledAsync("t-p2", "/rec/other.ts", CancellationToken.None);

        _host.Items.Remove(other);
        await service.OnItemRemovedAsync(other.Id, CancellationToken.None);
        Assert.Equal(new[] { Alex }, ReadStore().Timers["api-p2"]);

        // The cancelled timer is unlisted now, and syncs stamp it done without its item; the item
        // still arrives three days later, its file last written at the cancel.
        await service.SyncAsync(CancellationToken.None);
        Assert.True(ReadStore().TimersMissingSince.ContainsKey("api-p2"));
        _clock.Advance(TimeSpan.FromDays(3));
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(new[] { Alex }, ReadStore().Timers["api-p2"]);

        var item = _host.AddItem("/rec/other.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { Alex }, ReadStore().Items[item.Id]);
    }

    // Fix round 2: a done timer whose recording isn't in the library.

    // Jordan's timer records the path and finishes; no item for it is ever in the library.
    private async Task<ScheduledTimer> JordanTimerDoneWithoutItem(RecordingOwnershipService service, string path = "/rec/bonanza.ts")
    {
        AddProgram("p0", Start.AddDays(-2));
        await service.RecordProgramAsync("p0", Jordan, CancellationToken.None);
        var timer = TimerOf("p0");
        _host.StartRecording(timer, path, 1, 5);
        await service.SyncAsync(CancellationToken.None);
        _host.LastWrite[path] = _clock.UtcNow;
        _host.StopRecording(timer);
        Assert.Equal(path, ReadStore().TimerPaths["api-p0"]);
        return timer;
    }

    [Fact]
    public async Task DoneTimerWhoseItemNeverArrives_ForgottenAfterRetention()
    {
        var service = NewService();
        await JordanTimerDoneWithoutItem(service);

        await service.SyncAsync(CancellationToken.None);
        Assert.True(ReadStore().Timers.ContainsKey("api-p0"));

        _clock.Advance(TimeSpan.FromDays(8));
        await service.SyncAsync(CancellationToken.None);

        var saved = ReadStore();
        Assert.False(saved.Timers.ContainsKey("api-p0"));
        Assert.False(saved.TimerPaths.ContainsKey("api-p0"));
        Assert.False(saved.TimersMissingSince.ContainsKey("api-p0"));
    }

    [Fact]
    public async Task DoneTimerWithoutItem_UnlistedNoTombstone_UnownedRerunNotClaimed()
    {
        var service = NewService();
        var timer = await JordanTimerDoneWithoutItem(service);

        // The server drops the finished one-off from its listing; a sync stamps it done without its item.
        _host.Timers.Remove(timer);
        await service.SyncAsync(CancellationToken.None);
        Assert.True(ReadStore().TimersMissingSince.ContainsKey("api-p0"));

        // Two days on, a timer nobody owns records a rerun to the same file.
        _clock.Advance(TimeSpan.FromDays(2));
        AddProgram("p1", Start, episode: 5);
        _host.Timers.Add(new ScheduledTimer { ApiId = "api-x", TimerId = "t-x", ProgramId = "p1", StartDate = Start, EndDate = Start.AddMinutes(20), Status = "New" });
        _host.StartRecording(TimerOf("p1"), "/rec/bonanza.ts", 1, 5);
        _host.Write();
        var item = _host.AddItem("/rec/bonanza.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        await service.SyncAsync(CancellationToken.None);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Empty(TagsOf(item));
    }

    [Fact]
    public async Task DeletedWhileRecording_TimerForgottenAfterRetention()
    {
        AddProgram("p0", Start.AddDays(-2));
        var service = NewService();
        await service.RecordProgramAsync("p0", Jordan, CancellationToken.None);
        var timer = TimerOf("p0");
        _host.StartRecording(timer, "/rec/bonanza.ts", 1, 5);
        await service.SyncAsync(CancellationToken.None);
        var item = _host.AddItem("/rec/bonanza.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);

        // Deleted while still recording: the live timer is kept, then finishes with no item.
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        Assert.True(ReadStore().Timers.ContainsKey("api-p0"));
        _host.StopRecording(timer);
        await service.SyncAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(8));
        await service.SyncAsync(CancellationToken.None);

        Assert.False(ReadStore().Timers.ContainsKey("api-p0"));
        Assert.Empty(ReadStore().Removed);
    }

    // Fix round 2, Minor 1: the post-recording allowance boundary.

    private async Task<(LibraryItem Item, RecordingOwnershipService Service)> FinishedTimerFileWrittenAt(TimeSpan afterAllowance)
    {
        AddProgram("p0", Start.AddDays(-2));
        var service = NewService();
        await service.RecordProgramAsync("p0", Jordan, CancellationToken.None);
        var timer = TimerOf("p0");
        _host.StartRecording(timer, "/rec/bonanza.ts", 1, 5);
        await service.SyncAsync(CancellationToken.None);
        _host.StopRecording(timer);

        // The item reaches the library only now, while the listing still has the finished timer.
        _host.LastWrite["/rec/bonanza.ts"] = timer.WindowEnd + RecordingOwnershipService.PostRecordingAllowance + RecordingOwnershipService.FileClockTolerance + afterAllowance;
        var item = _host.AddItem("/rec/bonanza.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        return (item, service);
    }

    [Fact]
    public async Task FinishedTimer_FileWrittenPastAllowance_NotClaimed()
    {
        var (item, service) = await FinishedTimerFileWrittenAt(TimeSpan.FromSeconds(1));

        Assert.False(ReadStore().Items.ContainsKey(item.Id));
        await service.SyncAsync(CancellationToken.None);
        Assert.False(ReadStore().Items.ContainsKey(item.Id));
    }

    [Fact]
    public async Task FinishedTimer_FileWrittenWithinAllowance_Claimed()
    {
        var (item, _) = await FinishedTimerFileWrittenAt(TimeSpan.FromSeconds(-1));

        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(item));
    }

    [Fact]
    public async Task Rerun_ByUnownedTimer_WithStaleFinishedOwnedTimer_DoesNotGiveOldOwner()
    {
        var service = NewService();
        var old = await JordanRecordedByTimer(service);

        // A restart in between, so the removal can't forget the timer: it stays on the path.
        service = NewService();
        _host.Items.Remove(old);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(old.Path, ReadStore().TimerPaths["api-p0"]);

        _clock.Advance(TimeSpan.FromDays(2));
        var item = UnownedRerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.False(ReadStore().Items.ContainsKey(item.Id));
        Assert.Empty(ReadStore().Removed);
        Assert.Empty(TagsOf(item));

        await service.SyncAsync(CancellationToken.None);
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Empty(TagsOf(item));
    }

    [Fact]
    public async Task Rerun_ByUnownedTimer_WithStaleFinishedOwnedTimer_AfterTombstoneExpired_DoesNotGiveOldOwner()
    {
        var service = NewService();
        var old = await JordanRecordedByTimer(service);
        service = NewService();
        _host.Items.Remove(old);
        await service.OnItemRemovedAsync(old.Id, CancellationToken.None);

        // Eight days on, the tombstone is gone but the timer still sits on the path.
        _clock.Advance(TimeSpan.FromDays(8));
        await service.SyncAsync(CancellationToken.None);
        Assert.Empty(ReadStore().Removed);
        Assert.Equal(old.Path, ReadStore().TimerPaths["api-p0"]);

        var item = UnownedRerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);
        await service.SyncAsync(CancellationToken.None);

        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
        Assert.Empty(TagsOf(item));
    }

    // Fix round 1, Important 2: the listing for a returning item.

    // Jordan claims a recording whose file was written before now, then the item leaves.
    private async Task<LibraryItem> ClaimedThenRemoved(RecordingOwnershipService service, string path, bool knownTime = true)
    {
        var item = _host.AddItem(path);
        if (knownTime)
        {
            _host.Files[path] = 5_000_000;
            _host.LastWrite[path] = _clock.UtcNow.AddHours(-2);
        }

        await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        return item;
    }

    [Fact]
    public async Task ReturnedFile_TimersUnavailable_StillRestoresOwners()
    {
        var service = NewService();
        var known = await ClaimedThenRemoved(service, "/rec/a.ts");
        var unknown = await ClaimedThenRemoved(service, "/rec/b.ts", knownTime: false);

        // The startup scan re-adds them before Live TV is ready.
        _host.TimersUnavailable = true;
        _clock.Advance(TimeSpan.FromHours(1));
        var knownBack = Rerun(known);
        var unknownBack = Rerun(unknown);
        await service.OnItemAddedAsync(knownBack, CancellationToken.None);
        await service.OnItemAddedAsync(unknownBack, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[knownBack.Id]);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[unknownBack.Id]);
        Assert.Empty(ReadStore().Removed);
        Assert.Contains(_log.Entries, e => e.Kind == LogKind.Warn && e.Message.Contains("Could not list the timers"));
    }

    [Fact]
    public async Task ManyReturnedFiles_ListTimersAtMostOnce()
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
    public async Task ReAdded_FileTimeUnknown_KeepsTombstoneBehaviour()
    {
        var service = NewService();
        var old = await DeletedJordanRecording(service);

        // No file the host can read a time from: an unowned rerun and a returned file look the same.
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(_host.GetFileWrittenAt(old.Id));
        var back = Rerun(old);
        Assert.Null(_host.GetFileWrittenAt(back.Id));
        await service.OnItemAddedAsync(back, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[back.Id]);
        Assert.Equal(new[] { "Recorded by Jordan" }, TagsOf(back));
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
    public async Task CancelWithActivePath_ItemExists_AttachesAndKeepsUntilSyncForgetsTimer()
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
            Assert.Equal(new[] { Jordan }, saved.Timers["api-p1"]);
            Assert.Equal("/rec/a.ts", saved.TimerPaths["api-p1"]);
        }

        _host.Timers.Remove(timer);
        _host.ActivePaths.Remove(timer.TimerId);
        await service.SyncAsync(CancellationToken.None);

        var after = ReadStore();
        Assert.Empty(after.Timers);
        Assert.Empty(after.TimerPaths);
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
    public async Task ClaimItem_HostCantTellRecordings_StillClaims()
    {
        var item = _host.AddItem("/rec/a.ts");
        _host.RecordingKindUnknown = true;
        var service = NewService();

        var owners = await service.ClaimAsync(OwnedKind.Item, item.Id.ToString(), Jordan, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, owners!.Select(o => o.UserId));
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

    // A recording of Jordan's timer, removed, while the timer list can no longer be read and
    // still shows the timer recording. Returns the item and when its file was written.
    private async Task<(LibraryItem Item, RecordingOwnershipService Service)> RemovedWhileListingIsStale(TimeSpan? removedAfterStart = null)
    {
        AddProgram("p1", Start);
        _host.Clock = () => _clock.UtcNow;
        var service = NewService();
        await service.RecordProgramAsync("p1", Jordan, CancellationToken.None);
        _clock.UtcNow = Start.AddMinutes(5);
        _host.StartRecording(TimerOf("p1"), "/rec/bonanza.ts", 1, 1);
        _host.Write();
        await service.SyncAsync(CancellationToken.None);
        var item = _host.AddItem("/rec/bonanza.ts");
        await service.OnItemAddedAsync(item, CancellationToken.None);
        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);

        _host.TimersUnavailable = true;
        _clock.UtcNow = Start + (removedAfterStart ?? TimeSpan.FromHours(3));
        _host.Items.Remove(item);
        await service.OnItemRemovedAsync(item.Id, CancellationToken.None);
        return (item, service);
    }

    [Fact]
    public async Task StaleListing_NewerFileOnTheTimersPath_DoesNotTakeItsOwners()
    {
        var (old, service) = await RemovedWhileListingIsStale();

        // Something nobody owns records to the same file after the removal.
        _clock.Advance(TimeSpan.FromMinutes(10));
        _host.LastWrite[old.Path!] = _clock.UtcNow;
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.False(ReadStore().Items.ContainsKey(item.Id));
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
    }

    [Fact]
    public async Task StaleListing_UnchangedFileComesBack_KeepsItsOwners()
    {
        var (old, service) = await RemovedWhileListingIsStale();

        _clock.Advance(TimeSpan.FromMinutes(10));
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.Equal(new[] { Jordan }, ReadStore().Items[item.Id]);
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
    public async Task StaleListing_NewerFileWithinThePostRecordingAllowance_DoesNotTakeItsOwners()
    {
        // Removed five minutes after the 20-minute window, rerecorded ten minutes later: well
        // inside the hour allowed for post-processing.
        var (old, service) = await RemovedWhileListingIsStale(TimeSpan.FromMinutes(25));

        _clock.Advance(TimeSpan.FromMinutes(10));
        _host.LastWrite[old.Path!] = _clock.UtcNow;
        var item = Rerun(old);
        await service.OnItemAddedAsync(item, CancellationToken.None);

        Assert.False(ReadStore().Items.ContainsKey(item.Id));
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);

        // A later attach pass, run by another owned timer's cancel while the listing is still
        // unavailable, doesn't give the file to the old timer either.
        // The listing recovers, showing Jordan's timer finished, and a sync runs; then another
        // owned timer's cancel runs an attach pass while the listing is unavailable again.
        _host.StopRecording(TimerOf("p1"));
        AddProgram("p9", Start.AddHours(5));
        _host.TimersUnavailable = false;
        await service.RecordProgramAsync("p9", Alex, CancellationToken.None);
        _host.StartRecording(TimerOf("p9"), "/rec/p9.ts", 1, 9);
        await service.SyncAsync(CancellationToken.None);
        var alexs = _host.AddItem("/rec/p9.ts");
        _host.TimersUnavailable = true;
        await service.OnTimerCancelledAsync("t-p9", "/rec/p9.ts", CancellationToken.None);

        // The pass ran: the cancelled timer's own recording is Alex's.
        Assert.Equal(new[] { Alex }, ReadStore().Items[alexs.Id]);
        Assert.False(ReadStore().Items.ContainsKey(item.Id));
        Assert.Empty((await service.GetMineAsync(Jordan, CancellationToken.None)).ItemIds);
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
