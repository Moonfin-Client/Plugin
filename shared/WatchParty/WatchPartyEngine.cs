using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.WatchParty;

/// <summary>
/// Watch party rooms, kept in memory only. Clients post their messages and long-poll Events for
/// the room's. One lock covers every room, which is plenty for 200 rooms.
/// </summary>
public sealed class WatchPartyEngine : IDisposable
{
    public const int MaxRooms = 200;
    public const int RingSize = 200;
    public const int ChatHistory = 50;
    public const int ChatMaxLength = 500;
    public const int MaxWaitSeconds = 25;
    public const int MaxPinFailures = 5;
    public const int MaxSuggestions = 50;
    public const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static readonly TimeSpan DisconnectAfter = TimeSpan.FromSeconds(35);
    public static readonly TimeSpan RemoveAfter = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PinWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AnnounceEvery = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A run ends once the room's position has stayed this close to the item's runtime for
    /// <see cref="EndGrace"/>. Players sitting on the last frame keep reporting a position just
    /// short of the runtime, and metadata runtimes are often a little longer than the stream.
    /// </summary>
    public const double EndSlackSeconds = 2;

    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a command's execute_at the host's reports are left out of the anchor. A report
    /// sent before the host's own player has landed the command would pull the room back.
    /// </summary>
    public static readonly TimeSpan HostSettle = TimeSpan.FromSeconds(2);

    // A closed room or a removed member stays this long, so a poll in flight still reads why.
    private static readonly TimeSpan Linger = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> Reactions = new HashSet<string>(StringComparer.Ordinal) { "love", "haha", "wow", "sad", "fire", "popcorn" };
    private static readonly HashSet<string> Statuses = new HashSet<string>(StringComparer.Ordinal) { "joining", "ready", "in_sync", "catching_up", "buffering", "not_watching" };
    private static readonly HashSet<string> Policies = new HashSet<string>(StringComparer.Ordinal) { "host_only", "guests_can_pause", "everyone" };

    private readonly object _gate = new object();
    private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastAnnounced = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private readonly List<WatchPartyAnnouncement> _announce = new List<WatchPartyAnnouncement>();
    private readonly IWatchPartyHost _host;
    private readonly Func<WatchPartySettings> _settings;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Timer? _timer;

    public WatchPartyEngine(
        IWatchPartyHost host,
        Func<WatchPartySettings> settings,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _settings = settings;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public WatchPartySettings Settings => _settings();

    public void StartTimer() => _timer ??= new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public void Dispose() => _timer?.Dispose();

    public WatchPartyResult Config()
    {
        var s = Settings;
        return WatchPartyResult.Ok(new Dictionary<string, object?>
        {
            ["enabled"] = s.Enabled,
            ["chat"] = s.Chat,
            ["reactions"] = s.Reactions,
            ["alerts"] = s.Alerts,
            ["everyone_control_allowed"] = s.EveryoneControl,
            ["max_members"] = MaxMembersOf(s),
            ["version"] = 1,
        });
    }

    public WatchPartyResult Time() =>
        WatchPartyResult.Ok(new Dictionary<string, object?> { ["server_time"] = WatchPartyJson.Time(_now()) });

    public WatchPartyResult Rooms(WatchPartyCaller caller) => Locked(() =>
    {
        var canPlay = new Dictionary<string, bool>(StringComparer.Ordinal);
        var now = _now();
        var list = new List<SummaryDto>();
        foreach (var room in OpenRooms().OrderByDescending(r => r.CreatedAt))
        {
            if (room.ContentId != null)
            {
                if (!canPlay.TryGetValue(room.ContentId, out var ok))
                {
                    ok = _host.FindItem(caller.UserId, room.ContentId) != null;
                    canPlay[room.ContentId] = ok;
                }

                if (!ok)
                {
                    continue;
                }
            }

            list.Add(Summary(room, caller, now));
        }

        return WatchPartyResult.Ok(new Dictionary<string, object?> { ["rooms"] = list });
    });

    public WatchPartyResult Create(WatchPartyCaller caller, CreateBody body) => Locked(() =>
    {
        var now = _now();
        string? roomId = null;
        if (!string.IsNullOrEmpty(body.RoomId))
        {
            if (!Guid.TryParse(body.RoomId, out var parsed))
            {
                return BadRequest("room_id has to be a UUID.");
            }

            roomId = parsed.ToString("D");
        }

        if (body.Pin != null && !IsPin(body.Pin))
        {
            return BadRequest("A PIN is 4 to 8 digits.");
        }

        var policy = body.GuestControlPolicy ?? "host_only";
        var refused = CheckPolicy(policy);
        if (refused != null)
        {
            return refused;
        }

        if (roomId != null && _rooms.TryGetValue(roomId, out var existing) && existing.ClosedAt == null)
        {
            var mine = existing.Active.FirstOrDefault(m => IsCaller(m, caller));
            if (mine == null || existing.Host?.UserId != caller.UserId)
            {
                return BadRequest("That room id is taken.");
            }

            mine.PublicBase = caller.PublicBase ?? mine.PublicBase;
            return Access(existing, mine);
        }

        WatchPartyItem? item = null;
        if (!string.IsNullOrEmpty(body.ContentId))
        {
            item = _host.FindItem(caller.UserId, body.ContentId!);
            if (item == null)
            {
                return NoAccess();
            }
        }

        foreach (var other in OpenRooms().Where(r => r.Host?.UserId == caller.UserId).ToList())
        {
            Close(other, "replaced", now);
        }

        if (OpenRooms().Count() >= MaxRooms)
        {
            return WatchPartyResult.Error("full", "This server has too many watch parties right now.");
        }

        var room = new Room(roomId ?? Guid.NewGuid().ToString("D"), NewCode(), now) { Policy = policy };
        var host = NewMember(caller, now);
        host.Status = "ready";
        room.Members.Add(host);
        room.HostId = host.Id;
        if (body.Pin != null)
        {
            room.Pin = PinHash.Create(body.Pin);
        }

        _rooms[room.Id] = room;
        if (item != null)
        {
            Select(room, item, now);
        }

        Changed(room);
        return Access(room, host);
    });

    public WatchPartyResult Join(WatchPartyCaller caller, JoinBody body) => Locked(() =>
    {
        var now = _now();
        Room? room = null;
        if (!string.IsNullOrEmpty(body.RoomId))
        {
            _rooms.TryGetValue(body.RoomId!, out room);
        }
        else if (NormalizeCode(body.Code) is { } code)
        {
            room = OpenRooms().FirstOrDefault(r => r.Code == code);
        }
        else if (string.IsNullOrEmpty(body.Code))
        {
            return BadRequest("Send a code or a room_id.");
        }

        if (room == null || room.ClosedAt != null)
        {
            return WatchPartyResult.Error("not_found", "No watch party has that code.");
        }

        var mine = room.Active.FirstOrDefault(m => IsCaller(m, caller));
        if (mine != null)
        {
            mine.PublicBase = caller.PublicBase ?? mine.PublicBase;
            Seen(room, mine, now);
            return Access(room, mine);
        }

        if (room.Pin != null)
        {
            if (!room.PinFailures.TryGetValue(caller.UserId, out var failures))
            {
                failures = new List<DateTime>();
                room.PinFailures[caller.UserId] = failures;
            }

            failures.RemoveAll(t => now - t >= PinWindow);
            if (failures.Count >= MaxPinFailures)
            {
                var wait = (int)Math.Ceiling((PinWindow - (now - failures[0])).TotalSeconds);
                return WatchPartyResult.Error("too_many_attempts", "Too many tries. Wait a minute.", Math.Max(1, wait));
            }

            if (string.IsNullOrEmpty(body.Pin))
            {
                return WatchPartyResult.Error("pin_required", "This watch party needs a PIN.");
            }

            if (!room.Pin.Matches(body.Pin!))
            {
                failures.Add(now);
                return WatchPartyResult.Error("wrong_pin", "That PIN didn't work.");
            }
        }

        if (room.Active.Count() >= MaxMembersOf(Settings))
        {
            return WatchPartyResult.Error("full", "This party is full.");
        }

        if (room.ContentId != null && _host.FindItem(caller.UserId, room.ContentId) == null)
        {
            return NoAccess();
        }

        var member = NewMember(caller, now);
        room.Members.Add(member);
        Append(room, Notice("joined", member));
        Changed(room);
        return Access(room, member);
    });

    public WatchPartyResult Stage(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        if (string.IsNullOrEmpty(body.ContentId))
        {
            return BadRequest("Send a content_id.");
        }

        var item = _host.FindItem(caller.UserId, body.ContentId!);
        if (item == null)
        {
            return NoAccess();
        }

        var switched = room.Phase == "playing" && item.Id != room.ContentId;
        Select(room, item, now);
        if (switched)
        {
            Append(room, Notice("switched", host, item.Label));
        }

        Changed(room);
        return RoomOf(room, host);
    });

    public WatchPartyResult Start(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        if (room.ContentId == null)
        {
            return BadRequest("Pick something to watch first.");
        }

        var position = Math.Max(0, body.PositionSeconds ?? 0);
        room.Phase = "playing";
        room.SelectionRevision++;
        room.Generation++;
        foreach (var m in room.Active.Where(m => !m.NoAccess))
        {
            m.Status = "joining";
        }

        BeginWait(room, host, position, hostOnly: false, now);
        return RoomOf(room, host);
    });

    public WatchPartyResult Stop(WatchPartyCaller caller, string roomId, string? token) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        BackToLobby(room, now);
        return RoomOf(room, host);
    });

    /// <summary>
    /// Ends a run, from the host's Stop or when the item finishes. The room goes back to the lobby
    /// with the same item staged, which clients that didn't stop it read as finished.
    /// </summary>
    private static void BackToLobby(Room room, DateTime now)
    {
        room.Phase = "lobby";
        room.PlaybackState = "idle";
        room.IsPaused = true;
        room.SelectionRevision++;
        room.Generation++;
        room.Anchor = 0;
        room.AnchorAt = now;
        room.EndingSince = null;
        room.EndWait();
        foreach (var m in room.Active.Where(m => !m.NoAccess))
        {
            m.Status = "ready";
        }

        Changed(room);
    }

    public WatchPartyResult Policy(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        var policy = body.GuestControlPolicy ?? string.Empty;
        var refused = CheckPolicy(policy);
        if (refused != null)
        {
            return refused;
        }

        if (room.Policy != policy)
        {
            room.Policy = policy;
            Append(room, Notice("policy_changed", host));
            Changed(room);
        }

        return RoomOf(room, host);
    });

    public WatchPartyResult SetPin(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        if (body.Pin != null && !IsPin(body.Pin))
        {
            return BadRequest("A PIN is 4 to 8 digits.");
        }

        room.Pin = body.Pin == null ? null : PinHash.Create(body.Pin);
        room.PinFailures.Clear();
        Changed(room);
        return RoomOf(room, host);
    });

    public WatchPartyResult Voting(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        if (body.Voting == null)
        {
            return BadRequest("Send voting as true or false.");
        }

        if (room.Phase != "lobby")
        {
            return NotAllowed("Voting only works in the lobby.");
        }

        var mode = body.Voting.Value ? "vote" : "host_pick";
        if (room.SelectionMode != mode)
        {
            room.SelectionMode = mode;
            if (body.Voting.Value)
            {
                room.Generation += room.ContentId == null ? 0 : 1;
                room.ContentId = null;
                room.Item = null;
                foreach (var m in room.Active.Where(m => m.NoAccess))
                {
                    m.NoAccess = false;
                    m.Status = "ready";
                }
            }
            else
            {
                ClearSuggestions(room);
            }

            Changed(room);
        }

        return RoomOf(room, host);
    });

    public WatchPartyResult Suggestions(WatchPartyCaller caller, string roomId, string? token) => AsMember(caller, roomId, token, (room, me, now) =>
        WatchPartyResult.Ok(new Dictionary<string, object?> { ["suggestions"] = SuggestionList(room, me) }));

    public WatchPartyResult Suggest(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        if (room.SelectionMode != "vote")
        {
            return NotAllowed("Turn on voting first.");
        }

        if (string.IsNullOrEmpty(body.ContentId))
        {
            return BadRequest("Send a content_id.");
        }

        var item = _host.FindItem(caller.UserId, body.ContentId!);
        if (item == null)
        {
            return NoAccess();
        }

        if (room.Suggestions.Any(s => s.ContentId == item.Id))
        {
            return WatchPartyResult.NoContent();
        }

        if (room.Suggestions.Count >= MaxSuggestions)
        {
            return BadRequest("This room has as many suggestions as it can hold.");
        }

        room.Suggestions.Add(new Suggestion(item, now));
        SuggestionsChanged(room);
        return WatchPartyResult.NoContent();
    });

    public WatchPartyResult Vote(WatchPartyCaller caller, string roomId, string? token, string suggestionId, RoomBody body) => AsMember(caller, roomId, token, (room, me, now) =>
    {
        var suggestion = room.Suggestions.FirstOrDefault(s => s.Id == suggestionId);
        if (suggestion == null)
        {
            return WatchPartyResult.Error("not_found", "That suggestion is gone.");
        }

        var changed = body.Voted == false ? suggestion.Votes.Remove(me.Id) : suggestion.Votes.Add(me.Id);
        if (changed)
        {
            SuggestionsChanged(room);
        }

        return WatchPartyResult.NoContent();
    });

    public WatchPartyResult RemoveSuggestion(WatchPartyCaller caller, string roomId, string? token, string suggestionId) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        if (room.Suggestions.RemoveAll(s => s.Id == suggestionId) > 0)
        {
            SuggestionsChanged(room);
        }

        return WatchPartyResult.NoContent();
    });

    public WatchPartyResult Promote(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        var target = room.Active.FirstOrDefault(m => m.Id == body.MemberId);
        if (target == null)
        {
            return WatchPartyResult.Error("not_found", "That person isn't in the party.");
        }

        if (target != host)
        {
            room.HostId = target.Id;
            Append(room, Notice("host_changed", target));
            Changed(room);
        }

        return RoomOf(room, host);
    });

    public WatchPartyResult Remove(WatchPartyCaller caller, string roomId, string? token, RoomBody body) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        var target = room.Active.FirstOrDefault(m => m.Id == body.MemberId);
        if (target == null)
        {
            return WatchPartyResult.Error("not_found", "That person isn't in the party.");
        }

        if (target == host)
        {
            return BadRequest("Leave the party instead.");
        }

        Depart(room, target, "removed_by_host", now);
        return WatchPartyResult.NoContent();
    });

    public WatchPartyResult Close(WatchPartyCaller caller, string roomId, string? token) => AsHost(caller, roomId, token, (room, host, now) =>
    {
        Close(room, "ended_by_host", now);
        return WatchPartyResult.NoContent();
    });

    public WatchPartyResult Leave(WatchPartyCaller caller, string roomId, string? token) => AsMember(caller, roomId, token, (room, me, now) =>
    {
        Depart(room, me, null, now);
        return WatchPartyResult.NoContent();
    });

    /// <summary>
    /// Holds until there's a message after <paramref name="cursor"/> or <paramref name="waitSeconds"/>
    /// pass. A cursor of 0 answers at once with the room and its chat. Every call marks the member seen.
    /// </summary>
    public async Task<WatchPartyResult> Events(WatchPartyCaller caller, string roomId, string? token, long cursor, int waitSeconds, CancellationToken cancellationToken = default)
    {
        var wait = TimeSpan.FromSeconds(Math.Max(0, Math.Min(MaxWaitSeconds, waitSeconds)));
        var deadline = _now() + wait;
        Member? polling = null;
        try
        {
            while (true)
            {
                Task signal;
                TimeSpan remaining;
                lock (_gate)
                {
                    var refused = Find(caller, roomId, token, lingering: true, out var room, out var me);
                    if (refused != null)
                    {
                        return refused;
                    }

                    if (room.ClosedAt != null || me.GoneAt != null)
                    {
                        return LastWord(room, me, cursor);
                    }

                    var now = _now();
                    me.PublicBase = caller.PublicBase ?? me.PublicBase;
                    Seen(room, me, now);
                    var batch = Batch(room, me, cursor, out var any);
                    remaining = deadline - now;

                    if (any || remaining <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
                    {
                        return WatchPartyResult.Ok(batch);
                    }

                    if (polling == null)
                    {
                        polling = me;
                        me.Polls++;
                    }

                    signal = room.Signal.Task;
                }

                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeout = _delay(remaining, stop.Token);
                var first = await Task.WhenAny(signal, timeout).ConfigureAwait(false);
                stop.Cancel();
                if (first != signal)
                {
                    deadline = DateTime.MinValue;
                }
            }
        }
        finally
        {
            if (polling != null)
            {
                lock (_gate)
                {
                    polling.Polls--;
                    polling.LastSeen = _now();
                }
            }
        }
    }

    /// <summary>
    /// For a closed room or a member who's gone: what's left to read, always including the
    /// room_closed or removed the client's cursor hasn't passed yet, and not_found once it has,
    /// so a client loop hears why and then ends.
    /// </summary>
    private WatchPartyResult LastWord(Room room, Member me, long cursor)
    {
        if (cursor > 0 && cursor <= room.Seq && cursor >= room.DroppedThrough)
        {
            var batch = Batch(room, me, cursor, out var any);
            if (any)
            {
                return WatchPartyResult.Ok(batch);
            }
        }
        else
        {
            var last = new[] { me.Farewell, room.Farewell }.Where(e => e != null && e.Seq > cursor).Select(e => (object)e!.Message!).ToList();
            if (last.Count > 0)
            {
                return WatchPartyResult.Ok(new Dictionary<string, object?> { ["cursor"] = room.Seq, ["messages"] = last });
            }
        }

        return WatchPartyResult.Error("not_found", "That watch party has ended.");
    }

    /// <summary>Applies the client's messages. Only pongs come back in the answer.</summary>
    public WatchPartyResult Messages(WatchPartyCaller caller, string roomId, string? token, MessagesBody body) => AsMember(caller, roomId, token, (room, me, receivedAt) =>
    {
        var replies = new List<object>();
        foreach (var message in (body.Messages ?? new List<InboundMessage>()).Take(20))
        {
            if (room.ClosedAt != null || me.GoneAt != null)
            {
                break;
            }

            if (message.Type == "ping")
            {
                replies.Add(new Dictionary<string, object?>
                {
                    ["type"] = "pong",
                    ["client_sent_at"] = message.ClientSentAt,
                    ["server_received_at"] = WatchPartyJson.Time(receivedAt),
                    ["server_sent_at"] = WatchPartyJson.Time(_now()),
                });
                continue;
            }

            Apply(room, me, message, _now());
        }

        return WatchPartyResult.Ok(new Dictionary<string, object?> { ["messages"] = replies });
    });

    public WatchPartyResult AdminRooms() => Locked(() =>
    {
        var now = _now();
        var rooms = OpenRooms().OrderByDescending(r => r.CreatedAt).Select(room => new AdminRoomDto
        {
            RoomId = room.Id,
            Code = room.Code,
            HostName = room.Host?.DisplayName ?? string.Empty,
            Phase = room.Phase,
            PlaybackState = room.PlaybackState,
            PositionSeconds = Round(Position(room, now)),
            ContentTitle = room.Item?.Title,
            ContentLabel = room.Item?.Label,
            HasPin = room.Pin != null,
            CreatedAt = WatchPartyJson.Time(room.CreatedAt),
            Members = Ordered(room).Select(m => new AdminMemberDto
            {
                MemberId = m.Id,
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                IsHost = m.Id == room.HostId,
                Connected = m.Connected,
                Status = m.Status,
            }).ToList(),
        }).ToList();
        return WatchPartyResult.Ok(new Dictionary<string, object?> { ["rooms"] = rooms });
    });

    public WatchPartyResult AdminClose(string roomId) => Locked(() =>
    {
        if (!_rooms.TryGetValue(roomId, out var room) || room.ClosedAt != null)
        {
            return WatchPartyResult.Error("not_found", "That watch party has ended.");
        }

        Close(room, "closed_by_admin", _now());
        return WatchPartyResult.NoContent();
    });

    public WatchPartyResult AdminRemove(string roomId, RoomBody body) => Locked(() =>
    {
        var target = _rooms.TryGetValue(roomId, out var room) && room.ClosedAt == null
            ? room.Active.FirstOrDefault(m => m.Id == body.MemberId)
            : null;
        if (target == null)
        {
            return WatchPartyResult.Error("not_found", "That person isn't in a party.");
        }

        Depart(room!, target, "removed_by_admin", _now());
        return WatchPartyResult.NoContent();
    });

    /// <summary>Liveness, the ready wait's timeout and cleanup. <see cref="StartTimer"/> runs it every second.</summary>
    public void Sweep()
    {
        Locked(() =>
        {
            var now = _now();
            var enabled = Settings.Enabled;
            foreach (var room in _rooms.Values.ToList())
            {
                if (room.ClosedAt != null)
                {
                    if (now - room.ClosedAt.Value > Linger)
                    {
                        _rooms.Remove(room.Id);
                    }

                    continue;
                }

                if (!enabled)
                {
                    Close(room, "disabled", now);
                    continue;
                }

                var changed = false;
                foreach (var m in room.Active.ToList())
                {
                    if (m.Polls > 0)
                    {
                        m.LastSeen = now;
                    }

                    var idle = now - m.LastSeen;
                    if (idle > RemoveAfter)
                    {
                        Depart(room, m, null, now);
                    }
                    else if (idle > DisconnectAfter && m.Connected)
                    {
                        m.Connected = false;
                        changed = true;
                    }
                }

                room.Members.RemoveAll(m => m.GoneAt != null && now - m.GoneAt.Value > Linger);
                if (room.ClosedAt != null)
                {
                    continue;
                }

                if (changed)
                {
                    Changed(room);
                    CheckReady(room, now);
                }

                if (room.PlaybackState == "waiting" && now - room.WaitStarted >= ReadyWait)
                {
                    EndWait(room, now);
                }

                if (room.PlaybackState == "playing" && room.Item?.RuntimeSeconds is double runtime && runtime > 0
                    && Position(room, now) >= runtime - EndSlackSeconds)
                {
                    room.EndingSince ??= now;
                    if (now - room.EndingSince.Value >= EndGrace)
                    {
                        BackToLobby(room, now);
                    }
                }
                else
                {
                    room.EndingSince = null;
                }
            }

            return WatchPartyResult.NoContent();
        });
    }

    /// <summary>Uppercase with spaces and dashes dropped, or null when it can't be a room code.</summary>
    public static string? NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var code = new string(input!.Where(c => c != ' ' && c != '-').Select(char.ToUpperInvariant).ToArray());
        return code.Length == 6 && code.All(c => CodeAlphabet.IndexOf(c) >= 0) ? code : null;
    }

    private void Apply(Room room, Member me, InboundMessage message, DateTime now)
    {
        switch (message.Type)
        {
            case "transport_request":
                TransportRequest(room, me, message, now);
                break;
            case "buffering":
                SetStatus(room, me, "buffering");
                if (me.Id == room.HostId && room.PlaybackState == "playing")
                {
                    BeginWait(room, me, Math.Max(0, message.PositionSeconds ?? Position(room, now)), hostOnly: true, now);
                }

                break;
            case "state_report":
                StateReport(room, me, message, now);
                break;
            case "attach_session":
                var session = message.SessionId is { Length: > 0 and <= 128 } s ? s : null;
                if (session != me.SessionId)
                {
                    me.SessionId = session;
                    Changed(room);
                }

                break;
            case "chat":
                Chat(room, me, message.Text, now);
                break;
            case "reaction":
                if (!Settings.Reactions)
                {
                    PrivateError(room, me, "not_allowed", "Reactions are turned off on this server.");
                }
                else if (message.Reaction == null || !Reactions.Contains(message.Reaction))
                {
                    PrivateError(room, me, "bad_request", "That isn't a reaction.");
                }
                else if (!me.ReactionBucket.Take(now))
                {
                    PrivateError(room, me, "rate_limited", "Slow down a little.");
                }
                else
                {
                    var reaction = Message("reaction");
                    reaction["member_id"] = me.Id;
                    reaction["display_name"] = me.DisplayName;
                    reaction["reaction"] = message.Reaction;
                    Append(room, reaction);
                }

                break;
        }
    }

    private void TransportRequest(Room room, Member me, InboundMessage message, DateTime now)
    {
        if (!me.TransportBucket.Take(now))
        {
            PrivateError(room, me, "rate_limited", "Slow down a little.");
            return;
        }

        var action = message.Action;
        if (action != "play" && action != "pause" && action != "seek")
        {
            PrivateError(room, me, "bad_request", "That isn't a playback action.");
            return;
        }

        if (room.Phase != "playing" || !CanSend(room, me, action))
        {
            PrivateError(room, me, "not_allowed", "You can't control playback in this party.");
            return;
        }

        var position = Math.Max(0, message.PositionSeconds ?? Position(room, now));
        if (room.PlaybackState == "waiting")
        {
            if (action != "seek" || room.WaitHostOnly)
            {
                PrivateError(room, me, "not_allowed", "Wait for everyone's player first.");
                return;
            }

            // A seek before the start moves where everyone loads, so readiness starts over.
            BeginWait(room, room.WaitBy ?? me, position, hostOnly: false, now);
            return;
        }

        if (action == "play" && room.PlaybackState == "paused")
        {
            // A resume starts everyone from the paused spot, not from where the asking player stopped.
            position = room.Anchor;
        }

        var executeAt = now.AddMilliseconds(action == "play" && room.IsPaused ? 1000 : 300);
        if (action != "seek")
        {
            room.IsPaused = action == "pause";
        }

        room.PlaybackState = room.IsPaused ? "paused" : "playing";
        room.Anchor = position;
        room.AnchorAt = executeAt;
        Changed(room);
        Command(room, action, position, executeAt, room.PlaybackState, me, countdown: false);
    }

    private void StateReport(Room room, Member me, InboundMessage message, DateTime now)
    {
        var status = message.Status != null && Statuses.Contains(message.Status) ? message.Status : null;
        if (string.IsNullOrEmpty(message.SessionId))
        {
            // Without a player session it's a status alone, so it never moves the anchor or counts as ready.
            if (status != null)
            {
                SetStatus(room, me, status);
            }

            return;
        }

        if (room.PlaybackState == "waiting")
        {
            if (room.WaitHostOnly)
            {
                if (me.Id == room.HostId && status != "buffering")
                {
                    SetStatus(room, me, status ?? "in_sync");
                    room.WaitPosition = Math.Max(0, message.PositionSeconds ?? room.WaitPosition);
                    EndWait(room, now);
                    return;
                }
            }
            else if (message.IsReady == true && (message.CommandId == null || message.CommandId == room.WaitCommandId) && !me.NoAccess)
            {
                room.Ready.Add(me.Id);
                SetStatus(room, me, status ?? "ready");
                CheckReady(room, now);
                return;
            }
        }
        else if (room.Phase == "playing" && message.PositionSeconds is double reported && now >= room.AnchorAt)
        {
            // Nothing is judged before the last command has run, since players are still getting there.
            var matches = message.IsPaused == null || message.IsPaused == room.IsPaused;
            if (me.Id == room.HostId && status != "buffering")
            {
                // The host's reports keep a playing room's clock honest, so a stalled host slows it,
                // but they never override a paused position or a command that's still landing.
                if (matches && room.PlaybackState == "playing" && status != "catching_up" && now >= room.LastCommandAt + HostSettle)
                {
                    room.Anchor = Math.Max(0, reported);
                    room.AnchorAt = now;
                }

                status ??= "in_sync";
            }
            else if (status == null)
            {
                status = matches && Math.Abs(reported - Position(room, now)) <= 2 ? "in_sync" : "catching_up";
            }
        }

        if (status != null)
        {
            SetStatus(room, me, status);
        }
    }

    private void Chat(Room room, Member me, string? text, DateTime now)
    {
        if (!Settings.Chat)
        {
            PrivateError(room, me, "not_allowed", "Chat is turned off on this server.");
            return;
        }

        var clean = CleanChat(text);
        if (clean.Length == 0)
        {
            return;
        }

        if (!me.ChatBucket.Take(now))
        {
            PrivateError(room, me, "rate_limited", "Slow down a little.");
            return;
        }

        var chat = new ChatDto { Id = NewId(), MemberId = me.Id, DisplayName = me.DisplayName, Text = clean, SentAt = WatchPartyJson.Time(now) };
        room.Chat.Add(chat);
        if (room.Chat.Count > ChatHistory)
        {
            room.Chat.RemoveAt(0);
        }

        var message = Message("chat");
        message["message"] = chat;
        Append(room, message);
    }

    private static string CleanChat(string? text)
    {
        var kept = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (kept.Length <= ChatMaxLength)
        {
            return kept;
        }

        var cut = char.IsHighSurrogate(kept[ChatMaxLength - 1]) ? ChatMaxLength - 1 : ChatMaxLength;
        return kept.Substring(0, cut).TrimEnd();
    }

    private void Select(Room room, WatchPartyItem item, DateTime now)
    {
        room.Generation += room.ContentId == item.Id ? 0 : 1;
        room.ContentId = item.Id;
        room.Item = item;
        if (room.SelectionMode == "vote")
        {
            room.SelectionMode = "host_pick";
            ClearSuggestions(room);
        }

        var canPlay = new Dictionary<string, bool>(StringComparer.Ordinal) { [room.Host?.UserId ?? string.Empty] = true };
        foreach (var m in room.Active)
        {
            if (!canPlay.TryGetValue(m.UserId, out var ok))
            {
                ok = _host.FindItem(m.UserId, item.Id) != null;
                canPlay[m.UserId] = ok;
            }

            if (!ok)
            {
                m.NoAccess = true;
                m.Status = "not_watching";
                Private(room, m, Notice("no_access", m, item.Label));
            }
            else if (m.NoAccess)
            {
                m.NoAccess = false;
                m.Status = "ready";
            }
        }

        if (!room.Announced)
        {
            room.Announced = true;
            Announce(room, item, now);
        }
    }

    private void Announce(Room room, WatchPartyItem item, DateTime now)
    {
        var host = room.Host;
        if (host == null || !Settings.Alerts)
        {
            return;
        }

        if (_lastAnnounced.TryGetValue(host.UserId, out var last) && now - last < AnnounceEvery)
        {
            return;
        }

        _lastAnnounced[host.UserId] = now;
        _announce.Add(new WatchPartyAnnouncement
        {
            HostUserId = host.UserId,
            HostName = host.DisplayName,
            Code = room.Code,
            ContentId = item.Id,
            ContentLabel = item.Label,
            LobbyCount = room.Active.Count(),
            MemberUserIds = room.Active.Select(m => m.UserId).Distinct().ToList(),
        });
    }

    private void BeginWait(Room room, Member by, double position, bool hostOnly, DateTime now)
    {
        room.PlaybackState = "waiting";
        room.IsPaused = true;
        room.Anchor = position;
        room.AnchorAt = now;
        room.WaitPosition = position;
        room.WaitStarted = now;
        room.WaitHostOnly = hostOnly;
        room.WaitBy = by;
        room.Ready.Clear();
        Changed(room);
        room.WaitCommandId = Command(room, hostOnly ? "pause" : "seek", position, now.AddMilliseconds(300), "waiting", by, countdown: false);
    }

    private void CheckReady(Room room, DateTime now)
    {
        if (room.PlaybackState == "waiting" && !room.WaitHostOnly
            && room.Active.Where(m => m.Connected && !m.NoAccess).All(m => room.Ready.Contains(m.Id)))
        {
            EndWait(room, now);
        }
    }

    private void EndWait(Room room, DateTime now)
    {
        var countdown = !room.WaitHostOnly;
        var executeAt = now.AddMilliseconds(countdown ? 3000 : 1000);
        var by = room.WaitBy;
        if (countdown)
        {
            foreach (var m in room.Active.Where(m => room.Ready.Contains(m.Id)))
            {
                m.Status = "in_sync";
            }
        }

        room.PlaybackState = "playing";
        room.IsPaused = false;
        room.Anchor = room.WaitPosition;
        room.AnchorAt = executeAt;
        room.EndWait();
        Changed(room);
        Command(room, "play", room.Anchor, executeAt, "playing", by, countdown);
    }

    private string Command(Room room, string action, double position, DateTime executeAt, string playbackState, Member? by, bool countdown)
    {
        room.LastCommandAt = executeAt;
        var command = new CommandDto
        {
            CommandId = NewId(),
            Generation = room.Generation,
            SelectionRevision = room.SelectionRevision,
            Action = action,
            PositionSeconds = Round(position),
            ExecuteAt = WatchPartyJson.Time(executeAt),
            PlaybackState = playbackState,
            ByMemberId = by?.Id,
            ByDisplayName = by?.DisplayName,
            Countdown = countdown,
        };
        var message = Message("transport_command");
        message["command"] = command;
        Append(room, message);
        return command.CommandId;
    }

    private void Depart(Room room, Member member, string? reason, DateTime now)
    {
        member.GoneAt = now;
        room.Ready.Remove(member.Id);
        if (reason != null)
        {
            var removed = Message("removed");
            removed["reason"] = reason;
            member.Farewell = Private(room, member, removed);
        }

        Append(room, Notice("left", member));
        if (!room.Active.Any())
        {
            Close(room, "empty", now);
            return;
        }

        if (room.HostId == member.Id)
        {
            var next = room.Active.Where(m => m.Connected).OrderBy(m => m.JoinedAt).FirstOrDefault()
                ?? room.Active.OrderBy(m => m.JoinedAt).First();
            room.HostId = next.Id;
            if (room.WaitBy == member)
            {
                room.WaitBy = next;
            }

            Append(room, Notice("host_changed", next));
        }

        Changed(room);
        CheckReady(room, now);
    }

    private void Close(Room room, string reason, DateTime now)
    {
        room.ClosedAt = now;
        var closed = Message("room_closed");
        closed["reason"] = reason;
        room.Farewell = Append(room, closed);
    }

    private void SetStatus(Room room, Member member, string status)
    {
        if (member.NoAccess && status != "not_watching")
        {
            return;
        }

        if (member.Status != status)
        {
            member.Status = status;
            Changed(room);
        }
    }

    private void Seen(Room room, Member member, DateTime now)
    {
        member.LastSeen = now;
        var changed = !member.Connected;
        member.Connected = true;
        if (member.Status == "joining" && room.Phase == "lobby" && !member.NoAccess)
        {
            member.Status = "ready";
            changed = true;
        }

        if (changed && room.ClosedAt == null && member.GoneAt == null)
        {
            Changed(room);
        }
    }

    private static bool CanSend(Room room, Member member, string action) =>
        member.Id == room.HostId
        || room.Policy == "everyone"
        || (room.Policy == "guests_can_pause" && action != "seek");

    private WatchPartyResult? CheckPolicy(string policy)
    {
        if (!Policies.Contains(policy))
        {
            return BadRequest("guest_control_policy is host_only, guests_can_pause or everyone.");
        }

        return policy == "everyone" && !Settings.EveryoneControl
            ? NotAllowed("This server doesn't let everyone control playback.")
            : null;
    }

    // The batch the member reads after the cursor: the room's messages and their own, in order,
    // with one snapshot rendered for them where the last snapshot falls.
    private Dictionary<string, object?> Batch(Room room, Member me, long cursor, out bool any)
    {
        var batch = new Dictionary<string, object?> { ["cursor"] = room.Seq };
        var messages = new List<object>();
        batch["messages"] = messages;
        any = true;
        if (cursor <= 0 || cursor > room.Seq || cursor < room.DroppedThrough)
        {
            if (cursor > 0)
            {
                batch["reset"] = true;
            }

            messages.Add(Snapshot(room, me));
            var history = Message("chat_history");
            history["messages"] = room.Chat.ToList();
            messages.Add(history);
            messages.AddRange(me.Private.Where(e => e.Seq > cursor).Select(e => (object)e.Message!));
            me.Private.Clear();
            return batch;
        }

        me.Private.RemoveAll(e => e.Seq <= cursor);
        var entries = room.Ring.Where(e => e.Seq > cursor).Concat(me.Private).OrderBy(e => e.Seq).ToList();
        var lastSnapshot = entries.FindLastIndex(e => e.Message == null);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Message != null)
            {
                messages.Add(entries[i].Message!);
            }
            else if (i == lastSnapshot)
            {
                messages.Add(Snapshot(room, me));
            }
        }

        any = messages.Count > 0;
        return batch;
    }

    private Dictionary<string, object?> Snapshot(Room room, Member me)
    {
        var snapshot = Message("snapshot");
        snapshot["room"] = Render(room, me);
        return snapshot;
    }

    private RoomDto Render(Room room, Member me)
    {
        var s = Settings;
        var item = room.Item;
        return new RoomDto
        {
            RoomId = room.Id,
            Code = room.Code,
            InvitePath = "/Moonfin/Party/" + room.Code,
            InviteUrl = me.PublicBase == null ? null : me.PublicBase + "/Moonfin/Party/" + room.Code,
            Phase = room.Phase,
            PlaybackState = room.PlaybackState,
            SelectionMode = room.SelectionMode,
            SelectionRevision = room.SelectionRevision,
            SelectedContentId = room.ContentId,
            GuestControlPolicy = room.Policy,
            IsPaused = room.IsPaused,
            AnchorPositionSeconds = Round(room.Anchor),
            AnchorAt = WatchPartyJson.Time(room.AnchorAt),
            Generation = room.Generation,
            SelfRole = me.Id == room.HostId ? "host" : "guest",
            SelfCanControlTransport = me.Id == room.HostId || room.Policy != "host_only",
            SelfMemberId = me.Id,
            AttachedSessionId = me.SessionId,
            Members = Ordered(room).Select(m => new MemberDto
            {
                ProfileId = m.Id,
                DisplayName = m.DisplayName,
                IsHost = m.Id == room.HostId,
                IsSelf = m == me,
                Connected = m.Connected,
                UserId = m.UserId,
                Status = m.Status,
                AvatarUrl = m.AvatarUrl,
            }).ToList(),
            HasPin = room.Pin != null,
            ContentTitle = item?.Title,
            ContentType = item?.Type,
            SeriesName = item?.SeriesName,
            Season = item?.Season,
            Episode = item?.Episode,
            ChatEnabled = s.Chat,
            ReactionsEnabled = s.Reactions,
            MaxMembers = MaxMembersOf(s),
            CreatedAt = WatchPartyJson.Time(room.CreatedAt),
        };
    }

    private SummaryDto Summary(Room room, WatchPartyCaller caller, DateTime now)
    {
        var members = Ordered(room).ToList();
        var item = room.Item;
        return new SummaryDto
        {
            RoomId = room.Id,
            HostName = room.Host?.DisplayName ?? string.Empty,
            MemberCount = members.Count,
            MembersPreview = members.Take(3).Select(m => m.DisplayName).ToList(),
            Phase = room.Phase,
            PlaybackState = room.PlaybackState,
            PositionSeconds = Round(Position(room, now)),
            SelectedContentId = room.ContentId,
            ContentTitle = item?.Title,
            ContentType = item?.Type,
            SeriesName = item?.SeriesName,
            Season = item?.Season,
            Episode = item?.Episode,
            HasPin = room.Pin != null,
            GuestControlPolicy = room.Policy,
            SelectionMode = room.SelectionMode,
            IsMember = members.Any(m => m.UserId == caller.UserId),
            CreatedAt = WatchPartyJson.Time(room.CreatedAt),
        };
    }

    private static List<SuggestionDto> SuggestionList(Room room, Member? me) => room.Suggestions
        .OrderByDescending(s => s.Votes.Count)
        .ThenBy(s => s.CreatedAt)
        .ThenBy(s => s.Id, StringComparer.Ordinal)
        .Select(s => new SuggestionDto
        {
            Id = s.Id,
            ContentId = s.ContentId,
            Title = s.Title,
            Subtitle = s.Subtitle,
            VoteCount = s.Votes.Count,
            VotedByMe = me != null && s.Votes.Contains(me.Id),
            CreatedAt = WatchPartyJson.Time(s.CreatedAt),
        })
        .ToList();

    private void SuggestionsChanged(Room room)
    {
        var update = Message("suggestions_update");
        update["suggestions"] = SuggestionList(room, null);
        Append(room, update);
    }

    private void ClearSuggestions(Room room)
    {
        if (room.Suggestions.Count > 0)
        {
            room.Suggestions.Clear();
            SuggestionsChanged(room);
        }
    }

    private static IEnumerable<Member> Ordered(Room room) =>
        room.Active.OrderByDescending(m => m.Id == room.HostId).ThenBy(m => m.JoinedAt);

    private static double Position(Room room, DateTime now) =>
        room.PlaybackState == "playing" && !room.IsPaused
            ? room.Anchor + Math.Max(0, (now - room.AnchorAt).TotalSeconds)
            : room.Anchor;

    private static double Round(double seconds) => Math.Round(seconds, 3);

    private static int MaxMembersOf(WatchPartySettings s) => Math.Max(2, Math.Min(100, s.MaxMembers));

    private static bool IsPin(string pin) => pin.Length >= 4 && pin.Length <= 8 && pin.All(c => c >= '0' && c <= '9');

    private static bool IsCaller(Member m, WatchPartyCaller caller) => m.UserId == caller.UserId && m.DeviceId == caller.DeviceId;

    private IEnumerable<Room> OpenRooms() => _rooms.Values.Where(r => r.ClosedAt == null);

    private Member NewMember(WatchPartyCaller caller, DateTime now)
    {
        var profile = _host.Profile(caller.UserId);
        return new Member(caller.UserId, caller.DeviceId, profile.DisplayName, profile.AvatarUrl, now) { PublicBase = caller.PublicBase };
    }

    private string NewCode()
    {
        while (true)
        {
            var chars = new char[6];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }

            var code = new string(chars);
            if (!OpenRooms().Any(r => r.Code == code))
            {
                return code;
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static Dictionary<string, object?> Message(string type) => new Dictionary<string, object?> { ["type"] = type };

    private static Dictionary<string, object?> Notice(string kind, Member member, string? contentTitle = null)
    {
        var notice = Message("notice");
        notice["kind"] = kind;
        notice["member_id"] = member.Id;
        notice["display_name"] = member.DisplayName;
        if (contentTitle != null)
        {
            notice["content_title"] = contentTitle;
        }

        return notice;
    }

    private static void Changed(Room room) => Append(room, null);

    // A null message marks a snapshot, rendered per member when it's read.
    private static Entry Append(Room room, Dictionary<string, object?>? message)
    {
        var entry = new Entry(++room.Seq, message);
        room.Ring.Enqueue(entry);
        while (room.Ring.Count > RingSize)
        {
            room.DroppedThrough = room.Ring.Dequeue().Seq;
        }

        room.Wake();
        return entry;
    }

    private static Entry Private(Room room, Member member, Dictionary<string, object?> message)
    {
        var entry = new Entry(++room.Seq, message);
        member.Private.Add(entry);
        if (member.Private.Count > 50)
        {
            member.Private.RemoveAt(0);
        }

        room.Wake();
        return entry;
    }

    private static void PrivateError(Room room, Member member, string code, string text)
    {
        var error = Message("error");
        error["code"] = code;
        error["message"] = text;
        Private(room, member, error);
    }

    private WatchPartyResult? Find(WatchPartyCaller caller, string roomId, string? token, bool lingering, out Room room, out Member member)
    {
        room = null!;
        member = null!;
        if (!_rooms.TryGetValue(roomId, out var found) || (found.ClosedAt != null && !lingering))
        {
            return WatchPartyResult.Error("not_found", "That watch party has ended.");
        }

        var mine = string.IsNullOrEmpty(token) ? null : found.Members.FirstOrDefault(m => m.Token == token);
        if (mine == null || mine.UserId != caller.UserId)
        {
            return NotAllowed("That room token isn't yours.");
        }

        if (mine.GoneAt != null && !lingering)
        {
            return WatchPartyResult.Error("not_found", "You're not in this party anymore.");
        }

        room = found;
        member = mine;
        return null;
    }

    private WatchPartyResult AsMember(WatchPartyCaller caller, string roomId, string? token, Func<Room, Member, DateTime, WatchPartyResult> action) => Locked(() =>
    {
        var refused = Find(caller, roomId, token, lingering: false, out var room, out var me);
        if (refused != null)
        {
            return refused;
        }

        var now = _now();
        me.LastSeen = now;
        me.PublicBase = caller.PublicBase ?? me.PublicBase;
        return action(room, me, now);
    });

    private WatchPartyResult AsHost(WatchPartyCaller caller, string roomId, string? token, Func<Room, Member, DateTime, WatchPartyResult> action) =>
        AsMember(caller, roomId, token, (room, me, now) => me.Id == room.HostId
            ? action(room, me, now)
            : WatchPartyResult.Error("not_host", "Only the host can do that."));

    private WatchPartyResult Locked(Func<WatchPartyResult> action)
    {
        WatchPartyResult result;
        List<WatchPartyAnnouncement>? announce = null;
        lock (_gate)
        {
            result = action();
            if (_announce.Count > 0)
            {
                announce = _announce.ToList();
                _announce.Clear();
            }
        }

        foreach (var a in announce ?? Enumerable.Empty<WatchPartyAnnouncement>())
        {
            _host.Announce(a);
        }

        return result;
    }

    private WatchPartyResult Access(Room room, Member member) =>
        WatchPartyResult.Ok(new Dictionary<string, object?> { ["room"] = Render(room, member), ["token"] = member.Token });

    private WatchPartyResult RoomOf(Room room, Member member) =>
        WatchPartyResult.Ok(new Dictionary<string, object?> { ["room"] = Render(room, member) });

    private static WatchPartyResult BadRequest(string message) => WatchPartyResult.Error("bad_request", message);

    private static WatchPartyResult NotAllowed(string message) => WatchPartyResult.Error("not_allowed", message);

    private static WatchPartyResult NoAccess() => WatchPartyResult.Error("no_access", "You don't have access to what they're watching.");

    private sealed class Entry
    {
        public Entry(long seq, Dictionary<string, object?>? message)
        {
            Seq = seq;
            Message = message;
        }

        public long Seq { get; }

        public Dictionary<string, object?>? Message { get; }
    }

    private sealed class Member
    {
        public Member(string userId, string deviceId, string displayName, string? avatarUrl, DateTime now)
        {
            UserId = userId;
            DeviceId = deviceId;
            DisplayName = displayName;
            AvatarUrl = avatarUrl;
            JoinedAt = now;
            LastSeen = now;
        }

        public string Id { get; } = NewId();

        public string Token { get; } = NewToken();

        public string UserId { get; }

        public string DeviceId { get; }

        public string DisplayName { get; }

        public string? AvatarUrl { get; }

        /// <summary>The address this member last called the server at, for their invite_url.</summary>
        public string? PublicBase { get; set; }

        public DateTime JoinedAt { get; }

        public DateTime LastSeen { get; set; }

        public bool Connected { get; set; } = true;

        public string Status { get; set; } = "joining";

        public string? SessionId { get; set; }

        public bool NoAccess { get; set; }

        /// <summary>Long-polls holding right now, which count as being seen.</summary>
        public int Polls { get; set; }

        /// <summary>Set once the member left or was removed.</summary>
        public DateTime? GoneAt { get; set; }

        /// <summary>The removed message, kept until the member has read past it.</summary>
        public Entry? Farewell { get; set; }

        public List<Entry> Private { get; } = new List<Entry>();

        public Bucket ChatBucket { get; } = new Bucket(1, 5);

        public Bucket ReactionBucket { get; } = new Bucket(4, 4);

        public Bucket TransportBucket { get; } = new Bucket(10, 10);

        private static string NewToken()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    private sealed class Suggestion
    {
        public Suggestion(WatchPartyItem item, DateTime now)
        {
            ContentId = item.Id;
            Title = item.Title;
            Subtitle = item.Label != item.Title ? item.Label : item.Year?.ToString() ?? string.Empty;
            CreatedAt = now;
        }

        public string Id { get; } = NewId();

        public string ContentId { get; }

        public string Title { get; }

        public string Subtitle { get; }

        public DateTime CreatedAt { get; }

        public HashSet<string> Votes { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed class Room
    {
        public Room(string id, string code, DateTime now)
        {
            Id = id;
            Code = code;
            CreatedAt = now;
            AnchorAt = now;
        }

        public string Id { get; }

        public string Code { get; }

        public DateTime CreatedAt { get; }

        public string Phase { get; set; } = "lobby";

        public string PlaybackState { get; set; } = "idle";

        public string SelectionMode { get; set; } = "host_pick";

        public string Policy { get; set; } = "host_only";

        public int SelectionRevision { get; set; }

        public string? ContentId { get; set; }

        public WatchPartyItem? Item { get; set; }

        public bool IsPaused { get; set; } = true;

        public double Anchor { get; set; }

        public DateTime AnchorAt { get; set; }

        /// <summary>Only moves on start, stop and a new selection, so a client can tell a new playback run from another snapshot of the same one.</summary>
        public long Generation { get; set; }

        public string HostId { get; set; } = string.Empty;

        public PinHash? Pin { get; set; }

        public bool Announced { get; set; }

        public string? WaitCommandId { get; set; }

        public DateTime WaitStarted { get; set; }

        public double WaitPosition { get; set; }

        /// <summary>The host is buffering, so only the host's player is waited on.</summary>
        public bool WaitHostOnly { get; set; }

        public Member? WaitBy { get; set; }

        public HashSet<string> Ready { get; } = new HashSet<string>(StringComparer.Ordinal);

        public long Seq { get; set; }

        public long DroppedThrough { get; set; }

        public Queue<Entry> Ring { get; } = new Queue<Entry>();

        public List<ChatDto> Chat { get; } = new List<ChatDto>();

        public List<Suggestion> Suggestions { get; } = new List<Suggestion>();

        /// <summary>Wrong PIN times per user, kept for a minute.</summary>
        public Dictionary<string, List<DateTime>> PinFailures { get; } = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);

        /// <summary>Includes members who left, until they're swept.</summary>
        public List<Member> Members { get; } = new List<Member>();

        public IEnumerable<Member> Active => Members.Where(m => m.GoneAt == null);

        public Member? Host => Members.FirstOrDefault(m => m.Id == HostId);

        public DateTime? ClosedAt { get; set; }

        /// <summary>When the room's position first came within reach of the item's end, while it plays.</summary>
        public DateTime? EndingSince { get; set; }

        /// <summary>The execute_at of the newest transport command.</summary>
        public DateTime LastCommandAt { get; set; }

        /// <summary>The room_closed message, kept until each member has read past it.</summary>
        public Entry? Farewell { get; set; }

        public TaskCompletionSource<bool> Signal { get; private set; } = NewSignal();

        public void EndWait()
        {
            WaitCommandId = null;
            WaitHostOnly = false;
            WaitBy = null;
            Ready.Clear();
        }

        /// <summary>Releases every poll waiting on the room.</summary>
        public void Wake()
        {
            var signal = Signal;
            Signal = NewSignal();
            signal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A token bucket: <c>perSecond</c> refill up to <c>burst</c>.</summary>
    private sealed class Bucket
    {
        private readonly double _perSecond;
        private readonly double _burst;
        private double _tokens;
        private DateTime _at;

        public Bucket(double perSecond, double burst)
        {
            _perSecond = perSecond;
            _burst = burst;
            _tokens = burst;
        }

        public bool Take(DateTime now)
        {
            if (_at != default)
            {
                _tokens = Math.Min(_burst, _tokens + Math.Max(0, (now - _at).TotalSeconds) * _perSecond);
            }

            _at = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens--;
            return true;
        }
    }
}

/// <summary>A room PIN, salted and hashed. Rooms live in memory, and wrong guesses are capped per minute.</summary>
public sealed class PinHash
{
    private const int Iterations = 10_000;
    private readonly byte[] _salt;
    private readonly byte[] _hash;

    private PinHash(byte[] salt, byte[] hash)
    {
        _salt = salt;
        _hash = hash;
    }

    public static PinHash Create(string pin)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        return new PinHash(salt, Derive(pin, salt));
    }

    public bool Matches(string guess) => CryptographicOperations.FixedTimeEquals(Derive(guess, _salt), _hash);

    private static byte[] Derive(string pin, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(pin, salt, Iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }
}
