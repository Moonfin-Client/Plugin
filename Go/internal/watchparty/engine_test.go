package watchparty

import (
	"context"
	"encoding/json"
	"math"
	"net/url"
	"os"
	"strings"
	"testing"
	"time"
)

type fakeHost struct {
	items   map[string]*Item
	blocked map[[2]string]bool
}

func (h *fakeHost) FindItem(_ context.Context, c Caller, id string) *Item {
	if h.blocked[[2]string{c.UserID, id}] {
		return nil
	}
	return h.items[id]
}

type harness struct {
	t        *testing.T
	host     *fakeHost
	settings Settings
	now      time.Time
	e        *Engine
}

func newHarness(t *testing.T) *harness {
	series, season, episode, year := "Bluey", 2, 3, 2022
	h := &harness{t: t, settings: DefaultSettings(), now: time.Date(2026, 11, 9, 21, 0, 0, 0, time.UTC)}
	h.host = &fakeHost{blocked: map[[2]string]bool{}, items: map[string]*Item{
		"movie1": {ID: "movie1", Title: "Avatar", Type: "Movie", Year: &year},
		"kids1":  {ID: "kids1", Title: "Bluey", Type: "Episode", SeriesName: &series, Season: &season, Episode: &episode, RuntimeSeconds: ptr(100.0)},
	}}
	h.e = New(h.host, func() Settings { return h.settings }, func() time.Time { return h.now })
	return h
}

func caller(user string) Caller {
	return Caller{UserID: user, DeviceID: "d1", Name: user, Token: "tok-" + user}
}

// js turns a result into plain JSON values, the way a client reads it.
func js(t *testing.T, r Result) M {
	t.Helper()
	var out M
	if err := json.Unmarshal(mustJSON(t, r.Body), &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func mustJSON(t *testing.T, v any) []byte {
	t.Helper()
	raw, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return raw
}

// Code is the error code, or "" on success.
func (r Result) Code() string {
	if m, ok := r.Body.(M); ok {
		if c, ok := m["code"].(string); ok {
			return c
		}
	}
	return ""
}

func (h *harness) create(user string, body CreateBody) (string, string) {
	h.t.Helper()
	r := h.e.Create(context.Background(), caller(user), body)
	if r.Status != 200 {
		h.t.Fatalf("create: %d %v", r.Status, r.Body)
	}
	out := js(h.t, r)
	return out["room"].(M)["room_id"].(string), out["token"].(string)
}

func (h *harness) join(user, id string) string {
	h.t.Helper()
	r := h.e.Join(context.Background(), caller(user), JoinBody{RoomID: id})
	if r.Status != 200 {
		h.t.Fatalf("join: %d %v", r.Status, r.Body)
	}
	return js(h.t, r)["token"].(string)
}

func (h *harness) poll(user, id, token string, cursor *int64) []M {
	h.t.Helper()
	r := h.e.Events(context.Background(), caller(user), id, token, *cursor, 0)
	if r.Status != 200 {
		h.t.Fatalf("events: %d %v", r.Status, r.Body)
	}
	out := js(h.t, r)
	*cursor = int64(out["cursor"].(float64))
	list := []M{}
	for _, m := range out["messages"].([]any) {
		list = append(list, m.(M))
	}
	return list
}

func (h *harness) send(user, id, token string, msgs ...Inbound) {
	h.t.Helper()
	if r := h.e.Messages(caller(user), id, token, MessagesBody{Messages: msgs}); r.Status != 200 {
		h.t.Fatalf("messages: %d %v", r.Status, r.Body)
	}
}

func types(msgs []M) []string {
	out := []string{}
	for _, m := range msgs {
		out = append(out, m["type"].(string))
	}
	return out
}

func lastCommand(msgs []M) M {
	var c M
	for _, m := range msgs {
		if m["type"] == "transport_command" {
			c = m["command"].(M)
		}
	}
	return c
}

func at(h *harness, ms int) string { return Time(h.now.Add(time.Duration(ms) * time.Millisecond)) }

func ptr[T any](v T) *T { return &v }

func TestCreateAnswersTheRoomAndTheFirstPollHasTheSnapshotAndChat(t *testing.T) {
	h := newHarness(t)
	id, token := h.create("maya", CreateBody{ContentID: "movie1"})
	var cursor int64
	msgs := h.poll("maya", id, token, &cursor)
	if got := types(msgs); strings.Join(got, ",") != "snapshot,chat_history" {
		t.Fatalf("first poll = %v", got)
	}
	room := msgs[0]["room"].(M)
	if room["self_role"] != "host" || room["content_title"] != "Avatar" || room["selected_file_id"] != nil {
		t.Errorf("room = %v", room)
	}
	if code := room["code"].(string); NormalizeCode(code) != code || room["invite_path"] != "/Moonfin/Party/"+code {
		t.Errorf("code %q, invite %v", code, room["invite_path"])
	}
	if len(h.poll("maya", id, token, &cursor)) != 0 {
		t.Error("a second poll should be empty")
	}
}

func TestJoinTakesTheCodeAnyWayAndRefusesOthersTokens(t *testing.T) {
	h := newHarness(t)
	id, token := h.create("maya", CreateBody{})
	code := js(t, h.e.AdminRooms())["rooms"].([]any)[0].(M)["code"].(string)
	r := h.e.Join(context.Background(), caller("jordan"), JoinBody{Code: strings.ToLower(code[:3] + " - " + code[3:])})
	if r.Status != 200 || js(t, r)["room"].(M)["self_role"] != "guest" {
		t.Fatalf("join by code: %d %v", r.Status, r.Body)
	}
	if r := h.e.Stop(caller("jordan"), id, token); r.Code() != "not_allowed" {
		t.Errorf("foreign token = %v", r.Body)
	}
	if r := h.e.Join(context.Background(), caller("sam"), JoinBody{Code: "ZZZZZZ"}); r.Code() != "not_found" {
		t.Errorf("unknown code = %v", r.Body)
	}
}

func TestPinsAreAskedForAndWrongOnesAreCappedPerMinute(t *testing.T) {
	h := newHarness(t)
	id, _ := h.create("maya", CreateBody{Pin: ptr("4321")})
	join := func(pin *string) Result {
		return h.e.Join(context.Background(), caller("jordan"), JoinBody{RoomID: id, Pin: pin})
	}
	if r := join(nil); r.Code() != "pin_required" {
		t.Fatalf("no pin = %v", r.Body)
	}
	for range MaxPinFailures {
		if r := join(ptr("0000")); r.Code() != "wrong_pin" {
			t.Fatalf("wrong pin = %v", r.Body)
		}
	}
	r := join(ptr("4321"))
	if r.Code() != "too_many_attempts" || r.Status != 429 || r.RetryAfter < 1 || r.RetryAfter > 60 {
		t.Fatalf("locked = %d %v", r.Status, r.Body)
	}
	h.now = h.now.Add(time.Minute)
	if r := join(ptr("4321")); r.Status != 200 {
		t.Errorf("after a minute = %v", r.Body)
	}
	if r := h.e.Create(context.Background(), caller("sam"), CreateBody{Pin: ptr("12a4")}); r.Code() != "bad_request" {
		t.Errorf("bad pin = %v", r.Body)
	}
}

func TestAFullRoomRefusesMoreMembers(t *testing.T) {
	h := newHarness(t)
	h.settings.MaxMembers = 2
	id, _ := h.create("maya", CreateBody{})
	h.join("jordan", id)
	if r := h.e.Join(context.Background(), caller("sam"), JoinBody{RoomID: id}); r.Code() != "full" || r.Status != 409 {
		t.Errorf("full = %d %v", r.Status, r.Body)
	}
}

func TestRoomsOnlyShowToUsersWhoCanPlayTheItem(t *testing.T) {
	h := newHarness(t)
	h.host.blocked[[2]string{"jordan", "movie1"}] = true
	movieRoom, _ := h.create("maya", CreateBody{ContentID: "movie1"})
	emptyRoom, _ := h.create("sam", CreateBody{})
	rooms := js(t, h.e.Rooms(context.Background(), caller("jordan")))["rooms"].([]any)
	if len(rooms) != 1 || rooms[0].(M)["room_id"] != emptyRoom {
		t.Errorf("jordan sees %v", rooms)
	}
	if _, hasCode := rooms[0].(M)["code"]; hasCode {
		t.Error("the summary has a code")
	}
	if r := h.e.Join(context.Background(), caller("jordan"), JoinBody{RoomID: movieRoom}); r.Code() != "no_access" {
		t.Errorf("join = %v", r.Body)
	}
}

func TestStagingSomethingAMemberCantPlayLeavesThemNotWatching(t *testing.T) {
	h := newHarness(t)
	h.host.blocked[[2]string{"jordan", "movie1"}] = true
	id, token := h.create("maya", CreateBody{})
	jordan := h.join("jordan", id)
	var cursor int64
	h.poll("jordan", id, jordan, &cursor)
	if r := h.e.Stage(context.Background(), caller("maya"), id, token, RoomBody{ContentID: "movie1"}); r.Status != 200 {
		t.Fatalf("stage = %v", r.Body)
	}
	msgs := h.poll("jordan", id, jordan, &cursor)
	var noticeKind, status string
	for _, m := range msgs {
		if m["type"] == "notice" {
			noticeKind = m["kind"].(string)
		}
		if m["type"] == "snapshot" {
			for _, mem := range m["room"].(M)["members"].([]any) {
				if mem.(M)["is_self"] == true {
					status = mem.(M)["status"].(string)
				}
			}
		}
	}
	if noticeKind != "no_access" || status != "not_watching" {
		t.Errorf("notice %q, status %q", noticeKind, status)
	}
}

// playing makes a room that has started and counted down.
func playing(h *harness) (string, string, string) {
	id, host := h.create("maya", CreateBody{ContentID: "movie1"})
	guest := h.join("jordan", id)
	h.e.Start(caller("maya"), id, host, RoomBody{})
	h.now = h.now.Add(11 * time.Second)
	h.e.Sweep()
	h.now = h.now.Add(5 * time.Second)
	return id, host, guest
}

func TestGuestsNeedThePolicyToControlPlayback(t *testing.T) {
	h := newHarness(t)
	id, host, guest := playing(h)
	var cursor int64
	h.poll("jordan", id, guest, &cursor)
	h.send("jordan", id, guest, Inbound{Type: "transport_request", Action: "pause", PositionSeconds: ptr(10.0)})
	if msgs := h.poll("jordan", id, guest, &cursor); lastCommand(msgs) != nil || msgs[0]["code"] != "not_allowed" {
		t.Fatalf("host_only = %v", msgs)
	}
	h.e.Policy(caller("maya"), id, host, RoomBody{GuestControlPolicy: "guests_can_pause"})
	h.send("jordan", id, guest, Inbound{Type: "transport_request", Action: "seek", PositionSeconds: ptr(50.0)},
		Inbound{Type: "transport_request", Action: "pause", PositionSeconds: ptr(12.0)})
	msgs := h.poll("jordan", id, guest, &cursor)
	c := lastCommand(msgs)
	if c == nil || c["action"] != "pause" || c["by_display_name"] != "jordan" || c["execute_at"] != at(h, 300) {
		t.Errorf("pause = %v", c)
	}
	errors := 0
	for _, m := range msgs {
		if m["type"] == "error" {
			errors++
		}
	}
	if errors != 1 {
		t.Errorf("errors = %d, want only the seek refused", errors)
	}
	h.send("maya", id, host, Inbound{Type: "transport_request", Action: "play", PositionSeconds: ptr(12.0)})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c["execute_at"] != at(h, 1000) || c["playback_state"] != "playing" {
		t.Errorf("play from paused = %v", c)
	}
}

func TestStartWaitsForEveryReadyPlayerThenCountsDown(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{ContentID: "movie1"})
	guest := h.join("jordan", id)
	var cursor int64
	h.poll("jordan", id, guest, &cursor)
	h.e.Start(caller("maya"), id, host, RoomBody{PositionSeconds: ptr(30.0)})
	wait := lastCommand(h.poll("jordan", id, guest, &cursor))
	if wait["playback_state"] != "waiting" || wait["position_seconds"] != 30.0 {
		t.Fatalf("wait = %v", wait)
	}
	waitID := wait["command_id"].(string)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), CommandID: &waitID, IsReady: ptr(true)})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c != nil {
		t.Fatalf("played before everyone was ready: %v", c)
	}
	stale := "stale"
	h.send("jordan", id, guest, Inbound{Type: "state_report", SessionID: ptr("player"), CommandID: &stale, IsReady: ptr(true)})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c != nil {
		t.Fatalf("a ready for an older command counted: %v", c)
	}
	h.now = h.now.Add(2 * time.Second)
	h.send("jordan", id, guest, Inbound{Type: "state_report", SessionID: ptr("player"), CommandID: &waitID, IsReady: ptr(true)})
	play := lastCommand(h.poll("jordan", id, guest, &cursor))
	if play["action"] != "play" || play["countdown"] != true || play["execute_at"] != at(h, 3000) {
		t.Errorf("play = %v", play)
	}
}

func TestTheReadyWaitGivesUpAfterTenSeconds(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{ContentID: "movie1"})
	h.join("jordan", id)
	h.e.Start(caller("maya"), id, host, RoomBody{})
	var cursor int64
	h.poll("maya", id, host, &cursor)
	h.now = h.now.Add(9 * time.Second)
	h.e.Sweep()
	if c := lastCommand(h.poll("maya", id, host, &cursor)); c != nil {
		t.Fatalf("too early: %v", c)
	}
	h.now = h.now.Add(time.Second)
	h.e.Sweep()
	if c := lastCommand(h.poll("maya", id, host, &cursor)); c == nil || c["countdown"] != true {
		t.Errorf("after ten seconds = %v", c)
	}
}

func TestAHostWhoBuffersPausesTheRoomUntilTheirPlayerRecovers(t *testing.T) {
	h := newHarness(t)
	id, host, guest := playing(h)
	var cursor int64
	h.poll("jordan", id, guest, &cursor)
	h.send("maya", id, host, Inbound{Type: "buffering", PositionSeconds: ptr(40.0)})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c["action"] != "pause" || c["playback_state"] != "waiting" {
		t.Fatalf("hold = %v", c)
	}
	h.send("jordan", id, guest, Inbound{Type: "state_report", SessionID: ptr("player"), IsReady: ptr(true), PositionSeconds: ptr(40.0)})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c != nil {
		t.Fatalf("a guest ended the host's wait: %v", c)
	}
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), IsReady: ptr(true), PositionSeconds: ptr(41.0)})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c["action"] != "play" || c["countdown"] != false || c["position_seconds"] != 41.0 {
		t.Errorf("resume = %v", c)
	}
}

func TestChatIsCleanedCappedAndRateLimited(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{})
	var cursor int64
	h.poll("maya", id, host, &cursor)
	h.send("maya", id, host, Inbound{Type: "chat", Text: "  hi\a there\n "}, Inbound{Type: "chat", Text: strings.Repeat("x", 900)})
	for range 4 {
		h.send("maya", id, host, Inbound{Type: "chat", Text: "spam"})
	}
	texts, limited := []string{}, 0
	for _, m := range h.poll("maya", id, host, &cursor) {
		switch m["type"] {
		case "chat":
			texts = append(texts, m["message"].(M)["text"].(string))
		case "error":
			if m["code"] == "rate_limited" {
				limited++
			}
		}
	}
	if len(texts) != 5 || texts[0] != "hi there" || len(texts[1]) != ChatMaxLength || limited != 1 {
		t.Errorf("texts %d (%q), limited %d", len(texts), texts[0], limited)
	}
	if got := cleanChat(strings.Repeat("a", 499) + "😀"); len([]rune(got)) != 499 {
		t.Errorf("a cut split an emoji: %d runes", len([]rune(got)))
	}
}

func TestReactionsAreCheckedAndPingAnswersInThePost(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{})
	var cursor int64
	h.poll("maya", id, host, &cursor)
	h.send("maya", id, host, Inbound{Type: "reaction", Reaction: "fire"}, Inbound{Type: "reaction", Reaction: "thumbsup"})
	if got := strings.Join(types(h.poll("maya", id, host, &cursor)), ","); got != "reaction,error" {
		t.Errorf("reactions = %s", got)
	}
	r := js(t, h.e.Messages(caller("maya"), id, host, MessagesBody{Messages: []Inbound{{Type: "ping", ClientSentAt: "2026-11-09T20:59:59.900Z"}}}))
	pong := r["messages"].([]any)[0].(M)
	if pong["type"] != "pong" || pong["client_sent_at"] != "2026-11-09T20:59:59.900Z" || pong["server_received_at"] != at(h, 0) {
		t.Errorf("pong = %v", pong)
	}
	if len(h.poll("maya", id, host, &cursor)) != 0 {
		t.Error("the pong was queued")
	}
}

func TestEventsHoldsUntilSomethingHappensAndTimesOutEmpty(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{})
	var cursor int64
	h.poll("maya", id, host, &cursor)
	done := make(chan Result)
	go func() { done <- h.e.Events(context.Background(), caller("maya"), id, host, cursor, 25) }()
	select {
	case <-done:
		t.Fatal("answered before anything happened")
	case <-time.After(50 * time.Millisecond):
	}
	h.send("maya", id, host, Inbound{Type: "reaction", Reaction: "love"})
	select {
	case r := <-done:
		if msgs := js(t, r)["messages"].([]any); msgs[0].(M)["type"] != "reaction" {
			t.Errorf("woke with %v", msgs)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("didn't wake")
	}
	h.poll("maya", id, host, &cursor)
	start := time.Now()
	r := js(t, h.e.Events(context.Background(), caller("maya"), id, host, cursor, 1))
	if len(r["messages"].([]any)) != 0 || time.Since(start) < 900*time.Millisecond {
		t.Errorf("timeout = %v after %v", r, time.Since(start))
	}
}

func TestACursorOlderThanTheRingGetsAReset(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{})
	var cursor int64
	h.poll("maya", id, host, &cursor)
	for range RingSize + 5 {
		h.now = h.now.Add(time.Second)
		h.send("maya", id, host, Inbound{Type: "reaction", Reaction: "wow"})
	}
	r := js(t, h.e.Events(context.Background(), caller("maya"), id, host, cursor, 0))
	if r["reset"] != true || r["messages"].([]any)[0].(M)["type"] != "snapshot" {
		t.Errorf("reset = %v", r["reset"])
	}
}

func TestQuietMembersDisconnectThenLeaveAndTheHostHandsOver(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{})
	h.join("jordan", id)
	h.now = h.now.Add(time.Second)
	sam := h.join("sam", id)
	var cursor int64
	samID := h.poll("sam", id, sam, &cursor)[0]["room"].(M)["self_member_id"]
	h.now = h.now.Add(36 * time.Second)
	h.poll("sam", id, sam, &cursor)
	h.e.Sweep()
	msgs := h.poll("sam", id, sam, &cursor)
	if msgs[len(msgs)-1]["room"].(M)["members"].([]any)[0].(M)["connected"] != false {
		t.Error("the quiet host still shows connected")
	}
	h.now = h.now.Add(90 * time.Second)
	h.poll("sam", id, sam, &cursor)
	h.e.Sweep()
	handed := false
	for _, m := range h.poll("sam", id, sam, &cursor) {
		handed = handed || (m["kind"] == "host_changed" && m["member_id"] == samID)
	}
	if !handed {
		t.Error("the room didn't go to sam")
	}
	if r := h.e.Stop(caller("maya"), id, host); r.Code() != "not_found" {
		t.Errorf("removed host = %v", r.Body)
	}
}

func TestARemovedMemberHearsWhyThenTheRoomIsGoneForThem(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{})
	jordan := h.join("jordan", id)
	var cursor int64
	jordanID := h.poll("jordan", id, jordan, &cursor)[0]["room"].(M)["self_member_id"].(string)
	if r := h.e.Remove(caller("maya"), id, host, RoomBody{MemberID: jordanID}); r.Status != 204 {
		t.Fatalf("remove = %v", r.Body)
	}
	r := h.e.Events(context.Background(), caller("jordan"), id, jordan, cursor, 25)
	found := false
	for _, m := range js(t, r)["messages"].([]any) {
		found = found || (m.(M)["type"] == "removed" && m.(M)["reason"] == "removed_by_host")
	}
	if !found {
		t.Errorf("no removed message in %v", r.Body)
	}
	next := int64(js(t, r)["cursor"].(float64))
	if r := h.e.Events(context.Background(), caller("jordan"), id, jordan, next, 25); r.Code() != "not_found" {
		t.Errorf("after removed = %v", r.Body)
	}
}

func TestCreatingClosesTheOtherRoomAndARetryGetsTheSameRoom(t *testing.T) {
	h := newHarness(t)
	first, firstToken := h.create("maya", CreateBody{})
	roomID := newUUID()
	second, token := h.create("maya", CreateBody{RoomID: roomID})
	retry, retryToken := h.create("maya", CreateBody{RoomID: strings.ToUpper(roomID)})
	if second != roomID || retry != second || retryToken != token {
		t.Errorf("retry gave %s %s", retry, retryToken)
	}
	if r := h.e.Stop(caller("maya"), first, firstToken); r.Code() != "not_found" {
		t.Errorf("first room = %v", r.Body)
	}
	if r := h.e.Create(context.Background(), caller("jordan"), CreateBody{RoomID: roomID}); r.Code() != "bad_request" {
		t.Errorf("taken id = %v", r.Body)
	}
}

func TestVotingKeepsSuggestionsInVoteOrderAndStagingEndsIt(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	id, host := h.create("maya", CreateBody{})
	jordan := h.join("jordan", id)
	h.e.Voting(caller("maya"), id, host, RoomBody{Voting: ptr(true)})
	h.e.Suggest(ctx, caller("maya"), id, host, RoomBody{ContentID: "movie1"})
	h.now = h.now.Add(time.Second)
	h.e.Suggest(ctx, caller("maya"), id, host, RoomBody{ContentID: "kids1"})
	if r := h.e.Suggest(ctx, caller("jordan"), id, jordan, RoomBody{ContentID: "kids1"}); r.Code() != "not_host" {
		t.Errorf("guest suggest = %v", r.Body)
	}
	list := js(t, h.e.Suggestions(caller("jordan"), id, jordan))["suggestions"].([]any)
	kids := list[1].(M)
	if kids["subtitle"] != "Bluey · S2:E3" || list[0].(M)["subtitle"] != "2022" {
		t.Errorf("subtitles = %v, %v", kids["subtitle"], list[0].(M)["subtitle"])
	}
	h.e.Vote(caller("jordan"), id, jordan, kids["id"].(string), RoomBody{Voted: ptr(true)})
	mine := js(t, h.e.Suggestions(caller("jordan"), id, jordan))["suggestions"].([]any)[0].(M)
	if mine["content_id"] != "kids1" || mine["voted_by_me"] != true || mine["vote_count"] != 1.0 {
		t.Errorf("after the vote = %v", mine)
	}
	room := js(t, h.e.Stage(ctx, caller("maya"), id, host, RoomBody{ContentID: "kids1"}))["room"].(M)
	if room["selection_mode"] != "host_pick" || len(js(t, h.e.Suggestions(caller("maya"), id, host))["suggestions"].([]any)) != 0 {
		t.Errorf("stage left voting on: %v", room["selection_mode"])
	}
}

func TestRoutesParseBodiesAndAnswerDisabled(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	handle := func(method, path, token, body string, q url.Values) Result {
		return h.e.Handle(ctx, method, path, caller("maya"), token, []byte(body), q)
	}
	created := handle("POST", "Rooms", "", `{"content_id":"movie1"}`, nil)
	room := js(t, created)
	id, token := room["room"].(M)["room_id"].(string), room["token"].(string)
	if r := handle("POST", "Join", "", "{nope", nil); r.Code() != "bad_request" {
		t.Errorf("broken json = %v", r.Body)
	}
	if r := handle("POST", "rooms/"+id+"/start", token, `{"position_seconds":12.5}`, nil); r.Status != 200 {
		t.Errorf("start = %v", r.Body)
	}
	events := js(t, handle("GET", "Rooms/"+id+"/Events", token, "", url.Values{"cursor": {"0"}}))
	if events["messages"].([]any)[0].(M)["type"] != "snapshot" {
		t.Errorf("events = %v", events)
	}
	if !Routed("Rooms/x/Events") || Routed("31366623-4606-488f-b5c0-c837c80d0227") {
		t.Error("Routed doesn't tell the routes apart")
	}
	h.settings.Enabled = false
	if r := handle("GET", "Rooms", "", "", nil); r.Code() != "disabled" || r.Status != 403 {
		t.Errorf("disabled = %v", r.Body)
	}
	if c := js(t, handle("GET", "Config", "", "", nil)); c["enabled"] != false || c["version"] != 1.0 {
		t.Errorf("config = %v", c)
	}
	h.e.Sweep()
	if r := h.e.Events(ctx, caller("maya"), id, token, 1, 0); !strings.Contains(string(mustJSON(t, r.Body)), `"reason":"disabled"`) {
		t.Errorf("turning off didn't close the room: %v", r.Body)
	}
}

func TestTheInvitePageUsesTheSharedTemplate(t *testing.T) {
	template, err := os.ReadFile("../../../shared/WatchParty/invite.html")
	if err != nil {
		t.Fatal(err)
	}
	base := PublicBase("http", "10.0.0.2:32500", "https", "party.example.com, proxy")
	page := InvitePage(string(template), "Lake <side>", "abc123", base, "K7Q29M")
	for _, want := range []string{
		`<meta property="og:title" content="Join a watch party">`,
		`content="Open in Moonfin to join. Room code K7Q 29M."`,
		`content="https://party.example.com/Moonfin/Party/preview.png"`,
		`moonfin://party?server=abc123&amp;code=K7Q29M&amp;address=https%3A%2F%2Fparty.example.com`,
		`Lake &lt;side&gt;`,
	} {
		if !strings.Contains(page, want) {
			t.Errorf("page lacks %s", want)
		}
	}
	if strings.Contains(page, "{{") {
		t.Error("a placeholder was left in")
	}
	if NormalizeCode("k7q 29m") != "K7Q29M" || NormalizeCode("K0Q29M") != "" {
		t.Error("NormalizeCode")
	}
}

func TestAReportWithoutASessionOnlySetsTheStatus(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{ContentID: "movie1"})
	guest := h.join("jordan", id)
	var cursor int64
	h.poll("jordan", id, guest, &cursor)
	h.send("jordan", id, guest, Inbound{Type: "state_report", PositionSeconds: ptr(0.0), IsPaused: ptr(true), Status: "ready"})
	h.e.Start(caller("maya"), id, host, RoomBody{})
	wait := lastCommand(h.poll("jordan", id, guest, &cursor))["command_id"].(string)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), CommandID: &wait, IsReady: ptr(true)})
	h.send("jordan", id, guest, Inbound{Type: "state_report", CommandID: &wait, IsReady: ptr(true), Status: "joining"})
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c != nil {
		t.Fatalf("a status report counted as ready: %v", c)
	}
	h.now = h.now.Add(10 * time.Second)
	h.e.Sweep()
	h.now = h.now.Add(5 * time.Second)
	h.send("maya", id, host, Inbound{Type: "state_report", PositionSeconds: ptr(999.0), IsPaused: ptr(false), Status: "in_sync"})
	h.send("jordan", id, guest, Inbound{Type: "state_report", PositionSeconds: ptr(0.0), IsPaused: ptr(true), Status: "not_watching"})
	msgs := h.poll("jordan", id, guest, &cursor)
	room := msgs[len(msgs)-1]["room"].(M)
	if status := room["members"].([]any)[1].(M)["status"]; status != "not_watching" || room["anchor_position_seconds"] != 0.0 {
		t.Errorf("status %v, anchor %v", status, room["anchor_position_seconds"])
	}
}

func TestEveryRoomChangeSendsEachMemberTheirOwnSnapshotAndRepeatsSendNone(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	id, host := h.create("maya", CreateBody{})
	guest := h.join("jordan", id)
	var cursor int64
	me := h.poll("jordan", id, guest, &cursor)[0]["room"].(M)["self_member_id"]
	expect := func(what string, change func(), snapshot bool) {
		t.Helper()
		change()
		var found []M
		for _, m := range h.poll("jordan", id, guest, &cursor) {
			if m["type"] == "snapshot" {
				found = append(found, m)
			}
		}
		if snapshot != (len(found) == 1) {
			t.Errorf("%s: %d snapshots", what, len(found))
		} else if snapshot && found[0]["room"].(M)["self_member_id"] != me {
			t.Errorf("%s: the snapshot isn't jordan's own", what)
		}
	}
	var sam string
	expect("a member joins", func() { sam = h.join("sam", id) }, true)
	expect("a status changes", func() { h.send("jordan", id, guest, Inbound{Type: "state_report", Status: "not_watching"}) }, true)
	expect("the same status again", func() { h.send("jordan", id, guest, Inbound{Type: "state_report", Status: "not_watching"}) }, false)
	expect("the policy", func() { h.e.Policy(caller("maya"), id, host, RoomBody{GuestControlPolicy: "guests_can_pause"}) }, true)
	expect("the same policy again", func() { h.e.Policy(caller("maya"), id, host, RoomBody{GuestControlPolicy: "guests_can_pause"}) }, false)
	expect("the PIN", func() { h.e.SetPin(caller("maya"), id, host, RoomBody{Pin: ptr("1234")}) }, true)
	expect("voting", func() { h.e.Voting(caller("maya"), id, host, RoomBody{Voting: ptr(true)}) }, true)
	expect("the selection", func() { h.e.Stage(ctx, caller("maya"), id, host, RoomBody{ContentID: "movie1"}) }, true)
	expect("start", func() { h.e.Start(caller("maya"), id, host, RoomBody{}) }, true)
	expect("stop", func() { h.e.Stop(caller("maya"), id, host) }, true)
	expect("the host", func() { h.e.Promote(caller("maya"), id, host, RoomBody{MemberID: me.(string)}) }, true)
	expect("a member leaves", func() { h.e.Leave(caller("sam"), id, sam) }, true)
	expect("a member drops out", func() { h.now = h.now.Add(36 * time.Second); h.e.Sweep() }, true)
}

func TestAMemberRemovedDuringAPollStillReadsRemovedBeforeNotFound(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	id, host := h.create("maya", CreateBody{})
	jordan := h.join("jordan", id)
	var cursor int64
	me := h.poll("jordan", id, jordan, &cursor)[0]["room"].(M)["self_member_id"].(string)
	done := make(chan Result)
	go func() { done <- h.e.Events(ctx, caller("jordan"), id, jordan, cursor, 5) }()
	time.Sleep(50 * time.Millisecond)
	if r := h.e.Remove(caller("maya"), id, host, RoomBody{MemberID: me}); r.Status != 204 {
		t.Fatalf("remove = %v", r.Body)
	}
	var seen []string
	r := <-done
	for r.Status == 200 && !strings.Contains(strings.Join(seen, ","), "removed") {
		out := js(t, r)
		for _, m := range out["messages"].([]any) {
			seen = append(seen, m.(M)["type"].(string))
		}
		cursor = int64(out["cursor"].(float64))
		r = h.e.Events(ctx, caller("jordan"), id, jordan, cursor, 5)
	}
	if !strings.Contains(strings.Join(seen, ","), "removed") || r.Code() != "not_found" {
		t.Errorf("read %v, then %v", seen, r.Body)
	}
}

func TestTheLastWordIsReadableUntilTheCursorIsPastIt(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	id, host := h.create("maya", CreateBody{})
	jordan := h.join("jordan", id)
	var none int64
	me := h.poll("jordan", id, jordan, &none)[0]["room"].(M)["self_member_id"].(string)
	h.e.Remove(caller("maya"), id, host, RoomBody{MemberID: me})
	fresh := js(t, h.e.Events(ctx, caller("jordan"), id, jordan, 0, 5))
	if msgs := fresh["messages"].([]any); len(msgs) != 1 || msgs[0].(M)["type"] != "removed" {
		t.Fatalf("a fresh call after removal = %v", msgs)
	}
	if r := h.e.Events(ctx, caller("jordan"), id, jordan, int64(fresh["cursor"].(float64)), 5); r.Code() != "not_found" {
		t.Errorf("after reading it = %v", r.Body)
	}

	var cursor int64
	h.poll("maya", id, host, &cursor)
	for range RingSize + 5 {
		h.now = h.now.Add(time.Second)
		h.send("maya", id, host, Inbound{Type: "reaction", Reaction: "wow"})
	}
	h.e.Close(caller("maya"), id, host)
	reset := js(t, h.e.Events(ctx, caller("maya"), id, host, cursor, 5))
	found := false
	for _, m := range reset["messages"].([]any) {
		found = found || (m.(M)["type"] == "room_closed" && m.(M)["reason"] == "ended_by_host")
	}
	if !found {
		t.Errorf("an old cursor missed room_closed: %v", reset)
	}
	if r := h.e.Events(ctx, caller("maya"), id, host, int64(reset["cursor"].(float64)), 5); r.Code() != "not_found" {
		t.Errorf("after room_closed = %v", r.Body)
	}
}

func TestGenerationOnlyMovesOnStartStopAndANewSelection(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	id, host := h.create("maya", CreateBody{})
	guest := h.join("jordan", id)
	generation := func() float64 {
		var c int64
		return h.poll("maya", id, host, &c)[0]["room"].(M)["generation"].(float64)
	}
	start := generation()
	h.e.Policy(caller("maya"), id, host, RoomBody{GuestControlPolicy: "everyone"})
	h.e.SetPin(caller("maya"), id, host, RoomBody{Pin: ptr("1234")})
	h.send("jordan", id, guest, Inbound{Type: "chat", Text: "hi"}, Inbound{Type: "state_report", Status: "not_watching"})
	if g := generation(); g != start {
		t.Fatalf("generation moved to %v on a policy, PIN, chat and status", g)
	}
	h.e.Stage(ctx, caller("maya"), id, host, RoomBody{ContentID: "movie1"})
	h.e.Stage(ctx, caller("maya"), id, host, RoomBody{ContentID: "movie1"})
	if g := generation(); g != start+1 {
		t.Fatalf("after staging one item twice = %v", g)
	}
	started := js(t, h.e.Start(caller("maya"), id, host, RoomBody{}))["room"].(M)["generation"].(float64)
	h.now = h.now.Add(11 * time.Second)
	h.e.Sweep()
	h.now = h.now.Add(5 * time.Second)
	h.send("maya", id, host, Inbound{Type: "transport_request", Action: "pause", PositionSeconds: ptr(20.0)})
	h.send("maya", id, host, Inbound{Type: "buffering", PositionSeconds: ptr(20.0)})
	cursor := int64(1)
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); started != start+2 || generation() != started || c["generation"] != started {
		t.Fatalf("start %v, now %v, last command %v", started, generation(), c["generation"])
	}
	h.e.Stop(caller("maya"), id, host)
	if g := generation(); g != started+1 {
		t.Errorf("after stop = %v", g)
	}
}

func TestARunEndsBackInTheLobbyWithTheSameItemOnceTheItemFinishes(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{ContentID: "kids1"})
	guest := h.join("jordan", id)
	h.e.Start(caller("maya"), id, host, RoomBody{})
	h.now = h.now.Add(10 * time.Second)
	h.e.Sweep()
	var cursor, fresh int64
	generation := h.poll("jordan", id, guest, &cursor)[0]["room"].(M)["generation"].(float64)
	report := func(pos float64) {
		h.now = h.now.Add(time.Second)
		h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), PositionSeconds: &pos, IsPaused: ptr(false)})
		h.e.Sweep()
	}
	// Three seconds of countdown plus 95 of play reach two seconds short of the runtime.
	for i := range 98 {
		report(math.Min(99.9, float64(i-2)))
	}
	if phase := h.poll("jordan", id, guest, &fresh)[0]["room"].(M)["phase"]; phase != "playing" {
		t.Fatalf("ended early: %v", phase)
	}
	for range 6 {
		report(99.9)
	}
	msgs := h.poll("jordan", id, guest, &cursor)
	room := msgs[len(msgs)-1]["room"].(M)
	if room["phase"] != "lobby" || room["playback_state"] != "idle" || room["selected_content_id"] != "kids1" || room["generation"] != generation+1 {
		t.Errorf("after the end = %v %v %v %v", room["phase"], room["playback_state"], room["selected_content_id"], room["generation"])
	}
}

func TestARunWithoutAKnownRuntimeOrWhilePausedNeverEndsOnItsOwn(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{ContentID: "movie1"})
	h.e.Start(caller("maya"), id, host, RoomBody{})
	h.now = h.now.Add(10 * time.Second)
	h.e.Sweep()
	h.now = h.now.Add(5 * time.Hour)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), PositionSeconds: ptr(18000.0), IsPaused: ptr(false)})
	h.e.Sweep()
	h.now = h.now.Add(time.Minute)
	h.e.Sweep()
	var c1 int64
	if phase := h.poll("maya", id, host, &c1)[0]["room"].(M)["phase"]; phase != "playing" {
		t.Errorf("an item with no runtime ended: %v", phase)
	}

	kids, kidsHost := h.create("sam", CreateBody{ContentID: "kids1"})
	h.e.Start(caller("sam"), kids, kidsHost, RoomBody{})
	h.now = h.now.Add(10 * time.Second)
	h.e.Sweep()
	h.now = h.now.Add(50 * time.Second)
	h.send("sam", kids, kidsHost, Inbound{Type: "transport_request", Action: "pause", PositionSeconds: ptr(99.5)})
	for range 30 {
		h.now = h.now.Add(time.Second)
		h.e.Sweep()
	}
	var c2 int64
	if state := h.poll("sam", kids, kidsHost, &c2)[0]["room"].(M)["playback_state"]; state != "paused" {
		t.Errorf("a paused run ended: %v", state)
	}
}

func TestInviteURLsUseTheAddressEachMemberCallsFrom(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	maya := caller("maya")
	maya.PublicBase = "https://plex.example.com"
	room := js(t, h.e.Create(ctx, maya, CreateBody{}))["room"].(M)
	code := room["code"].(string)
	if room["invite_url"] != "https://plex.example.com/Moonfin/Party/"+code {
		t.Errorf("host's invite_url = %v", room["invite_url"])
	}
	jordan := caller("jordan")
	jordan.PublicBase = "http://10.0.0.2:32500"
	joined := js(t, h.e.Join(ctx, jordan, JoinBody{RoomID: room["room_id"].(string)}))["room"].(M)
	if joined["invite_url"] != "http://10.0.0.2:32500/Moonfin/Party/"+code || joined["invite_path"] != "/Moonfin/Party/"+code {
		t.Errorf("guest's invite_url = %v", joined["invite_url"])
	}
	if plain := js(t, h.e.Create(ctx, caller("sam"), CreateBody{}))["room"].(M); plain["invite_url"] != nil {
		t.Errorf("no address gave %v", plain["invite_url"])
	}
}

func anchorOf(h *harness, user, id, token string) float64 {
	var c int64
	return h.poll(user, id, token, &c)[0]["room"].(M)["anchor_position_seconds"].(float64)
}

func TestAHostReportFromBeforeItsOwnSeekLandedDoesNotPullTheRoomBack(t *testing.T) {
	h := newHarness(t)
	id, host, guest := playing(h)
	report := func(pos float64, status string) {
		h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), PositionSeconds: &pos, IsPaused: ptr(false), Status: status})
	}
	report(100, "")
	if a := anchorOf(h, "jordan", id, guest); a != 100 {
		t.Fatalf("a settled host report = %v", a)
	}
	h.send("maya", id, host, Inbound{Type: "transport_request", Action: "seek", PositionSeconds: ptr(200.0)})
	h.now = h.now.Add(600 * time.Millisecond)
	report(100.6, "")
	if a := anchorOf(h, "jordan", id, guest); a != 200 {
		t.Errorf("the host's stale report pulled the room to %v", a)
	}
	h.now = h.now.Add(3 * time.Second)
	report(203.1, "catching_up")
	if a := anchorOf(h, "jordan", id, guest); a != 200 {
		t.Errorf("a catching_up report moved the room to %v", a)
	}
	report(203.2, "")
	if a := anchorOf(h, "jordan", id, guest); a != 203.2 {
		t.Errorf("a settled report after the seek = %v", a)
	}
}

func TestAGuestsPauseKeepsTheGuestsPositionOverTheHostsReports(t *testing.T) {
	h := newHarness(t)
	id, host, guest := playing(h)
	h.e.Policy(caller("maya"), id, host, RoomBody{GuestControlPolicy: "everyone"})
	h.send("jordan", id, guest, Inbound{Type: "transport_request", Action: "pause", PositionSeconds: ptr(575.383)})
	h.now = h.now.Add(3 * time.Second)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), PositionSeconds: ptr(576.291), IsPaused: ptr(true)})
	if a := anchorOf(h, "jordan", id, guest); a != 575.383 {
		t.Errorf("the paused position became %v", a)
	}
}

func TestHostReportsWaitTwoSecondsPastTheCountdownsPlay(t *testing.T) {
	h := newHarness(t)
	id, host := h.create("maya", CreateBody{ContentID: "movie1"})
	h.e.Start(caller("maya"), id, host, RoomBody{})
	cursor := int64(1)
	wait := lastCommand(h.poll("maya", id, host, &cursor))["command_id"].(string)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), CommandID: &wait, IsReady: ptr(true)})
	h.now = h.now.Add(3500 * time.Millisecond)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), PositionSeconds: ptr(25.0), IsPaused: ptr(false)})
	if a := anchorOf(h, "maya", id, host); a != 0 {
		t.Errorf("a report right after the countdown moved the room to %v", a)
	}
	h.now = h.now.Add(2 * time.Second)
	h.send("maya", id, host, Inbound{Type: "state_report", SessionID: ptr("player"), PositionSeconds: ptr(2.5), IsPaused: ptr(false)})
	if a := anchorOf(h, "maya", id, host); a != 2.5 {
		t.Errorf("a settled report = %v", a)
	}
}

func TestAResumeStartsFromTheRoomsPausedSpotNotTheAskersOwn(t *testing.T) {
	h := newHarness(t)
	id, host, guest := playing(h)
	h.e.Policy(caller("maya"), id, host, RoomBody{GuestControlPolicy: "everyone"})
	h.send("jordan", id, guest, Inbound{Type: "transport_request", Action: "pause", PositionSeconds: ptr(575.383)})
	h.now = h.now.Add(2 * time.Second)
	h.send("maya", id, host, Inbound{Type: "transport_request", Action: "play", PositionSeconds: ptr(576.283)})
	cursor := int64(1)
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c["action"] != "play" || c["position_seconds"] != 575.383 {
		t.Errorf("resume = %v at %v", c["action"], c["position_seconds"])
	}
	h.send("maya", id, host, Inbound{Type: "transport_request", Action: "seek", PositionSeconds: ptr(600.0)})
	cursor = 1
	if c := lastCommand(h.poll("jordan", id, guest, &cursor)); c["position_seconds"] != 600.0 {
		t.Errorf("a seek after = %v", c["position_seconds"])
	}
}
