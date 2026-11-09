using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Moonfin.WatchParty.Tests;

public class WatchPartyEngineTests
{
    private const string Maya = "maya";
    private const string Jordan = "jordan";
    private const string Sam = "sam";
    private const string Movie = "movie1";
    private const string Kids = "kids1";

    private readonly FakeHost _host = new();
    private readonly WatchPartySettings _settings = new();
    private DateTime _now = new(2026, 11, 9, 21, 0, 0, DateTimeKind.Utc);
    private Func<TimeSpan, CancellationToken, Task> _delay = (_, ct) => Task.Delay(Timeout.Infinite, ct);
    private readonly WatchPartyEngine _engine;

    public WatchPartyEngineTests()
    {
        _host.Items[Movie] = new WatchPartyItem { Id = Movie, Title = "Avatar", Type = "Movie", Year = 2022 };
        _host.Items[Kids] = new WatchPartyItem { Id = Kids, Title = "Bluey", Type = "Episode", SeriesName = "Bluey", Season = 2, Episode = 3, RuntimeSeconds = 100 };
        _engine = new WatchPartyEngine(_host, () => _settings, () => _now, (t, ct) => _delay(t, ct));
    }

    [Fact]
    public void CreateAnswersTheRoomAndATokenAndTheFirstPollHasTheSnapshotAndChat()
    {
        var (id, token) = Create(Maya, new CreateBody { ContentId = Movie });
        var messages = Poll(Maya, id, token, 0, out var cursor);

        Assert.True(cursor > 0);
        Assert.Equal(new[] { "snapshot", "chat_history" }, Types(messages));
        var room = messages[0].GetProperty("room");
        Assert.Equal("host", room.GetProperty("self_role").GetString());
        Assert.Equal(Movie, room.GetProperty("selected_content_id").GetString());
        Assert.Equal("Avatar", room.GetProperty("content_title").GetString());
        Assert.Matches("^[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{6}$", room.GetProperty("code").GetString());
        Assert.Equal("/Moonfin/Party/" + room.GetProperty("code").GetString(), room.GetProperty("invite_path").GetString());
        Assert.Equal(JsonValueKind.Null, room.GetProperty("selected_file_id").ValueKind);
        Assert.Equal("ready", room.GetProperty("members")[0].GetProperty("status").GetString());

        Assert.Empty(Poll(Maya, id, token, cursor, out _));
    }

    [Fact]
    public void JoinTakesTheCodeInAnyCaseWithSpacesAndDashesOrTheRoomId()
    {
        var (id, _) = Create(Maya);
        var code = Code(id);
        var spaced = (code.Substring(0, 3) + " - " + code.Substring(3)).ToLowerInvariant();

        var byCode = Json(_engine.Join(Caller(Jordan), new JoinBody { Code = spaced }));
        var byId = Json(_engine.Join(Caller(Sam), new JoinBody { RoomId = id }));

        Assert.Equal(id, byCode.GetProperty("room").GetProperty("room_id").GetString());
        Assert.Equal("guest", byId.GetProperty("room").GetProperty("self_role").GetString());
        Assert.Equal(3, byId.GetProperty("room").GetProperty("members").GetArrayLength());
        Assert.Equal("not_found", _engine.Join(Caller(Sam), new JoinBody { Code = "ZZZZZZ" }).Code);
    }

    [Fact]
    public void TheSameUserOnTwoDevicesIsTwoMembersAndRejoiningKeepsTheToken()
    {
        var (id, _) = Create(Maya);
        var phone = Json(_engine.Join(Caller(Jordan, "phone"), new JoinBody { RoomId = id })).GetProperty("token").GetString();
        var tv = Json(_engine.Join(Caller(Jordan, "tv"), new JoinBody { RoomId = id })).GetProperty("token").GetString();
        var again = Json(_engine.Join(Caller(Jordan, "phone"), new JoinBody { RoomId = id })).GetProperty("token").GetString();

        Assert.NotEqual(phone, tv);
        Assert.Equal(phone, again);
    }

    [Fact]
    public void ARoomTokenOnlyWorksForItsOwnUser()
    {
        var (id, token) = Create(Maya);

        var result = _engine.Stop(Caller(Jordan), id, token);

        Assert.Equal(403, result.Status);
        Assert.Equal("not_allowed", result.Code);
    }

    [Fact]
    public void PinsAreAskedForAndWrongOnesAreCappedPerMinute()
    {
        var (id, _) = Create(Maya, new CreateBody { Pin = "4321" });

        Assert.Equal("pin_required", _engine.Join(Caller(Jordan), new JoinBody { RoomId = id }).Code);
        for (var i = 0; i < WatchPartyEngine.MaxPinFailures; i++)
        {
            Assert.Equal("wrong_pin", _engine.Join(Caller(Jordan), new JoinBody { RoomId = id, Pin = "0000" }).Code);
        }

        var locked = _engine.Join(Caller(Jordan), new JoinBody { RoomId = id, Pin = "4321" });
        Assert.Equal("too_many_attempts", locked.Code);
        Assert.Equal(429, locked.Status);
        Assert.InRange(locked.RetryAfterSeconds, 1, 60);
        Assert.Equal(locked.RetryAfterSeconds, Json(locked).GetProperty("retry_after_seconds").GetInt32());

        Assert.Equal(200, _engine.Join(Caller(Sam), new JoinBody { RoomId = id, Pin = "4321" }).Status);
        _now = _now.AddMinutes(1);
        Assert.Equal(200, _engine.Join(Caller(Jordan), new JoinBody { RoomId = id, Pin = "4321" }).Status);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    public void PinsAreFourToEightDigits(string pin)
    {
        Assert.Equal("bad_request", _engine.Create(Caller(Maya), new CreateBody { Pin = pin }).Code);
    }

    [Fact]
    public void AFullRoomRefusesMoreMembers()
    {
        _settings.MaxMembers = 2;
        var (id, _) = Create(Maya);
        _engine.Join(Caller(Jordan), new JoinBody { RoomId = id });

        var result = _engine.Join(Caller(Sam), new JoinBody { RoomId = id });

        Assert.Equal("full", result.Code);
        Assert.Equal(409, result.Status);
    }

    [Fact]
    public void RoomsOnlyShowToUsersWhoCanPlayTheItem()
    {
        _host.Blocked.Add((Jordan, Movie));
        var (movieRoom, _) = Create(Maya, new CreateBody { ContentId = Movie });
        var (emptyRoom, _) = Create(Sam);

        var listed = Json(_engine.Rooms(Caller(Jordan))).GetProperty("rooms").EnumerateArray().Select(r => r.GetProperty("room_id").GetString()).ToList();

        Assert.Equal(new[] { emptyRoom }, listed);
        Assert.Equal("no_access", _engine.Join(Caller(Jordan), new JoinBody { RoomId = movieRoom }).Code);
        var summary = Json(_engine.Rooms(Caller(Maya))).GetProperty("rooms").EnumerateArray().Single(r => r.GetProperty("room_id").GetString() == movieRoom);
        Assert.False(summary.TryGetProperty("code", out _));
        Assert.True(summary.GetProperty("is_member").GetBoolean());
    }

    [Fact]
    public void StagingSomethingAMemberCantPlayLeavesThemInTheRoomNotWatching()
    {
        _host.Blocked.Add((Jordan, Movie));
        var (id, token) = Create(Maya);
        var jordan = Join(Jordan, id);
        Poll(Jordan, id, jordan, 0, out var cursor);

        Assert.Equal(200, _engine.Stage(Caller(Maya), id, token, new RoomBody { ContentId = Movie }).Status);

        var messages = Poll(Jordan, id, jordan, cursor, out _);
        var notice = messages.Single(m => Type(m) == "notice");
        Assert.Equal("no_access", notice.GetProperty("kind").GetString());
        Assert.Equal("Avatar", notice.GetProperty("content_title").GetString());
        var me = messages.Last(m => Type(m) == "snapshot").GetProperty("room").GetProperty("members").EnumerateArray().Single(m => m.GetProperty("is_self").GetBoolean());
        Assert.Equal("not_watching", me.GetProperty("status").GetString());
    }

    [Fact]
    public void GuestsNeedThePolicyToControlPlayback()
    {
        var (id, host, guest) = Playing();
        Poll(Jordan, id, guest, 0, out var cursor);

        Send(Jordan, id, guest, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 10 });
        var refused = Poll(Jordan, id, guest, cursor, out cursor);
        Assert.Equal("not_allowed", refused.Single(m => Type(m) == "error").GetProperty("code").GetString());

        _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "guests_can_pause" });
        Send(Jordan, id, guest, new InboundMessage { Type = "transport_request", Action = "seek", PositionSeconds = 50 });
        Send(Jordan, id, guest, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 12 });
        var messages = Poll(Jordan, id, guest, cursor, out cursor);
        Assert.Single(messages, m => Type(m) == "error");
        var command = messages.Single(m => Type(m) == "transport_command").GetProperty("command");
        Assert.Equal("pause", command.GetProperty("action").GetString());
        Assert.Equal(Jordan, command.GetProperty("by_display_name").GetString());

        _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "everyone" });
        Send(Jordan, id, guest, new InboundMessage { Type = "transport_request", Action = "seek", PositionSeconds = 50 });
        Assert.Contains(Poll(Jordan, id, guest, cursor, out _), m => Type(m) == "transport_command");
    }

    [Fact]
    public void EveryoneControlIsRefusedWhenTheAdminTurnedItOff()
    {
        _settings.EveryoneControl = false;
        var (id, token) = Create(Maya);

        Assert.Equal("not_allowed", _engine.Policy(Caller(Maya), id, token, new RoomBody { GuestControlPolicy = "everyone" }).Code);
        Assert.Equal("not_allowed", _engine.Create(Caller(Sam), new CreateBody { GuestControlPolicy = "everyone" }).Code);
    }

    [Fact]
    public void APlayFromPausedRunsInASecondAndPauseAndSeekIn300Milliseconds()
    {
        var (id, host, _) = Playing();
        Poll(Maya, id, host, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 20 });
        var pause = Command(Poll(Maya, id, host, cursor, out cursor));
        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "play", PositionSeconds = 20 });
        var play = Command(Poll(Maya, id, host, cursor, out cursor));
        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "seek", PositionSeconds = 90 });
        var seek = Command(Poll(Maya, id, host, cursor, out _));

        Assert.Equal(At(300), pause.GetProperty("execute_at").GetString());
        Assert.Equal("paused", pause.GetProperty("playback_state").GetString());
        Assert.Equal(At(1000), play.GetProperty("execute_at").GetString());
        Assert.Equal("playing", play.GetProperty("playback_state").GetString());
        Assert.Equal(At(300), seek.GetProperty("execute_at").GetString());
        Assert.Equal("playing", seek.GetProperty("playback_state").GetString());
        Assert.Equal(90, seek.GetProperty("position_seconds").GetDouble());
    }

    [Fact]
    public void StartWaitsForEveryReadyPlayerThenCountsDown()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        var guest = Join(Jordan, id);
        Poll(Jordan, id, guest, 0, out var cursor);

        var started = Json(_engine.Start(Caller(Maya), id, host, new RoomBody { PositionSeconds = 30 })).GetProperty("room");
        Assert.Equal("playing", started.GetProperty("phase").GetString());
        Assert.Equal("waiting", started.GetProperty("playback_state").GetString());
        var wait = Command(Poll(Jordan, id, guest, cursor, out cursor));
        Assert.Equal("waiting", wait.GetProperty("playback_state").GetString());
        Assert.Equal(30, wait.GetProperty("position_seconds").GetDouble());
        var waitId = wait.GetProperty("command_id").GetString();

        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", CommandId = waitId, PositionSeconds = 30, IsPaused = true, IsReady = true });
        Assert.DoesNotContain(Poll(Jordan, id, guest, cursor, out cursor), m => Type(m) == "transport_command");

        _now = _now.AddSeconds(2);
        Send(Jordan, id, guest, new InboundMessage { Type = "state_report", SessionId = "player", CommandId = waitId, PositionSeconds = 30, IsPaused = true, IsReady = true });
        var play = Command(Poll(Jordan, id, guest, cursor, out _));
        Assert.Equal("play", play.GetProperty("action").GetString());
        Assert.True(play.GetProperty("countdown").GetBoolean());
        Assert.Equal(At(3000), play.GetProperty("execute_at").GetString());
        Assert.Equal("playing", play.GetProperty("playback_state").GetString());
    }

    [Fact]
    public void TheReadyWaitGivesUpAfterTenSeconds()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        Join(Jordan, id);
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        Poll(Maya, id, host, 0, out var cursor);

        _now = _now.AddSeconds(9);
        _engine.Sweep();
        Assert.DoesNotContain(Poll(Maya, id, host, cursor, out cursor), m => Type(m) == "transport_command");

        _now = _now.AddSeconds(1);
        _engine.Sweep();
        Assert.True(Command(Poll(Maya, id, host, cursor, out _)).GetProperty("countdown").GetBoolean());
    }

    [Fact]
    public void AnEarlyReadyForAnOlderCommandDoesNotCount()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        Poll(Maya, id, host, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", CommandId = "stale", IsReady = true });

        Assert.DoesNotContain(Poll(Maya, id, host, cursor, out _), m => Type(m) == "transport_command");
    }

    [Fact]
    public void AHostSeekBeforeTheStartMovesWhereEveryoneLoads()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        Poll(Maya, id, host, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "seek", PositionSeconds = 600 });
        var seek = Command(Poll(Maya, id, host, cursor, out cursor));
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", CommandId = seek.GetProperty("command_id").GetString(), IsReady = true });
        var play = Command(Poll(Maya, id, host, cursor, out _));

        Assert.Equal("waiting", seek.GetProperty("playback_state").GetString());
        Assert.Equal(600, play.GetProperty("position_seconds").GetDouble());
    }

    [Fact]
    public void AHostWhoBuffersPausesTheRoomUntilTheirPlayerRecovers()
    {
        var (id, host, guest) = Playing();
        Poll(Jordan, id, guest, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "buffering", PositionSeconds = 40 });
        var hold = Command(Poll(Jordan, id, guest, cursor, out cursor));
        Assert.Equal("pause", hold.GetProperty("action").GetString());
        Assert.Equal("waiting", hold.GetProperty("playback_state").GetString());

        Send(Jordan, id, guest, new InboundMessage { Type = "state_report", SessionId = "player", IsReady = true, PositionSeconds = 40 });
        Assert.DoesNotContain(Poll(Jordan, id, guest, cursor, out cursor), m => Type(m) == "transport_command");

        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", IsReady = true, PositionSeconds = 41 });
        var resume = Command(Poll(Jordan, id, guest, cursor, out _));
        Assert.Equal("play", resume.GetProperty("action").GetString());
        Assert.False(resume.GetProperty("countdown").GetBoolean());
        Assert.Equal(41, resume.GetProperty("position_seconds").GetDouble());
    }

    [Fact]
    public void AReportWithoutASessionOnlySetsTheStatus()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        var guest = Join(Jordan, id);
        Poll(Jordan, id, guest, 0, out var cursor);
        Send(Jordan, id, guest, new InboundMessage { Type = "state_report", PositionSeconds = 0, IsPaused = true, Status = "ready" });
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        var wait = Command(Poll(Jordan, id, guest, cursor, out cursor)).GetProperty("command_id").GetString();
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", CommandId = wait, IsReady = true });

        Send(Jordan, id, guest, new InboundMessage { Type = "state_report", CommandId = wait, PositionSeconds = 0, IsPaused = true, IsReady = true, Status = "joining" });
        var messages = Poll(Jordan, id, guest, cursor, out cursor);
        Assert.DoesNotContain(messages, m => Type(m) == "transport_command");

        _now = _now.AddSeconds(10);
        _engine.Sweep();
        _now = _now.AddSeconds(5);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", PositionSeconds = 999, IsPaused = false, Status = "in_sync" });
        Send(Jordan, id, guest, new InboundMessage { Type = "state_report", PositionSeconds = 0, IsPaused = true, Status = "not_watching" });
        var room = Poll(Jordan, id, guest, cursor, out _).Last(m => Type(m) == "snapshot").GetProperty("room");
        Assert.Equal("not_watching", room.GetProperty("members")[1].GetProperty("status").GetString());
        Assert.Equal(0, room.GetProperty("anchor_position_seconds").GetDouble());
    }

    [Fact]
    public void EveryRoomChangeSendsEachMemberTheirOwnSnapshotAndRepeatsSendNone()
    {
        var (id, host) = Create(Maya);
        var guest = Join(Jordan, id);
        var me = SelfId(Jordan, id, guest);
        Poll(Jordan, id, guest, 0, out var cursor);

        void Expect(string what, Action change, bool snapshot = true)
        {
            change();
            var messages = Poll(Jordan, id, guest, cursor, out cursor);
            var snapshots = messages.Where(m => Type(m) == "snapshot").ToList();
            Assert.True(snapshot == (snapshots.Count == 1), what);
            if (snapshot)
            {
                Assert.Equal(me, snapshots[0].GetProperty("room").GetProperty("self_member_id").GetString());
            }
        }

        var sam = string.Empty;
        Expect("a member joins", () => sam = Join(Sam, id));
        Expect("a status changes", () => Send(Jordan, id, guest, new InboundMessage { Type = "state_report", Status = "not_watching" }));
        Expect("the same status again", () => Send(Jordan, id, guest, new InboundMessage { Type = "state_report", Status = "not_watching" }), snapshot: false);
        Expect("the policy", () => _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "guests_can_pause" }));
        Expect("the same policy again", () => _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "guests_can_pause" }), snapshot: false);
        Expect("the PIN", () => _engine.SetPin(Caller(Maya), id, host, new RoomBody { Pin = "1234" }));
        Expect("voting", () => _engine.Voting(Caller(Maya), id, host, new RoomBody { Voting = true }));
        Expect("the selection", () => _engine.Stage(Caller(Maya), id, host, new RoomBody { ContentId = Movie }));
        Expect("start", () => _engine.Start(Caller(Maya), id, host, new RoomBody()));
        Expect("stop", () => _engine.Stop(Caller(Maya), id, host));
        Expect("the host", () => _engine.Promote(Caller(Maya), id, host, new RoomBody { MemberId = me }));
        Expect("a member leaves", () => _engine.Leave(Caller(Sam), id, sam));
        Expect("a member drops out", () =>
        {
            _now = _now.AddSeconds(36);
            _engine.Sweep();
        });
    }

    [Fact]
    public void ARunEndsBackInTheLobbyWithTheSameItemOnceTheItemFinishes()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Kids });
        var guest = Join(Jordan, id);
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        _now = _now.AddSeconds(10);
        _engine.Sweep();
        var cursor = 0L;
        var generation = Poll(Jordan, id, guest, 0, out cursor)[0].GetProperty("room").GetProperty("generation").GetInt64();

        // Three seconds of countdown plus 95 of play reach two seconds short of the runtime.
        for (var i = 0; i < 98; i++)
        {
            _now = _now.AddSeconds(1);
            Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = Math.Min(99.9, i - 2), IsPaused = false });
            _engine.Sweep();
        }

        Assert.Equal("playing", Poll(Jordan, id, guest, 0, out _)[0].GetProperty("room").GetProperty("phase").GetString());
        for (var i = 0; i < 6; i++)
        {
            _now = _now.AddSeconds(1);
            Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 99.9, IsPaused = false });
            _engine.Sweep();
        }

        var room = Poll(Jordan, id, guest, cursor, out _).Last(m => Type(m) == "snapshot").GetProperty("room");
        Assert.Equal("lobby", room.GetProperty("phase").GetString());
        Assert.Equal("idle", room.GetProperty("playback_state").GetString());
        Assert.Equal(Kids, room.GetProperty("selected_content_id").GetString());
        Assert.Equal(generation + 1, room.GetProperty("generation").GetInt64());
    }

    [Fact]
    public void ARunWithoutAKnownRuntimeOrWhilePausedNeverEndsOnItsOwn()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        _now = _now.AddSeconds(10);
        _engine.Sweep();
        _now = _now.AddHours(5);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 18000, IsPaused = false });
        _engine.Sweep();
        _now = _now.AddMinutes(1);
        _engine.Sweep();
        Assert.Equal("playing", Poll(Maya, id, host, 0, out _)[0].GetProperty("room").GetProperty("phase").GetString());

        var (kidsRoom, kidsHost) = Create(Sam, new CreateBody { ContentId = Kids });
        _engine.Start(Caller(Sam), kidsRoom, kidsHost, new RoomBody());
        _now = _now.AddSeconds(10);
        _engine.Sweep();
        _now = _now.AddSeconds(50);
        Send(Sam, kidsRoom, kidsHost, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 99.5 });
        for (var i = 0; i < 30; i++)
        {
            _now = _now.AddSeconds(1);
            _engine.Sweep();
        }

        Assert.Equal("paused", Poll(Sam, kidsRoom, kidsHost, 0, out _)[0].GetProperty("room").GetProperty("playback_state").GetString());
    }

    [Fact]
    public void AHostReportFromBeforeItsOwnSeekLandedDoesNotPullTheRoomBack()
    {
        var (id, host, guest) = Playing();
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 100, IsPaused = false });
        Assert.Equal(100, Anchor(Jordan, id, guest));

        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "seek", PositionSeconds = 200 });
        _now = _now.AddMilliseconds(600);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 100.6, IsPaused = false });
        Assert.Equal(200, Anchor(Jordan, id, guest));

        _now = _now.AddSeconds(3);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 203.1, IsPaused = false, Status = "catching_up" });
        Assert.Equal(200, Anchor(Jordan, id, guest));
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 203.2, IsPaused = false });
        Assert.Equal(203.2, Anchor(Jordan, id, guest));
    }

    [Fact]
    public void AGuestsPauseKeepsTheGuestsPositionOverTheHostsReports()
    {
        var (id, host, guest) = Playing();
        _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "everyone" });
        Send(Jordan, id, guest, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 575.383 });

        _now = _now.AddSeconds(3);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 576.291, IsPaused = true });

        Assert.Equal(575.383, Anchor(Jordan, id, guest));
    }

    [Fact]
    public void AResumeStartsFromTheRoomsPausedSpotNotTheAskersOwn()
    {
        var (id, host, guest) = Playing();
        _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "everyone" });
        Send(Jordan, id, guest, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 575.383 });
        _now = _now.AddSeconds(2);

        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "play", PositionSeconds = 576.283 });

        var play = Command(Poll(Jordan, id, guest, 1, out _));
        Assert.Equal("play", play.GetProperty("action").GetString());
        Assert.Equal(575.383, play.GetProperty("position_seconds").GetDouble());

        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "seek", PositionSeconds = 600 });
        Assert.Equal(600, Command(Poll(Jordan, id, guest, 1, out _)).GetProperty("position_seconds").GetDouble());
    }

    [Fact]
    public void HostReportsWaitTwoSecondsPastTheCountdownsPlay()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        var wait = Command(Poll(Maya, id, host, 1, out _)).GetProperty("command_id").GetString();
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", CommandId = wait, IsReady = true });

        _now = _now.AddSeconds(3.5);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 25, IsPaused = false });
        Assert.Equal(0, Anchor(Maya, id, host));

        _now = _now.AddSeconds(2);
        Send(Maya, id, host, new InboundMessage { Type = "state_report", SessionId = "player", PositionSeconds = 2.5, IsPaused = false });
        Assert.Equal(2.5, Anchor(Maya, id, host));
    }

    [Fact]
    public void AGuestWhoBuffersOnlyChangesTheirStatus()
    {
        var (id, host, guest) = Playing();
        Poll(Maya, id, host, 0, out var cursor);

        Send(Jordan, id, guest, new InboundMessage { Type = "buffering", PositionSeconds = 40 });

        var messages = Poll(Maya, id, host, cursor, out _);
        Assert.DoesNotContain(messages, m => Type(m) == "transport_command");
        var jordan = messages.Single(m => Type(m) == "snapshot").GetProperty("room").GetProperty("members")[1];
        Assert.Equal("buffering", jordan.GetProperty("status").GetString());
    }

    [Fact]
    public void AttachingASessionShowsInThatMembersSnapshot()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "attach_session", SessionId = "abc" });

        var room = Poll(Maya, id, host, cursor, out _).Single(m => Type(m) == "snapshot").GetProperty("room");
        Assert.Equal("abc", room.GetProperty("attached_session_id").GetString());
    }

    [Fact]
    public void ChatIsCleanedCappedRateLimitedAndKeptForLateJoiners()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "chat", Text = "  hi\u0007 there\n " });
        Send(Maya, id, host, new InboundMessage { Type = "chat", Text = new string('x', 900) });
        var first = Poll(Maya, id, host, cursor, out cursor).Where(m => Type(m) == "chat").Select(m => m.GetProperty("message").GetProperty("text").GetString()).ToList();
        Assert.Equal("hi there", first[0]);
        Assert.Equal(500, first[1]!.Length);

        for (var i = 0; i < 4; i++)
        {
            Send(Maya, id, host, new InboundMessage { Type = "chat", Text = "spam" });
        }

        var burst = Poll(Maya, id, host, cursor, out _);
        Assert.Equal(3, burst.Count(m => Type(m) == "chat"));
        Assert.Equal("rate_limited", burst.Single(m => Type(m) == "error").GetProperty("code").GetString());

        var jordan = Join(Jordan, id);
        var history = Poll(Jordan, id, jordan, 0, out _).Single(m => Type(m) == "chat_history").GetProperty("messages");
        Assert.Equal(5, history.GetArrayLength());
    }

    [Fact]
    public void ChatHistoryKeepsTheLastFifty()
    {
        var (id, host) = Create(Maya);
        for (var i = 0; i < 60; i++)
        {
            _now = _now.AddSeconds(1);
            Send(Maya, id, host, new InboundMessage { Type = "chat", Text = "m" + i });
        }

        var history = Poll(Maya, id, host, 0, out _).Single(m => Type(m) == "chat_history").GetProperty("messages");

        Assert.Equal(WatchPartyEngine.ChatHistory, history.GetArrayLength());
        Assert.Equal("m10", history[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ChatAndReactionsFollowTheAdminSwitches()
    {
        _settings.Chat = false;
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        Send(Maya, id, host, new InboundMessage { Type = "chat", Text = "hi" });
        Send(Maya, id, host, new InboundMessage { Type = "reaction", Reaction = "fire" });
        Send(Maya, id, host, new InboundMessage { Type = "reaction", Reaction = "thumbsup" });

        var messages = Poll(Maya, id, host, cursor, out _);
        Assert.Equal(new[] { "error", "reaction", "error" }, Types(messages));
        Assert.Equal("fire", messages[1].GetProperty("reaction").GetString());
    }

    [Fact]
    public void PingIsAnsweredInThePostNotQueued()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        var reply = Json(_engine.Messages(Caller(Maya), id, host, new MessagesBody { Messages = new() { new InboundMessage { Type = "ping", ClientSentAt = "2026-11-09T20:59:59.900Z" } } }));

        var pong = reply.GetProperty("messages")[0];
        Assert.Equal("pong", Type(pong));
        Assert.Equal("2026-11-09T20:59:59.900Z", pong.GetProperty("client_sent_at").GetString());
        Assert.Equal(At(0), pong.GetProperty("server_received_at").GetString());
        Assert.Empty(Poll(Maya, id, host, cursor, out _));
    }

    [Fact]
    public async Task EventsHoldsUntilSomethingHappens()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        var poll = _engine.Events(Caller(Maya), id, host, cursor, 25);
        await Task.Delay(50);
        Assert.False(poll.IsCompleted);

        Send(Maya, id, host, new InboundMessage { Type = "reaction", Reaction = "love" });
        var done = await Task.WhenAny(poll, Task.Delay(5000));

        Assert.Same(poll, done);
        Assert.Equal("reaction", Type(Json(await poll).GetProperty("messages")[0]));
    }

    [Fact]
    public async Task EventsAnswersEmptyWhenTheWaitRunsOut()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);
        _delay = (_, _) => Task.CompletedTask;

        var answer = Json(await _engine.Events(Caller(Maya), id, host, cursor, 25));

        Assert.Equal(cursor, answer.GetProperty("cursor").GetInt64());
        Assert.Equal(0, answer.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task ACursorOlderThanTheRingGetsAResetWithAFreshSnapshot()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);
        for (var i = 0; i < WatchPartyEngine.RingSize + 5; i++)
        {
            _now = _now.AddSeconds(1);
            Send(Maya, id, host, new InboundMessage { Type = "reaction", Reaction = "wow" });
        }

        var answer = Json(await _engine.Events(Caller(Maya), id, host, cursor, 0));

        Assert.True(answer.GetProperty("reset").GetBoolean());
        Assert.Equal(new[] { "snapshot", "chat_history" }, Types(answer.GetProperty("messages").EnumerateArray().ToList()));
    }

    [Fact]
    public void QuietMembersShowDisconnectedThenLeaveAndTheHostHandsOver()
    {
        var (id, host) = Create(Maya);
        var jordan = Join(Jordan, id);
        _now = _now.AddSeconds(1);
        var sam = Join(Sam, id);
        Poll(Sam, id, sam, 0, out var cursor);

        _now = _now.AddSeconds(36);
        Poll(Sam, id, sam, cursor, out cursor);
        _engine.Sweep();
        var members = Poll(Sam, id, sam, cursor, out cursor).Last(m => Type(m) == "snapshot").GetProperty("room").GetProperty("members");
        Assert.False(members[0].GetProperty("connected").GetBoolean());

        _now = _now.AddSeconds(90);
        Poll(Sam, id, sam, cursor, out cursor);
        _engine.Sweep();
        var messages = Poll(Sam, id, sam, cursor, out _);
        Assert.Contains(messages, m => Type(m) == "notice" && m.GetProperty("kind").GetString() == "host_changed" && m.GetProperty("member_id").GetString() == SelfId(Sam, id, sam));
        Assert.Equal("not_found", _engine.Stop(Caller(Maya), id, host).Code);
        Assert.Equal("not_found", _engine.Stop(Caller(Jordan), id, jordan).Code);
    }

    [Fact]
    public async Task ARoomWithNobodyLeftCloses()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        Assert.Equal(204, _engine.Leave(Caller(Maya), id, host).Status);

        var last = Json(await _engine.Events(Caller(Maya), id, host, cursor, 0)).GetProperty("messages");
        Assert.Contains(last.EnumerateArray(), m => Type(m) == "room_closed" && m.GetProperty("reason").GetString() == "empty");
        Assert.Empty(Json(_engine.Rooms(Caller(Sam))).GetProperty("rooms").EnumerateArray());
    }

    [Fact]
    public async Task ARemovedMemberHearsWhyThenTheRoomIsGoneForThem()
    {
        var (id, host) = Create(Maya);
        var jordan = Join(Jordan, id);
        Poll(Jordan, id, jordan, 0, out var cursor);

        Assert.Equal(204, _engine.Remove(Caller(Maya), id, host, new RoomBody { MemberId = SelfId(Jordan, id, jordan) }).Status);

        var answer = await _engine.Events(Caller(Jordan), id, jordan, cursor, 25);
        var removed = Json(answer).GetProperty("messages").EnumerateArray().Single(m => Type(m) == "removed");
        Assert.Equal("removed_by_host", removed.GetProperty("reason").GetString());
        var after = await _engine.Events(Caller(Jordan), id, jordan, Json(answer).GetProperty("cursor").GetInt64(), 25);
        Assert.Equal("not_found", after.Code);
    }

    [Fact]
    public async Task AMemberRemovedDuringAPollStillReadsRemovedBeforeNotFound()
    {
        var (id, host) = Create(Maya);
        var jordan = Join(Jordan, id);
        var me = SelfId(Jordan, id, jordan);
        Poll(Jordan, id, jordan, 0, out var cursor);
        Poll(Jordan, id, jordan, cursor, out cursor);

        var inFlight = _engine.Events(Caller(Jordan), id, jordan, cursor, 5);
        await Task.Delay(50);
        Assert.Equal(204, _engine.Remove(Caller(Maya), id, host, new RoomBody { MemberId = me }).Status);
        var answer = Json(await inFlight);
        var seen = answer.GetProperty("messages").EnumerateArray().Select(Type).ToList();
        cursor = answer.GetProperty("cursor").GetInt64();
        while (!seen.Contains("removed"))
        {
            var next = await _engine.Events(Caller(Jordan), id, jordan, cursor, 5);
            Assert.Equal(200, next.Status);
            seen.AddRange(Json(next).GetProperty("messages").EnumerateArray().Select(Type));
            cursor = Json(next).GetProperty("cursor").GetInt64();
        }

        Assert.Equal("not_found", (await _engine.Events(Caller(Jordan), id, jordan, cursor, 5)).Code);
    }

    [Fact]
    public async Task TheLastWordIsReadableUntilTheCursorIsPastItEvenAfterAResetOrAFreshStart()
    {
        var (id, host) = Create(Maya);
        var jordan = Join(Jordan, id);
        var me = SelfId(Jordan, id, jordan);
        _engine.Remove(Caller(Maya), id, host, new RoomBody { MemberId = me });

        var fresh = Json(await _engine.Events(Caller(Jordan), id, jordan, 0, 5));
        Assert.Equal(new[] { "removed" }, Types(fresh.GetProperty("messages").EnumerateArray().ToList()));
        Assert.Equal("not_found", (await _engine.Events(Caller(Jordan), id, jordan, fresh.GetProperty("cursor").GetInt64(), 5)).Code);

        Poll(Maya, id, host, 0, out var cursor);
        for (var i = 0; i < WatchPartyEngine.RingSize + 5; i++)
        {
            _now = _now.AddSeconds(1);
            Send(Maya, id, host, new InboundMessage { Type = "reaction", Reaction = "wow" });
        }

        _engine.Close(Caller(Maya), id, host);
        var reset = Json(await _engine.Events(Caller(Maya), id, host, cursor, 5));
        var closed = reset.GetProperty("messages").EnumerateArray().Single(m => Type(m) == "room_closed");
        Assert.Equal("ended_by_host", closed.GetProperty("reason").GetString());
        Assert.Equal("not_found", (await _engine.Events(Caller(Maya), id, host, reset.GetProperty("cursor").GetInt64(), 5)).Code);
    }

    [Fact]
    public void GenerationOnlyMovesOnStartStopAndANewSelection()
    {
        var (id, host) = Create(Maya);
        var guest = Join(Jordan, id);
        long Generation() => Poll(Maya, id, host, 0, out _)[0].GetProperty("room").GetProperty("generation").GetInt64();
        var start = Generation();

        _engine.Policy(Caller(Maya), id, host, new RoomBody { GuestControlPolicy = "everyone" });
        _engine.SetPin(Caller(Maya), id, host, new RoomBody { Pin = "1234" });
        Send(Jordan, id, guest, new InboundMessage { Type = "chat", Text = "hi" }, new InboundMessage { Type = "state_report", Status = "not_watching" });
        Assert.Equal(start, Generation());

        _engine.Stage(Caller(Maya), id, host, new RoomBody { ContentId = Movie });
        Assert.Equal(start + 1, Generation());
        _engine.Stage(Caller(Maya), id, host, new RoomBody { ContentId = Movie });
        Assert.Equal(start + 1, Generation());

        var started = Json(_engine.Start(Caller(Maya), id, host, new RoomBody())).GetProperty("room").GetProperty("generation").GetInt64();
        Assert.Equal(start + 2, started);
        _now = _now.AddSeconds(11);
        _engine.Sweep();
        _now = _now.AddSeconds(5);
        Send(Maya, id, host, new InboundMessage { Type = "transport_request", Action = "pause", PositionSeconds = 20 });
        Send(Maya, id, host, new InboundMessage { Type = "buffering", PositionSeconds = 20 });
        Assert.Equal(started, Generation());
        Assert.Equal(started, Command(Poll(Jordan, id, guest, 1, out _)).GetProperty("generation").GetInt64());

        _engine.Stop(Caller(Maya), id, host);
        Assert.Equal(started + 1, Generation());
    }

    [Fact]
    public void CreatingClosesTheOtherRoomTheUserHostsAndARetryGetsTheSameRoom()
    {
        var (first, firstToken) = Create(Maya);
        var roomId = Guid.NewGuid().ToString("D");

        var (second, token) = Create(Maya, new CreateBody { RoomId = roomId });
        var (retry, retryToken) = Create(Maya, new CreateBody { RoomId = roomId });

        Assert.Equal(roomId, second);
        Assert.Equal(second, retry);
        Assert.Equal(token, retryToken);
        Assert.Equal("not_found", _engine.Stop(Caller(Maya), first, firstToken).Code);
        Assert.Equal("bad_request", _engine.Create(Caller(Jordan), new CreateBody { RoomId = roomId }).Code);
    }

    [Fact]
    public void AlertsGoOutOnTheFirstItemAtMostEveryTenMinutesPerHost()
    {
        var (id, token) = Create(Maya);
        Assert.Empty(_host.Announced);

        _engine.Stage(Caller(Maya), id, token, new RoomBody { ContentId = Kids });
        _engine.Stage(Caller(Maya), id, token, new RoomBody { ContentId = Movie });
        var alert = Assert.Single(_host.Announced);
        Assert.Equal("maya started a watch party", alert.Title);
        Assert.Equal("Bluey · S2:E3 · 1 in the lobby", alert.Body);
        Assert.Equal("/watch-party?code=" + Code(id), alert.Route);
        Assert.Contains(Maya, alert.MemberUserIds);

        _now = _now.AddMinutes(5);
        Create(Maya, new CreateBody { ContentId = Movie });
        Assert.Single(_host.Announced);

        _now = _now.AddMinutes(6);
        Create(Maya, new CreateBody { ContentId = Movie });
        Assert.Equal(2, _host.Announced.Count);

        _settings.Alerts = false;
        Create(Sam, new CreateBody { ContentId = Movie });
        Assert.Equal(2, _host.Announced.Count);
    }

    [Fact]
    public void VotingKeepsSuggestionsInVoteOrderAndStagingEndsIt()
    {
        var (id, host) = Create(Maya);
        var jordan = Join(Jordan, id);
        _engine.Voting(Caller(Maya), id, host, new RoomBody { Voting = true });

        Assert.Equal(204, _engine.Suggest(Caller(Maya), id, host, new RoomBody { ContentId = Movie }).Status);
        _now = _now.AddSeconds(1);
        _engine.Suggest(Caller(Maya), id, host, new RoomBody { ContentId = Kids });
        Assert.Equal("not_host", _engine.Suggest(Caller(Jordan), id, jordan, new RoomBody { ContentId = Kids }).Code);

        var kids = Suggestions(Jordan, id, jordan)[1];
        Assert.Equal("Bluey · S2:E3", kids.GetProperty("subtitle").GetString());
        _engine.Vote(Caller(Jordan), id, jordan, kids.GetProperty("id").GetString()!, new RoomBody { Voted = true });

        var mine = Suggestions(Jordan, id, jordan);
        Assert.Equal(Kids, mine[0].GetProperty("content_id").GetString());
        Assert.True(mine[0].GetProperty("voted_by_me").GetBoolean());
        Assert.False(Suggestions(Maya, id, host)[0].GetProperty("voted_by_me").GetBoolean());

        var room = Json(_engine.Stage(Caller(Maya), id, host, new RoomBody { ContentId = Kids })).GetProperty("room");
        Assert.Equal("host_pick", room.GetProperty("selection_mode").GetString());
        Assert.Empty(Suggestions(Maya, id, host));
    }

    [Fact]
    public void OnlyTheHostRunsTheRoom()
    {
        var (id, _) = Create(Maya, new CreateBody { ContentId = Movie });
        var jordan = Join(Jordan, id);

        Assert.Equal("not_host", _engine.Start(Caller(Jordan), id, jordan, new RoomBody()).Code);
        Assert.Equal("not_host", _engine.Close(Caller(Jordan), id, jordan).Code);
    }

    [Fact]
    public void PromoteHandsTheRoomToAnotherMember()
    {
        var (id, host) = Create(Maya);
        var jordan = Join(Jordan, id);

        var room = Json(_engine.Promote(Caller(Maya), id, host, new RoomBody { MemberId = SelfId(Jordan, id, jordan) })).GetProperty("room");

        Assert.Equal("guest", room.GetProperty("self_role").GetString());
        Assert.Equal(200, _engine.Stop(Caller(Jordan), id, jordan).Status);
    }

    [Fact]
    public void AdminsListEveryRoomAndCanCloseOrRemove()
    {
        var (id, _) = Create(Maya, new CreateBody { ContentId = Movie });
        var sam = Join(Sam, id);

        var rooms = Json(_engine.AdminRooms()).GetProperty("rooms");
        Assert.Equal(2, rooms[0].GetProperty("members").GetArrayLength());
        Assert.Equal(Sam, rooms[0].GetProperty("members")[1].GetProperty("user_id").GetString());

        Assert.Equal(204, _engine.AdminRemove(id, new RoomBody { MemberId = SelfId(Sam, id, sam) }).Status);
        Assert.Equal(204, _engine.AdminClose(id).Status);
        Assert.Empty(Json(_engine.AdminRooms()).GetProperty("rooms").EnumerateArray());
    }

    [Fact]
    public async Task RoutesAnswerDisabledExceptConfigWhenTurnedOff()
    {
        _settings.Enabled = false;

        var config = await Route("GET", "Config");
        var rooms = await Route("GET", "Rooms");

        Assert.False(Json(config).GetProperty("enabled").GetBoolean());
        Assert.Equal(1, Json(config).GetProperty("version").GetInt32());
        Assert.Equal("disabled", rooms.Code);
        Assert.Equal(403, rooms.Status);
    }

    [Fact]
    public async Task TurningWatchPartiesOffClosesTheOpenRooms()
    {
        var (id, host) = Create(Maya);
        Poll(Maya, id, host, 0, out var cursor);

        _settings.Enabled = false;
        _engine.Sweep();

        var messages = Json(await _engine.Events(Caller(Maya), id, host, cursor, 0)).GetProperty("messages");
        Assert.Contains(messages.EnumerateArray(), m => Type(m) == "room_closed" && m.GetProperty("reason").GetString() == "disabled");
    }

    [Fact]
    public async Task RoutesParseBodiesAndPaths()
    {
        var created = await Route("POST", "Rooms", body: "{\"content_id\":\"movie1\"}");
        var id = Json(created).GetProperty("room").GetProperty("room_id").GetString()!;
        var token = Json(created).GetProperty("token").GetString();

        Assert.Equal(200, created.Status);
        Assert.Equal("bad_request", (await Route("POST", "Join", body: "{nope")).Code);
        Assert.Equal("not_found", (await Route("GET", "Nope")).Code);
        Assert.Equal(200, (await Route("POST", $"rooms/{id}/start", token, "{\"position_seconds\":12.5}")).Status);
        var events = await Route("GET", $"Rooms/{id}/Events", token, query: new() { ["cursor"] = "0", ["wait"] = "25" });
        Assert.Equal("snapshot", Type(Json(events).GetProperty("messages")[0]));
        Assert.Equal(204, (await Route("POST", $"Rooms/{id}/Close", token)).Status);
    }

    [Fact]
    public async Task BodiesOverTheCapReadAsBrokenJson()
    {
        var small = await WatchPartyRoutes.ReadBodyAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"pin\":\"1234\"}")));
        var big = await WatchPartyRoutes.ReadBodyAsync(new MemoryStream(new byte[(64 * 1024) + 1]));

        Assert.Equal("{\"pin\":\"1234\"}", Encoding.UTF8.GetString(small));
        Assert.Equal("bad_request", (await Route("POST", "Join", body: Encoding.UTF8.GetString(big))).Code);
    }

    [Fact]
    public void CodesNormalizeAndRejectLettersOutsideTheAlphabet()
    {
        Assert.Equal("K7Q29M", WatchPartyEngine.NormalizeCode("k7q 29m"));
        Assert.Equal("K7Q29M", WatchPartyEngine.NormalizeCode("K7Q-29M"));
        Assert.Null(WatchPartyEngine.NormalizeCode("K7Q29"));
        Assert.Null(WatchPartyEngine.NormalizeCode("K0Q29M"));
        Assert.Null(WatchPartyEngine.NormalizeCode("<b>hi"));
    }

    [Fact]
    public void TheInvitePageCarriesTheLinkPreviewAndDeepLinkAndEscapesTheServerName()
    {
        var baseUrl = WatchPartyInvite.PublicBase("http", "10.0.0.2:8096", "https", "media.example.com, proxy", "/jellyfin/");
        var page = WatchPartyInvite.Page("Lake <side>", "abc123", baseUrl, "K7Q29M");

        Assert.Equal("https://media.example.com/jellyfin", baseUrl);
        Assert.Contains("<meta property=\"og:title\" content=\"Join a watch party\">", page);
        Assert.Contains("content=\"Open in Moonfin to join. Room code K7Q 29M.\"", page);
        Assert.Contains("content=\"https://media.example.com/jellyfin/Moonfin/Party/preview.png\"", page);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary_large_image\">", page);
        Assert.Contains("moonfin://party?server=abc123&amp;code=K7Q29M&amp;address=https%3A%2F%2Fmedia.example.com%2Fjellyfin", page);
        Assert.Contains("Lake &lt;side&gt;", page);
        Assert.DoesNotContain("Lake <side>", page);
        Assert.Equal("http://h:1", WatchPartyInvite.PublicBase("http", "h:1", "javascript", null, null));
    }

    [Fact]
    public void InviteUrlsUseTheAddressEachMemberCallsFrom()
    {
        var created = Json(_engine.Create(new WatchPartyCaller(Maya, "d1", "https://media.example.com"), new CreateBody()));
        var id = created.GetProperty("room").GetProperty("room_id").GetString()!;
        var code = created.GetProperty("room").GetProperty("code").GetString();
        Assert.Equal("https://media.example.com/Moonfin/Party/" + code, created.GetProperty("room").GetProperty("invite_url").GetString());

        var joined = Json(_engine.Join(new WatchPartyCaller(Jordan, "d1", "http://10.0.0.2:8096"), new JoinBody { RoomId = id }));
        Assert.Equal("http://10.0.0.2:8096/Moonfin/Party/" + code, joined.GetProperty("room").GetProperty("invite_url").GetString());
        Assert.Equal("/Moonfin/Party/" + code, joined.GetProperty("room").GetProperty("invite_path").GetString());

        var noBase = Json(_engine.Create(Caller(Sam), new CreateBody())).GetProperty("room");
        Assert.Equal(JsonValueKind.Null, noBase.GetProperty("invite_url").ValueKind);
    }

    [Theory]
    [InlineData("https://jf.example.com/", "http://127.0.0.1:8096", "https://jf.example.com")]
    [InlineData("https://example.com/jellyfin/", "http://127.0.0.1:8096", "https://example.com/jellyfin")]
    [InlineData("", "http://127.0.0.1:8096", "http://127.0.0.1:8096")]
    [InlineData("jf.example.com", "http://127.0.0.1:8096", "http://127.0.0.1:8096")]
    [InlineData(null, "http://10.0.2.2:8101", "http://10.0.2.2:8101")]
    public void TheConfiguredPublicAddressWinsOverTheRequestWhenItIsUsable(string? configured, string request, string expected)
    {
        Assert.Equal(expected, WatchPartyInvite.ChooseBase(configured, request));
    }

    [Fact]
    public void ThePublishedServerUrlPrefersTheOneForEveryone()
    {
        Assert.Equal("https://jf.example.com", WatchPartyInvite.Published(new[] { "192.168.1.0/24=http://192.168.1.5:8096", "all=https://jf.example.com" }));
        Assert.Equal("https://out.example.com", WatchPartyInvite.Published(new[] { "internal=http://lan:8096", "external=https://out.example.com" }));
        Assert.Equal("http://lan:8096", WatchPartyInvite.Published(new[] { "internal=http://lan:8096" }));
        Assert.Null(WatchPartyInvite.Published(new[] { "broken" }));
        Assert.Null(WatchPartyInvite.Published(null));
    }

    [Fact]
    public void ThePreviewImageIsEmbedded()
    {
        var png = WatchPartyInvite.PreviewPng();

        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png!.Take(4).ToArray());
    }

    private (string Id, string Token, string Guest) Playing()
    {
        var (id, host) = Create(Maya, new CreateBody { ContentId = Movie });
        var guest = Join(Jordan, id);
        _engine.Start(Caller(Maya), id, host, new RoomBody());
        _now = _now.AddSeconds(11);
        _engine.Sweep();
        _now = _now.AddSeconds(5);
        return (id, host, guest);
    }

    private Task<WatchPartyResult> Route(string method, string path, string? token = null, string? body = null, Dictionary<string, string>? query = null) =>
        WatchPartyRoutes.HandleAsync(_engine, method, path, Caller(Maya), token, body == null ? null : Encoding.UTF8.GetBytes(body), k => query != null && query.TryGetValue(k, out var v) ? v : null);

    private (string Id, string Token) Create(string user, CreateBody? body = null)
    {
        var json = Json(_engine.Create(Caller(user), body ?? new CreateBody()));
        return (json.GetProperty("room").GetProperty("room_id").GetString()!, json.GetProperty("token").GetString()!);
    }

    private string Join(string user, string id) =>
        Json(_engine.Join(Caller(user), new JoinBody { RoomId = id })).GetProperty("token").GetString()!;

    private string Code(string id) =>
        Json(_engine.AdminRooms()).GetProperty("rooms").EnumerateArray().Single(r => r.GetProperty("room_id").GetString() == id).GetProperty("code").GetString()!;

    private string SelfId(string user, string id, string token) =>
        Poll(user, id, token, 0, out _)[0].GetProperty("room").GetProperty("self_member_id").GetString()!;

    private List<JsonElement> Suggestions(string user, string id, string token) =>
        Json(_engine.Suggestions(Caller(user), id, token)).GetProperty("suggestions").EnumerateArray().ToList();

    private void Send(string user, string id, string token, params InboundMessage[] messages) =>
        Assert.Equal(200, _engine.Messages(Caller(user), id, token, new MessagesBody { Messages = messages.ToList() }).Status);

    private List<JsonElement> Poll(string user, string id, string token, long cursor, out long next)
    {
        var json = Json(_engine.Events(Caller(user), id, token, cursor, 0).Result);
        next = json.GetProperty("cursor").GetInt64();
        return json.GetProperty("messages").EnumerateArray().ToList();
    }

    private double Anchor(string user, string id, string token) =>
        Poll(user, id, token, 0, out _)[0].GetProperty("room").GetProperty("anchor_position_seconds").GetDouble();

    private static JsonElement Command(List<JsonElement> messages) =>
        messages.Last(m => Type(m) == "transport_command").GetProperty("command");

    private string At(int milliseconds) => WatchPartyJson.Time(_now.AddMilliseconds(milliseconds));

    private static WatchPartyCaller Caller(string user, string device = "d1") => new(user, device);

    private static JsonElement Json(WatchPartyResult result) => JsonDocument.Parse(result.Json()).RootElement;

    private static string? Type(JsonElement message) => message.GetProperty("type").GetString();

    private static string?[] Types(List<JsonElement> messages) => messages.Select(Type).ToArray();

    private sealed class FakeHost : IWatchPartyHost
    {
        public Dictionary<string, WatchPartyItem> Items { get; } = new();

        public HashSet<(string User, string Item)> Blocked { get; } = new();

        public List<WatchPartyAnnouncement> Announced { get; } = new();

        public WatchPartyItem? FindItem(string userId, string contentId) =>
            Items.TryGetValue(contentId, out var item) && !Blocked.Contains((userId, contentId)) ? item : null;

        public WatchPartyProfile Profile(string userId) => new() { DisplayName = userId, AvatarUrl = "/Users/" + userId + "/Images/Primary" };

        public void Announce(WatchPartyAnnouncement announcement) => Announced.Add(announcement);
    }
}
