// Package watchparty runs watch party rooms in memory for the Plex companion.
// It mirrors shared/WatchParty in the C# plugins: the same routes, room JSON
// and messages, carried over HTTP long-poll. Item lookups ask the server, so
// they run outside the lock.
package watchparty

import (
	"cmp"
	"context"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"math"
	"math/big"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf16"
)

const (
	MaxRooms       = 200
	RingSize       = 200
	ChatHistory    = 50
	ChatMaxLength  = 500
	MaxWaitSeconds = 25
	MaxPinFailures = 5
	MaxSuggestions = 50
	CodeAlphabet   = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"

	DisconnectAfter = 35 * time.Second
	RemoveAfter     = 120 * time.Second
	ReadyWait       = 10 * time.Second
	PinWindow       = time.Minute

	// A run ends once the room's position has stayed within EndSlackSeconds
	// of the item's runtime for EndGrace. Players sitting on the last frame
	// keep reporting a position just short of the runtime, and metadata
	// runtimes are often a little longer than the stream.
	EndSlackSeconds = 2.0
	EndGrace        = 5 * time.Second

	// HostSettle is how long after a command's execute_at the host's reports
	// are left out of the anchor. A report sent before the host's own player
	// has landed the command would pull the room back.
	HostSettle = 2 * time.Second

	// A closed room or a removed member stays this long, so a poll in flight
	// still reads why.
	linger = 2 * time.Minute
)

var (
	reactions = map[string]bool{"love": true, "haha": true, "wow": true, "sad": true, "fire": true, "popcorn": true}
	statuses  = map[string]bool{"joining": true, "ready": true, "in_sync": true, "catching_up": true, "buffering": true, "not_watching": true}
	policies  = map[string]bool{"host_only": true, "guests_can_pause": true, "everyone": true}
)

// Settings are the admin's, read on every call so changes apply at once.
type Settings struct {
	Enabled, Chat, Reactions, Alerts, EveryoneControl bool
	MaxMembers                                        int
}

func DefaultSettings() Settings {
	return Settings{Enabled: true, Chat: true, Reactions: true, Alerts: true, EveryoneControl: true, MaxMembers: 20}
}

// Item is something a user can play, with what the room shows about it.
type Item struct {
	ID, Title, Type string
	SeriesName      *string
	Season, Episode *int
	Year            *int
	// RuntimeSeconds is how long the item runs, or nil when the server
	// doesn't know, which never ends a run on its own.
	RuntimeSeconds *float64
}

// Label is "Arcane · S2:E1" for an episode, the title otherwise.
func (i Item) Label() string {
	if i.SeriesName != nil && *i.SeriesName != "" && i.Season != nil && i.Episode != nil {
		return fmt.Sprintf("%s · S%d:E%d", *i.SeriesName, *i.Season, *i.Episode)
	}
	return i.Title
}

// Caller is the signed-in user and the device they call from.
type Caller struct {
	UserID, DeviceID, Name string
	// Token is the caller's own server token, kept with their membership so
	// what they can play is checked again when the host picks something else.
	// It never goes in a reply or a log.
	Token string
	// PublicBase is the address invite links point at, from the request.
	PublicBase string
}

// Host looks items up as a user. A nil item means it doesn't exist or the
// user can't play it.
type Host interface {
	FindItem(ctx context.Context, caller Caller, contentID string) *Item
}

// Result is an answer: a status and a body to send as JSON, or none for 204.
type Result struct {
	Status     int
	Body       any
	RetryAfter int
}

type M = map[string]any

var errorStatus = map[string]int{
	"disabled": 403, "not_found": 404, "pin_required": 403, "wrong_pin": 403, "too_many_attempts": 429,
	"full": 409, "no_access": 403, "not_host": 403, "not_allowed": 403, "rate_limited": 429, "bad_request": 400,
}

func ok(body any) Result { return Result{Status: 200, Body: body} }

func noContent() Result { return Result{Status: 204} }

func fail(code, message string) Result { return failAfter(code, message, 0) }

func failAfter(code, message string, retryAfter int) Result {
	body := M{"code": code, "message": message}
	if retryAfter > 0 {
		body["retry_after_seconds"] = retryAfter
	}
	return Result{Status: errorStatus[code], Body: body, RetryAfter: retryAfter}
}

func badRequest(message string) Result { return fail("bad_request", message) }

func notAllowed(message string) Result { return fail("not_allowed", message) }

func noAccess() Result { return fail("no_access", "You don't have access to what they're watching.") }

func notFound(message string) Result { return fail("not_found", message) }

// Engine holds every room behind one lock.
type Engine struct {
	host     Host
	settings func() Settings
	now      func() time.Time

	mu    sync.Mutex
	rooms map[string]*room
}

func New(host Host, settings func() Settings, now func() time.Time) *Engine {
	if now == nil {
		now = time.Now
	}
	return &Engine{host: host, settings: settings, now: now, rooms: map[string]*room{}}
}

// Run sweeps every second until ctx ends.
func (e *Engine) Run(ctx context.Context) {
	t := time.NewTicker(time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			e.Sweep()
		}
	}
}

func (e *Engine) Settings() Settings { return e.settings() }

type entry struct {
	seq int64
	// msg is nil for a snapshot, which is rendered per member when read.
	msg M
}

type member struct {
	id, token          string
	caller             Caller
	joinedAt, lastSeen time.Time
	connected          bool
	status             string
	sessionID          *string
	noAccess           bool
	polls              int
	goneAt             time.Time
	private            []entry
	// farewell is the removed message, kept until the member has read past it.
	farewell       *entry
	chat, reaction bucket
	transport      bucket
}

func (m *member) gone() bool { return !m.goneAt.IsZero() }

type suggestion struct {
	id, contentID, title, subtitle string
	createdAt                      time.Time
	votes                          map[string]bool
}

type room struct {
	id, code                   string
	createdAt                  time.Time
	phase, playbackState, mode string
	policy                     string
	revision                   int
	contentID                  *string
	item                       *Item
	isPaused                   bool
	anchor                     float64
	anchorAt                   time.Time
	generation                 int64
	hostID                     string
	pin                        *pinHash
	waitCommandID              string
	waitStarted                time.Time
	waitPosition               float64
	waitHostOnly               bool
	waitBy                     *member
	ready                      map[string]bool
	seq, droppedThrough        int64
	ring                       []entry
	chat                       []chatJSON
	suggestions                []*suggestion
	pinFailures                map[string][]time.Time
	members                    []*member
	closedAt                   time.Time
	// endingSince is when the position first came within reach of the
	// item's end while it plays.
	endingSince time.Time
	// lastCommandAt is the execute_at of the newest transport command.
	lastCommandAt time.Time
	// farewell is the room_closed message, kept until each member has read past it.
	farewell *entry
	signal   chan struct{}
}

func (r *room) closed() bool { return !r.closedAt.IsZero() }

func (r *room) active() []*member {
	out := make([]*member, 0, len(r.members))
	for _, m := range r.members {
		if !m.gone() {
			out = append(out, m)
		}
	}
	return out
}

func (r *room) host() *member {
	for _, m := range r.members {
		if m.id == r.hostID {
			return m
		}
	}
	return nil
}

func (r *room) endWait() {
	r.waitCommandID = ""
	r.waitHostOnly = false
	r.waitBy = nil
	r.ready = map[string]bool{}
}

// wake releases every poll waiting on the room.
func (r *room) wake() {
	close(r.signal)
	r.signal = make(chan struct{})
}

// bucket is a token bucket: perSecond refill up to burst.
type bucket struct {
	perSecond, burst, tokens float64
	at                       time.Time
}

func newBucket(perSecond, burst float64) bucket {
	return bucket{perSecond: perSecond, burst: burst, tokens: burst}
}

func (b *bucket) take(now time.Time) bool {
	if !b.at.IsZero() {
		b.tokens = math.Min(b.burst, b.tokens+math.Max(0, now.Sub(b.at).Seconds())*b.perSecond)
	}
	b.at = now
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}

// pinHash is a room PIN, salted and hashed. Rooms live in memory, and wrong
// guesses are capped per minute.
type pinHash struct{ salt, hash []byte }

const pinIterations = 10_000

func newPin(pin string) *pinHash {
	salt := make([]byte, 16)
	_, _ = rand.Read(salt)
	return &pinHash{salt: salt, hash: derivePin(pin, salt)}
}

func (p *pinHash) matches(guess string) bool {
	return subtle.ConstantTimeCompare(derivePin(guess, p.salt), p.hash) == 1
}

func derivePin(pin string, salt []byte) []byte {
	key, _ := pbkdf2.Key(sha256.New, pin, salt, pinIterations, 32)
	return key
}

func newID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func newUUID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	h := hex.EncodeToString(b)
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:]
}

func newToken() string {
	b := make([]byte, 32)
	_, _ = rand.Read(b)
	return base64.RawURLEncoding.EncodeToString(b)
}

// parseUUID gives the canonical lowercase form, or "" when s isn't a UUID.
func parseUUID(s string) string {
	h := strings.ToLower(strings.ReplaceAll(strings.Trim(s, "{}"), "-", ""))
	if len(h) != 32 {
		return ""
	}
	if _, err := hex.DecodeString(h); err != nil {
		return ""
	}
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:]
}

// Time is ISO 8601 UTC with milliseconds, like 2026-11-09T21:04:05.123Z.
func Time(t time.Time) string { return t.UTC().Format("2006-01-02T15:04:05.000Z") }

// NormalizeCode is the code uppercase with spaces and dashes dropped, or ""
// when it can't be a room code.
func NormalizeCode(input string) string {
	code := strings.ToUpper(strings.NewReplacer(" ", "", "-", "").Replace(input))
	if len(code) != 6 {
		return ""
	}
	for _, c := range code {
		if !strings.ContainsRune(CodeAlphabet, c) {
			return ""
		}
	}
	return code
}

func round(v float64) float64 { return math.Round(v*1000) / 1000 }

func maxMembersOf(s Settings) int { return max(2, min(100, s.MaxMembers)) }

func isPin(pin string) bool {
	if len(pin) < 4 || len(pin) > 8 {
		return false
	}
	for _, c := range pin {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

// cleanChat drops control characters, trims and caps at ChatMaxLength UTF-16
// units like the C# engine, without splitting a pair.
func cleanChat(text string) string {
	kept := strings.TrimSpace(strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return -1
		}
		return r
	}, text))
	units := utf16.Encode([]rune(kept))
	if len(units) <= ChatMaxLength {
		return kept
	}
	cut := ChatMaxLength
	if utf16.IsSurrogate(rune(units[cut-1])) && units[cut-1] < 0xDC00 {
		cut--
	}
	return strings.TrimRightFunc(string(utf16.Decode(units[:cut])), unicode.IsSpace)
}

func notice(kind string, m *member, contentTitle string) M {
	n := M{"type": "notice", "kind": kind, "member_id": m.id, "display_name": m.caller.Name}
	if contentTitle != "" {
		n["content_title"] = contentTitle
	}
	return n
}

func (e *Engine) openRooms() []*room {
	out := []*room{}
	for _, r := range e.rooms {
		if !r.closed() {
			out = append(out, r)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].createdAt.After(out[j].createdAt) })
	return out
}

func (e *Engine) newCode() string {
	for {
		b := make([]byte, 6)
		for i := range b {
			n, _ := rand.Int(rand.Reader, big.NewInt(int64(len(CodeAlphabet))))
			b[i] = CodeAlphabet[n.Int64()]
		}
		code := string(b)
		taken := false
		for _, r := range e.openRooms() {
			taken = taken || r.code == code
		}
		if !taken {
			return code
		}
	}
}

func newMember(c Caller, now time.Time) *member {
	return &member{
		id: newID(), token: newToken(), caller: c, joinedAt: now, lastSeen: now, connected: true, status: "joining",
		chat: newBucket(1, 5), reaction: newBucket(4, 4), transport: newBucket(10, 10),
	}
}

func isCaller(m *member, c Caller) bool {
	return m.caller.UserID == c.UserID && m.caller.DeviceID == c.DeviceID
}

// changed sends every member a fresh snapshot. Generation only moves on
// start, stop and a new selection, so a client can tell a new playback run
// from another snapshot of the same one.
func changed(r *room) { appendMsg(r, nil) }

func appendMsg(r *room, msg M) *entry {
	r.seq++
	e := entry{seq: r.seq, msg: msg}
	r.ring = append(r.ring, e)
	for len(r.ring) > RingSize {
		r.droppedThrough = r.ring[0].seq
		r.ring = r.ring[1:]
	}
	r.wake()
	return &e
}

func private(r *room, m *member, msg M) *entry {
	r.seq++
	e := entry{seq: r.seq, msg: msg}
	m.private = append(m.private, e)
	if len(m.private) > 50 {
		m.private = m.private[1:]
	}
	r.wake()
	return &e
}

func privateError(r *room, m *member, code, text string) {
	private(r, m, M{"type": "error", "code": code, "message": text})
}

// find resolves the room and the caller's membership from the room token.
// lingering lets a closed room or a departed member through, for Events.
func (e *Engine) find(c Caller, roomID, token string, lingering bool) (*room, *member, *Result) {
	r, found := e.rooms[strings.ToLower(roomID)]
	if !found || (r.closed() && !lingering) {
		res := notFound("That watch party has ended.")
		return nil, nil, &res
	}
	var mine *member
	if token != "" {
		for _, m := range r.members {
			if m.token == token {
				mine = m
			}
		}
	}
	if mine == nil || mine.caller.UserID != c.UserID {
		res := notAllowed("That room token isn't yours.")
		return nil, nil, &res
	}
	if mine.gone() && !lingering {
		res := notFound("You're not in this party anymore.")
		return nil, nil, &res
	}
	return r, mine, nil
}

func (e *Engine) asMember(c Caller, roomID, token string, action func(r *room, me *member, now time.Time) Result) Result {
	e.mu.Lock()
	defer e.mu.Unlock()
	r, me, res := e.find(c, roomID, token, false)
	if res != nil {
		return *res
	}
	now := e.now()
	me.lastSeen = now
	remember(me, c)
	return action(r, me, now)
}

func (e *Engine) asHost(c Caller, roomID, token string, action func(r *room, host *member, now time.Time) Result) Result {
	return e.asMember(c, roomID, token, func(r *room, me *member, now time.Time) Result {
		if me.id != r.hostID {
			return fail("not_host", "Only the host can do that.")
		}
		return action(r, me, now)
	})
}

// hostCheck answers whether the call may go on as the room's host, before a
// lookup made outside the lock.
func (e *Engine) hostCheck(c Caller, roomID, token string) *Result {
	res := e.asHost(c, roomID, token, func(*room, *member, time.Time) Result { return noContent() })
	if res.Status != 204 {
		return &res
	}
	return nil
}

func (e *Engine) access(r *room, me *member) Result {
	return ok(M{"room": e.render(r, me), "token": me.token})
}

func (e *Engine) roomOf(r *room, me *member) Result { return ok(M{"room": e.render(r, me)}) }

func (e *Engine) checkPolicy(policy string) *Result {
	if !policies[policy] {
		res := badRequest("guest_control_policy is host_only, guests_can_pause or everyone.")
		return &res
	}
	if policy == "everyone" && !e.Settings().EveryoneControl {
		res := notAllowed("This server doesn't let everyone control playback.")
		return &res
	}
	return nil
}

func (e *Engine) Config() Result {
	s := e.Settings()
	return ok(M{"enabled": s.Enabled, "chat": s.Chat, "reactions": s.Reactions, "alerts": s.Alerts,
		"everyone_control_allowed": s.EveryoneControl, "max_members": maxMembersOf(s), "version": 1})
}

func (e *Engine) TimeNow() Result { return ok(M{"server_time": Time(e.now())}) }

// Rooms lists the open rooms, leaving out the ones whose item the caller
// can't play.
func (e *Engine) Rooms(ctx context.Context, c Caller) Result {
	e.mu.Lock()
	wanted := map[string]bool{}
	for _, r := range e.openRooms() {
		if r.contentID != nil {
			wanted[*r.contentID] = false
		}
	}
	e.mu.Unlock()
	for id := range wanted {
		wanted[id] = e.host.FindItem(ctx, c, id) != nil
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	now := e.now()
	list := []summaryJSON{}
	for _, r := range e.openRooms() {
		if r.contentID != nil && !wanted[*r.contentID] {
			continue
		}
		list = append(list, e.summary(r, c, now))
	}
	return ok(M{"rooms": list})
}

func (e *Engine) Create(ctx context.Context, c Caller, body CreateBody) Result {
	roomID := ""
	if body.RoomID != "" {
		if roomID = parseUUID(body.RoomID); roomID == "" {
			return badRequest("room_id has to be a UUID.")
		}
	}
	if body.Pin != nil && !isPin(*body.Pin) {
		return badRequest("A PIN is 4 to 8 digits.")
	}
	policy := body.GuestControlPolicy
	if policy == "" {
		policy = "host_only"
	}
	if res := e.checkPolicy(policy); res != nil {
		return *res
	}
	var item *Item
	if body.ContentID != "" {
		if item = e.host.FindItem(ctx, c, body.ContentID); item == nil {
			return noAccess()
		}
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	now := e.now()
	if existing, found := e.rooms[roomID]; roomID != "" && found && !existing.closed() {
		for _, m := range existing.active() {
			if isCaller(m, c) && existing.host() != nil && existing.host().caller.UserID == c.UserID {
				remember(m, c)
				return e.access(existing, m)
			}
		}
		return badRequest("That room id is taken.")
	}
	for _, other := range e.openRooms() {
		if h := other.host(); h != nil && h.caller.UserID == c.UserID {
			e.close(other, "replaced", now)
		}
	}
	if len(e.openRooms()) >= MaxRooms {
		return fail("full", "This server has too many watch parties right now.")
	}
	if roomID == "" {
		roomID = newUUID()
	}
	r := &room{id: roomID, code: e.newCode(), createdAt: now, phase: "lobby", playbackState: "idle", mode: "host_pick",
		policy: policy, isPaused: true, anchorAt: now, ready: map[string]bool{}, pinFailures: map[string][]time.Time{},
		signal: make(chan struct{})}
	host := newMember(c, now)
	host.status = "ready"
	r.members = append(r.members, host)
	r.hostID = host.id
	if body.Pin != nil {
		r.pin = newPin(*body.Pin)
	}
	e.rooms[r.id] = r
	if item != nil {
		e.selectItem(r, item, map[string]bool{})
	}
	changed(r)
	return e.access(r, host)
}

func (e *Engine) Join(ctx context.Context, c Caller, body JoinBody) Result {
	code := NormalizeCode(body.Code)
	if body.RoomID == "" && body.Code == "" {
		return badRequest("Send a code or a room_id.")
	}
	// The item check asks the server, so it runs between two turns of the
	// lock, and runs again if the host picked something else meanwhile.
	checked := ""
	for attempt := 0; attempt < 3; attempt++ {
		e.mu.Lock()
		now := e.now()
		var r *room
		if body.RoomID != "" {
			r = e.rooms[strings.ToLower(body.RoomID)]
		} else if code != "" {
			for _, open := range e.openRooms() {
				if open.code == code {
					r = open
				}
			}
		}
		if r == nil || r.closed() {
			e.mu.Unlock()
			return notFound("No watch party has that code.")
		}
		for _, m := range r.active() {
			if isCaller(m, c) {
				remember(m, c)
				e.seen(r, m, now)
				res := e.access(r, m)
				e.mu.Unlock()
				return res
			}
		}
		if attempt == 0 && r.pin != nil {
			if res := e.checkPin(r, c, body.Pin, now); res != nil {
				e.mu.Unlock()
				return *res
			}
		}
		if len(r.active()) >= maxMembersOf(e.Settings()) {
			e.mu.Unlock()
			return fail("full", "This party is full.")
		}
		if r.contentID != nil && *r.contentID != checked {
			id := *r.contentID
			e.mu.Unlock()
			if e.host.FindItem(ctx, c, id) == nil {
				return noAccess()
			}
			checked = id
			continue
		}
		m := newMember(c, now)
		r.members = append(r.members, m)
		appendMsg(r, notice("joined", m, ""))
		changed(r)
		res := e.access(r, m)
		e.mu.Unlock()
		return res
	}
	return noAccess()
}

func (e *Engine) checkPin(r *room, c Caller, pin *string, now time.Time) *Result {
	failures := r.pinFailures[c.UserID][:0:0]
	for _, t := range r.pinFailures[c.UserID] {
		if now.Sub(t) < PinWindow {
			failures = append(failures, t)
		}
	}
	r.pinFailures[c.UserID] = failures
	if len(failures) >= MaxPinFailures {
		wait := int(math.Ceil((PinWindow - now.Sub(failures[0])).Seconds()))
		res := failAfter("too_many_attempts", "Too many tries. Wait a minute.", max(1, wait))
		return &res
	}
	if pin == nil || *pin == "" {
		res := fail("pin_required", "This watch party needs a PIN.")
		return &res
	}
	if !r.pin.matches(*pin) {
		r.pinFailures[c.UserID] = append(failures, now)
		res := fail("wrong_pin", "That PIN didn't work.")
		return &res
	}
	return nil
}

// memberAccess asks, outside the lock, which members can play contentID.
func (e *Engine) memberAccess(ctx context.Context, roomID, contentID string) map[string]bool {
	e.mu.Lock()
	callers := map[string]Caller{}
	if r := e.rooms[roomID]; r != nil {
		for _, m := range r.active() {
			callers[m.caller.UserID] = m.caller
		}
	}
	e.mu.Unlock()
	out := map[string]bool{}
	for user, c := range callers {
		out[user] = e.host.FindItem(ctx, c, contentID) != nil
	}
	return out
}

func (e *Engine) Stage(ctx context.Context, c Caller, roomID, token string, body RoomBody) Result {
	if body.ContentID == "" {
		return badRequest("Send a content_id.")
	}
	if res := e.hostCheck(c, roomID, token); res != nil {
		return *res
	}
	item := e.host.FindItem(ctx, c, body.ContentID)
	if item == nil {
		return noAccess()
	}
	canPlay := e.memberAccess(ctx, strings.ToLower(roomID), item.ID)
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		switched := r.phase == "playing" && (r.contentID == nil || *r.contentID != item.ID)
		e.selectItem(r, item, canPlay)
		if switched {
			appendMsg(r, notice("switched", host, item.Label()))
		}
		changed(r)
		return e.roomOf(r, host)
	})
}

// selectItem stages item. canPlay says who can play it, and anyone missing
// from it joined after the check and keeps what they had.
func (e *Engine) selectItem(r *room, item *Item, canPlay map[string]bool) {
	id := item.ID
	if r.contentID == nil || *r.contentID != id {
		r.generation++
	}
	r.contentID = &id
	r.item = item
	if r.mode == "vote" {
		r.mode = "host_pick"
		e.clearSuggestions(r)
	}
	hostUser := ""
	if h := r.host(); h != nil {
		hostUser = h.caller.UserID
	}
	for _, m := range r.active() {
		can, known := canPlay[m.caller.UserID]
		if m.caller.UserID == hostUser {
			can, known = true, true
		}
		switch {
		case !known:
		case !can:
			m.noAccess = true
			m.status = "not_watching"
			private(r, m, notice("no_access", m, item.Label()))
		case m.noAccess:
			m.noAccess = false
			m.status = "ready"
		}
	}
}

func (e *Engine) Start(c Caller, roomID, token string, body RoomBody) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		if r.contentID == nil {
			return badRequest("Pick something to watch first.")
		}
		position := 0.0
		if body.PositionSeconds != nil {
			position = math.Max(0, *body.PositionSeconds)
		}
		r.phase = "playing"
		r.revision++
		r.generation++
		for _, m := range r.active() {
			if !m.noAccess {
				m.status = "joining"
			}
		}
		e.beginWait(r, host, position, false, now)
		return e.roomOf(r, host)
	})
}

func (e *Engine) Stop(c Caller, roomID, token string) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		backToLobby(r, now)
		return e.roomOf(r, host)
	})
}

// backToLobby ends a run, by the host's Stop or when the item finishes, by
// going back to the lobby with the same item staged. Clients that didn't
// stop it take that as finished.
func backToLobby(r *room, now time.Time) {
	r.phase, r.playbackState, r.isPaused = "lobby", "idle", true
	r.revision++
	r.generation++
	r.anchor, r.anchorAt = 0, now
	r.endingSince = time.Time{}
	r.endWait()
	for _, m := range r.active() {
		if !m.noAccess {
			m.status = "ready"
		}
	}
	changed(r)
}

func (e *Engine) Policy(c Caller, roomID, token string, body RoomBody) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		if res := e.checkPolicy(body.GuestControlPolicy); res != nil {
			return *res
		}
		if r.policy != body.GuestControlPolicy {
			r.policy = body.GuestControlPolicy
			appendMsg(r, notice("policy_changed", host, ""))
			changed(r)
		}
		return e.roomOf(r, host)
	})
}

func (e *Engine) SetPin(c Caller, roomID, token string, body RoomBody) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		if body.Pin != nil && !isPin(*body.Pin) {
			return badRequest("A PIN is 4 to 8 digits.")
		}
		r.pin = nil
		if body.Pin != nil {
			r.pin = newPin(*body.Pin)
		}
		r.pinFailures = map[string][]time.Time{}
		changed(r)
		return e.roomOf(r, host)
	})
}

func (e *Engine) Voting(c Caller, roomID, token string, body RoomBody) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		if body.Voting == nil {
			return badRequest("Send voting as true or false.")
		}
		if r.phase != "lobby" {
			return notAllowed("Voting only works in the lobby.")
		}
		mode := "host_pick"
		if *body.Voting {
			mode = "vote"
		}
		if r.mode != mode {
			r.mode = mode
			if *body.Voting {
				if r.contentID != nil {
					r.generation++
				}
				r.contentID, r.item = nil, nil
				for _, m := range r.active() {
					if m.noAccess {
						m.noAccess, m.status = false, "ready"
					}
				}
			} else {
				e.clearSuggestions(r)
			}
			changed(r)
		}
		return e.roomOf(r, host)
	})
}

func (e *Engine) Suggestions(c Caller, roomID, token string) Result {
	return e.asMember(c, roomID, token, func(r *room, me *member, now time.Time) Result {
		return ok(M{"suggestions": suggestionList(r, me)})
	})
}

func (e *Engine) Suggest(ctx context.Context, c Caller, roomID, token string, body RoomBody) Result {
	if res := e.hostCheck(c, roomID, token); res != nil {
		return *res
	}
	if body.ContentID == "" {
		return badRequest("Send a content_id.")
	}
	item := e.host.FindItem(ctx, c, body.ContentID)
	if item == nil {
		return noAccess()
	}
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		if r.mode != "vote" {
			return notAllowed("Turn on voting first.")
		}
		for _, s := range r.suggestions {
			if s.contentID == item.ID {
				return noContent()
			}
		}
		if len(r.suggestions) >= MaxSuggestions {
			return badRequest("This room has as many suggestions as it can hold.")
		}
		subtitle := ""
		if item.Label() != item.Title {
			subtitle = item.Label()
		} else if item.Year != nil {
			subtitle = strconv.Itoa(*item.Year)
		}
		r.suggestions = append(r.suggestions, &suggestion{id: newID(), contentID: item.ID, title: item.Title, subtitle: subtitle,
			createdAt: now, votes: map[string]bool{}})
		e.suggestionsChanged(r)
		return noContent()
	})
}

func (e *Engine) Vote(c Caller, roomID, token, suggestionID string, body RoomBody) Result {
	return e.asMember(c, roomID, token, func(r *room, me *member, now time.Time) Result {
		for _, s := range r.suggestions {
			if s.id != suggestionID {
				continue
			}
			want := body.Voted == nil || *body.Voted
			if s.votes[me.id] != want {
				if want {
					s.votes[me.id] = true
				} else {
					delete(s.votes, me.id)
				}
				e.suggestionsChanged(r)
			}
			return noContent()
		}
		return notFound("That suggestion is gone.")
	})
}

func (e *Engine) RemoveSuggestion(c Caller, roomID, token, suggestionID string) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		for i, s := range r.suggestions {
			if s.id == suggestionID {
				r.suggestions = append(r.suggestions[:i], r.suggestions[i+1:]...)
				e.suggestionsChanged(r)
				break
			}
		}
		return noContent()
	})
}

func activeMember(r *room, id string) *member {
	for _, m := range r.active() {
		if m.id == id {
			return m
		}
	}
	return nil
}

func (e *Engine) Promote(c Caller, roomID, token string, body RoomBody) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		target := activeMember(r, body.MemberID)
		if target == nil {
			return notFound("That person isn't in the party.")
		}
		if target != host {
			r.hostID = target.id
			appendMsg(r, notice("host_changed", target, ""))
			changed(r)
		}
		return e.roomOf(r, host)
	})
}

func (e *Engine) Remove(c Caller, roomID, token string, body RoomBody) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		target := activeMember(r, body.MemberID)
		if target == nil {
			return notFound("That person isn't in the party.")
		}
		if target == host {
			return badRequest("Leave the party instead.")
		}
		e.depart(r, target, "removed_by_host", now)
		return noContent()
	})
}

func (e *Engine) Close(c Caller, roomID, token string) Result {
	return e.asHost(c, roomID, token, func(r *room, host *member, now time.Time) Result {
		e.close(r, "ended_by_host", now)
		return noContent()
	})
}

func (e *Engine) Leave(c Caller, roomID, token string) Result {
	return e.asMember(c, roomID, token, func(r *room, me *member, now time.Time) Result {
		e.depart(r, me, "", now)
		return noContent()
	})
}

// Events holds until there's a message after cursor or wait seconds pass. A
// cursor of 0 answers at once with the room and its chat. Every call marks
// the member seen.
func (e *Engine) Events(ctx context.Context, c Caller, roomID, token string, cursor int64, wait int) Result {
	deadline := e.now().Add(time.Duration(max(0, min(MaxWaitSeconds, wait))) * time.Second)
	var polling *member
	defer func() {
		if polling != nil {
			e.mu.Lock()
			polling.polls--
			polling.lastSeen = e.now()
			e.mu.Unlock()
		}
	}()
	for {
		e.mu.Lock()
		r, me, res := e.find(c, roomID, token, true)
		if res != nil {
			e.mu.Unlock()
			return *res
		}
		if r.closed() || me.gone() {
			res := e.lastWord(r, me, cursor)
			e.mu.Unlock()
			return res
		}
		now := e.now()
		remember(me, c)
		e.seen(r, me, now)
		batch, any := e.batch(r, me, cursor)
		remaining := deadline.Sub(now)
		if any || remaining <= 0 || ctx.Err() != nil {
			e.mu.Unlock()
			return ok(batch)
		}
		if polling == nil {
			polling = me
			me.polls++
		}
		signal := r.signal
		e.mu.Unlock()
		timer := time.NewTimer(remaining)
		select {
		case <-signal:
		case <-timer.C:
			deadline = time.Time{}
		case <-ctx.Done():
			deadline = time.Time{}
		}
		timer.Stop()
	}
}

// lastWord answers for a closed room or a member who's gone: what's left to
// read, always including the room_closed or removed the client's cursor
// hasn't passed yet, and not_found once it has, so a client loop hears why
// and then ends.
func (e *Engine) lastWord(r *room, me *member, cursor int64) Result {
	if cursor > 0 && cursor <= r.seq && cursor >= r.droppedThrough {
		if batch, any := e.batch(r, me, cursor); any {
			return ok(batch)
		}
	} else {
		last := []any{}
		for _, f := range []*entry{me.farewell, r.farewell} {
			if f != nil && f.seq > cursor {
				last = append(last, f.msg)
			}
		}
		if len(last) > 0 {
			return ok(M{"cursor": r.seq, "messages": last})
		}
	}
	return notFound("That watch party has ended.")
}

// Messages applies the client's messages. Only pongs come back in the answer.
func (e *Engine) Messages(c Caller, roomID, token string, body MessagesBody) Result {
	return e.asMember(c, roomID, token, func(r *room, me *member, receivedAt time.Time) Result {
		replies := []M{}
		for i, msg := range body.Messages {
			if i >= 20 || r.closed() || me.gone() {
				break
			}
			if msg.Type == "ping" {
				replies = append(replies, M{"type": "pong", "client_sent_at": msg.ClientSentAt,
					"server_received_at": Time(receivedAt), "server_sent_at": Time(e.now())})
				continue
			}
			e.apply(r, me, msg, e.now())
		}
		return ok(M{"messages": replies})
	})
}

func (e *Engine) apply(r *room, me *member, msg Inbound, now time.Time) {
	switch msg.Type {
	case "transport_request":
		e.transportRequest(r, me, msg, now)
	case "buffering":
		e.setStatus(r, me, "buffering")
		if me.id == r.hostID && r.playbackState == "playing" {
			pos := position(r, now)
			if msg.PositionSeconds != nil {
				pos = *msg.PositionSeconds
			}
			e.beginWait(r, me, math.Max(0, pos), true, now)
		}
	case "state_report":
		e.stateReport(r, me, msg, now)
	case "attach_session":
		var session *string
		if s := msg.SessionID; s != nil && len(*s) > 0 && len(*s) <= 128 {
			session = s
		}
		if !equalPtr(session, me.sessionID) {
			me.sessionID = session
			changed(r)
		}
	case "chat":
		e.chat(r, me, msg.Text, now)
	case "reaction":
		switch {
		case !e.Settings().Reactions:
			privateError(r, me, "not_allowed", "Reactions are turned off on this server.")
		case !reactions[msg.Reaction]:
			privateError(r, me, "bad_request", "That isn't a reaction.")
		case !me.reaction.take(now):
			privateError(r, me, "rate_limited", "Slow down a little.")
		default:
			appendMsg(r, M{"type": "reaction", "member_id": me.id, "display_name": me.caller.Name, "reaction": msg.Reaction})
		}
	}
}

func equalPtr(a, b *string) bool { return (a == nil && b == nil) || (a != nil && b != nil && *a == *b) }

func canSend(r *room, m *member, action string) bool {
	return m.id == r.hostID || r.policy == "everyone" || (r.policy == "guests_can_pause" && action != "seek")
}

func (e *Engine) transportRequest(r *room, me *member, msg Inbound, now time.Time) {
	if !me.transport.take(now) {
		privateError(r, me, "rate_limited", "Slow down a little.")
		return
	}
	action := msg.Action
	if action != "play" && action != "pause" && action != "seek" {
		privateError(r, me, "bad_request", "That isn't a playback action.")
		return
	}
	if r.phase != "playing" || !canSend(r, me, action) {
		privateError(r, me, "not_allowed", "You can't control playback in this party.")
		return
	}
	pos := position(r, now)
	if msg.PositionSeconds != nil {
		pos = *msg.PositionSeconds
	}
	pos = math.Max(0, pos)
	if r.playbackState == "waiting" {
		if action != "seek" || r.waitHostOnly {
			privateError(r, me, "not_allowed", "Wait for everyone's player first.")
			return
		}
		// A seek before the start moves where everyone loads, so readiness starts over.
		by := r.waitBy
		if by == nil {
			by = me
		}
		e.beginWait(r, by, pos, false, now)
		return
	}
	if action == "play" && r.playbackState == "paused" {
		// A resume starts everyone from the paused spot, not from where the asking player stopped.
		pos = r.anchor
	}
	delay := 300 * time.Millisecond
	if action == "play" && r.isPaused {
		delay = time.Second
	}
	executeAt := now.Add(delay)
	if action != "seek" {
		r.isPaused = action == "pause"
	}
	r.playbackState = "playing"
	if r.isPaused {
		r.playbackState = "paused"
	}
	r.anchor, r.anchorAt = pos, executeAt
	changed(r)
	e.command(r, action, pos, executeAt, r.playbackState, me, false)
}

func (e *Engine) stateReport(r *room, me *member, msg Inbound, now time.Time) {
	status := ""
	if statuses[msg.Status] {
		status = msg.Status
	}
	if msg.SessionID == nil || *msg.SessionID == "" {
		// Without a player session it's a status alone, so it never moves the anchor or counts as ready.
		if status != "" {
			e.setStatus(r, me, status)
		}
		return
	}
	if r.playbackState == "waiting" {
		if r.waitHostOnly {
			if me.id == r.hostID && status != "buffering" {
				e.setStatus(r, me, cmp.Or(status, "in_sync"))
				if msg.PositionSeconds != nil {
					r.waitPosition = math.Max(0, *msg.PositionSeconds)
				}
				e.endWait(r, now)
				return
			}
		} else if msg.IsReady != nil && *msg.IsReady && (msg.CommandID == nil || *msg.CommandID == r.waitCommandID) && !me.noAccess {
			r.ready[me.id] = true
			e.setStatus(r, me, cmp.Or(status, "ready"))
			e.checkReady(r, now)
			return
		}
	} else if r.phase == "playing" && msg.PositionSeconds != nil && !now.Before(r.anchorAt) {
		// Nothing is judged before the last command has run, since players are still getting there.
		matches := msg.IsPaused == nil || *msg.IsPaused == r.isPaused
		if me.id == r.hostID && status != "buffering" {
			// The host's reports keep a playing room's clock honest, so a stalled
			// host slows it, but they never override a paused position or a
			// command that's still landing.
			if matches && r.playbackState == "playing" && status != "catching_up" && !now.Before(r.lastCommandAt.Add(HostSettle)) {
				r.anchor, r.anchorAt = math.Max(0, *msg.PositionSeconds), now
			}
			status = cmp.Or(status, "in_sync")
		} else if status == "" {
			status = "catching_up"
			if matches && math.Abs(*msg.PositionSeconds-position(r, now)) <= 2 {
				status = "in_sync"
			}
		}
	}
	if status != "" {
		e.setStatus(r, me, status)
	}
}

func (e *Engine) chat(r *room, me *member, text string, now time.Time) {
	if !e.Settings().Chat {
		privateError(r, me, "not_allowed", "Chat is turned off on this server.")
		return
	}
	clean := cleanChat(text)
	if clean == "" {
		return
	}
	if !me.chat.take(now) {
		privateError(r, me, "rate_limited", "Slow down a little.")
		return
	}
	c := chatJSON{ID: newID(), MemberID: me.id, DisplayName: me.caller.Name, Text: clean, SentAt: Time(now)}
	r.chat = append(r.chat, c)
	if len(r.chat) > ChatHistory {
		r.chat = r.chat[1:]
	}
	appendMsg(r, M{"type": "chat", "message": c})
}

func (e *Engine) beginWait(r *room, by *member, pos float64, hostOnly bool, now time.Time) {
	r.playbackState, r.isPaused = "waiting", true
	r.anchor, r.anchorAt = pos, now
	r.waitPosition, r.waitStarted, r.waitHostOnly, r.waitBy = pos, now, hostOnly, by
	r.ready = map[string]bool{}
	changed(r)
	action := "seek"
	if hostOnly {
		action = "pause"
	}
	r.waitCommandID = e.command(r, action, pos, now.Add(300*time.Millisecond), "waiting", by, false)
}

func (e *Engine) checkReady(r *room, now time.Time) {
	if r.playbackState != "waiting" || r.waitHostOnly {
		return
	}
	for _, m := range r.active() {
		if m.connected && !m.noAccess && !r.ready[m.id] {
			return
		}
	}
	e.endWait(r, now)
}

func (e *Engine) endWait(r *room, now time.Time) {
	countdown := !r.waitHostOnly
	delay := time.Second
	if countdown {
		delay = 3 * time.Second
		for _, m := range r.active() {
			if r.ready[m.id] {
				m.status = "in_sync"
			}
		}
	}
	executeAt := now.Add(delay)
	by := r.waitBy
	r.playbackState, r.isPaused = "playing", false
	r.anchor, r.anchorAt = r.waitPosition, executeAt
	r.endWait()
	changed(r)
	e.command(r, "play", r.anchor, executeAt, "playing", by, countdown)
}

func (e *Engine) command(r *room, action string, pos float64, executeAt time.Time, state string, by *member, countdown bool) string {
	r.lastCommandAt = executeAt
	c := commandJSON{CommandID: newID(), Generation: r.generation, SelectionRevision: r.revision, Action: action,
		PositionSeconds: round(pos), ExecuteAt: Time(executeAt), PlaybackState: state, Countdown: countdown}
	if by != nil {
		c.ByMemberID, c.ByDisplayName = &by.id, &by.caller.Name
	}
	appendMsg(r, M{"type": "transport_command", "command": c})
	return c.CommandID
}

func (e *Engine) depart(r *room, m *member, reason string, now time.Time) {
	m.goneAt = now
	delete(r.ready, m.id)
	if reason != "" {
		m.farewell = private(r, m, M{"type": "removed", "reason": reason})
	}
	appendMsg(r, notice("left", m, ""))
	active := r.active()
	if len(active) == 0 {
		e.close(r, "empty", now)
		return
	}
	if r.hostID == m.id {
		sort.SliceStable(active, func(i, j int) bool { return active[i].joinedAt.Before(active[j].joinedAt) })
		next := active[0]
		for _, a := range active {
			if a.connected {
				next = a
				break
			}
		}
		r.hostID = next.id
		if r.waitBy == m {
			r.waitBy = next
		}
		appendMsg(r, notice("host_changed", next, ""))
	}
	changed(r)
	e.checkReady(r, now)
}

func (e *Engine) close(r *room, reason string, now time.Time) {
	r.closedAt = now
	r.farewell = appendMsg(r, M{"type": "room_closed", "reason": reason})
}

func (e *Engine) setStatus(r *room, m *member, status string) {
	if m.noAccess && status != "not_watching" {
		return
	}
	if m.status != status {
		m.status = status
		changed(r)
	}
}

func (e *Engine) seen(r *room, m *member, now time.Time) {
	m.lastSeen = now
	update := !m.connected
	m.connected = true
	if m.status == "joining" && r.phase == "lobby" && !m.noAccess {
		m.status = "ready"
		update = true
	}
	if update && !r.closed() && !m.gone() {
		changed(r)
	}
}

func position(r *room, now time.Time) float64 {
	if r.playbackState == "playing" && !r.isPaused {
		return r.anchor + math.Max(0, now.Sub(r.anchorAt).Seconds())
	}
	return r.anchor
}

// batch is what the member reads after cursor: the room's messages and their
// own, in order, with one snapshot rendered for them where the last one falls.
func (e *Engine) batch(r *room, me *member, cursor int64) (M, bool) {
	messages := []any{}
	out := M{"cursor": r.seq}
	if cursor <= 0 || cursor > r.seq || cursor < r.droppedThrough {
		if cursor > 0 {
			out["reset"] = true
		}
		messages = append(messages, M{"type": "snapshot", "room": e.render(r, me)},
			M{"type": "chat_history", "messages": append([]chatJSON{}, r.chat...)})
		for _, p := range me.private {
			if p.seq > cursor {
				messages = append(messages, p.msg)
			}
		}
		me.private = nil
		out["messages"] = messages
		return out, true
	}
	kept := me.private[:0]
	for _, p := range me.private {
		if p.seq > cursor {
			kept = append(kept, p)
		}
	}
	me.private = kept
	entries := []entry{}
	for _, en := range r.ring {
		if en.seq > cursor {
			entries = append(entries, en)
		}
	}
	entries = append(entries, me.private...)
	sort.Slice(entries, func(i, j int) bool { return entries[i].seq < entries[j].seq })
	last := -1
	for i, en := range entries {
		if en.msg == nil {
			last = i
		}
	}
	for i, en := range entries {
		if en.msg != nil {
			messages = append(messages, en.msg)
		} else if i == last {
			messages = append(messages, M{"type": "snapshot", "room": e.render(r, me)})
		}
	}
	out["messages"] = messages
	return out, len(messages) > 0
}

func ordered(r *room) []*member {
	active := r.active()
	sort.SliceStable(active, func(i, j int) bool {
		hi, hj := active[i].id == r.hostID, active[j].id == r.hostID
		if hi != hj {
			return hi
		}
		return active[i].joinedAt.Before(active[j].joinedAt)
	})
	return active
}

func (e *Engine) render(r *room, me *member) roomJSON {
	s := e.Settings()
	out := roomJSON{
		RoomID: r.id, Code: r.code, InvitePath: "/Moonfin/Party/" + r.code, Phase: r.phase, PlaybackState: r.playbackState,
		InviteURL:     inviteURL(me.caller.PublicBase, r.code),
		SelectionMode: r.mode, SelectionRevision: r.revision, SelectedContentID: r.contentID, GuestControlPolicy: r.policy,
		IsPaused: r.isPaused, AnchorPositionSeconds: round(r.anchor), AnchorAt: Time(r.anchorAt), Generation: r.generation,
		SelfRole: "guest", SelfCanControlTransport: me.id == r.hostID || r.policy != "host_only", SelfMemberID: me.id,
		AttachedSessionID: me.sessionID, Members: []memberJSON{}, HasPin: r.pin != nil, ChatEnabled: s.Chat,
		ReactionsEnabled: s.Reactions, MaxMembers: maxMembersOf(s), CreatedAt: Time(r.createdAt),
	}
	if me.id == r.hostID {
		out.SelfRole = "host"
	}
	if r.item != nil {
		out.ContentTitle, out.ContentType = &r.item.Title, &r.item.Type
		out.SeriesName, out.Season, out.Episode = r.item.SeriesName, r.item.Season, r.item.Episode
	}
	for _, m := range ordered(r) {
		out.Members = append(out.Members, memberJSON{ProfileID: m.id, DisplayName: m.caller.Name, IsHost: m.id == r.hostID,
			IsSelf: m == me, Connected: m.connected, UserID: m.caller.UserID, Status: m.status})
	}
	return out
}

func inviteURL(base, code string) *string {
	if base == "" {
		return nil
	}
	u := base + "/Moonfin/Party/" + code
	return &u
}

// remember keeps the address the caller last called from, for their invite_url.
func remember(m *member, c Caller) {
	if c.PublicBase != "" {
		m.caller.PublicBase = c.PublicBase
	}
}

func (e *Engine) summary(r *room, c Caller, now time.Time) summaryJSON {
	members := ordered(r)
	out := summaryJSON{RoomID: r.id, MemberCount: len(members), MembersPreview: []string{}, Phase: r.phase,
		PlaybackState: r.playbackState, PositionSeconds: round(position(r, now)), SelectedContentID: r.contentID,
		HasPin: r.pin != nil, GuestControlPolicy: r.policy, SelectionMode: r.mode, CreatedAt: Time(r.createdAt)}
	if h := r.host(); h != nil {
		out.HostName = h.caller.Name
	}
	for i, m := range members {
		if i < 3 {
			out.MembersPreview = append(out.MembersPreview, m.caller.Name)
		}
		out.IsMember = out.IsMember || m.caller.UserID == c.UserID
	}
	if r.item != nil {
		out.ContentTitle, out.ContentType = &r.item.Title, &r.item.Type
		out.SeriesName, out.Season, out.Episode = r.item.SeriesName, r.item.Season, r.item.Episode
	}
	return out
}

func suggestionList(r *room, me *member) []suggestionJSON {
	list := append([]*suggestion{}, r.suggestions...)
	sort.SliceStable(list, func(i, j int) bool {
		if len(list[i].votes) != len(list[j].votes) {
			return len(list[i].votes) > len(list[j].votes)
		}
		if !list[i].createdAt.Equal(list[j].createdAt) {
			return list[i].createdAt.Before(list[j].createdAt)
		}
		return list[i].id < list[j].id
	})
	out := []suggestionJSON{}
	for _, s := range list {
		out = append(out, suggestionJSON{ID: s.id, ContentID: s.contentID, Title: s.title, Subtitle: s.subtitle,
			VoteCount: len(s.votes), VotedByMe: me != nil && s.votes[me.id], CreatedAt: Time(s.createdAt)})
	}
	return out
}

func (e *Engine) suggestionsChanged(r *room) {
	appendMsg(r, M{"type": "suggestions_update", "suggestions": suggestionList(r, nil)})
}

func (e *Engine) clearSuggestions(r *room) {
	if len(r.suggestions) > 0 {
		r.suggestions = nil
		e.suggestionsChanged(r)
	}
}

func (e *Engine) AdminRooms() Result {
	e.mu.Lock()
	defer e.mu.Unlock()
	now := e.now()
	rooms := []adminRoomJSON{}
	for _, r := range e.openRooms() {
		a := adminRoomJSON{RoomID: r.id, Code: r.code, Phase: r.phase, PlaybackState: r.playbackState,
			PositionSeconds: round(position(r, now)), HasPin: r.pin != nil, CreatedAt: Time(r.createdAt), Members: []adminMemberJSON{}}
		if h := r.host(); h != nil {
			a.HostName = h.caller.Name
		}
		if r.item != nil {
			label := r.item.Label()
			a.ContentTitle, a.ContentLabel = &r.item.Title, &label
		}
		for _, m := range ordered(r) {
			a.Members = append(a.Members, adminMemberJSON{MemberID: m.id, UserID: m.caller.UserID, DisplayName: m.caller.Name,
				IsHost: m.id == r.hostID, Connected: m.connected, Status: m.status})
		}
		rooms = append(rooms, a)
	}
	return ok(M{"rooms": rooms})
}

func (e *Engine) AdminClose(roomID string) Result {
	e.mu.Lock()
	defer e.mu.Unlock()
	r := e.rooms[strings.ToLower(roomID)]
	if r == nil || r.closed() {
		return notFound("That watch party has ended.")
	}
	e.close(r, "closed_by_admin", e.now())
	return noContent()
}

func (e *Engine) AdminRemove(roomID string, body RoomBody) Result {
	e.mu.Lock()
	defer e.mu.Unlock()
	r := e.rooms[strings.ToLower(roomID)]
	var target *member
	if r != nil && !r.closed() {
		target = activeMember(r, body.MemberID)
	}
	if target == nil {
		return notFound("That person isn't in a party.")
	}
	e.depart(r, target, "removed_by_admin", e.now())
	return noContent()
}

// Sweep handles liveness, the ready wait's timeout and cleanup. Run calls it
// every second.
func (e *Engine) Sweep() {
	e.mu.Lock()
	defer e.mu.Unlock()
	now := e.now()
	enabled := e.Settings().Enabled
	for id, r := range e.rooms {
		if r.closed() {
			if now.Sub(r.closedAt) > linger {
				delete(e.rooms, id)
			}
			continue
		}
		if !enabled {
			e.close(r, "disabled", now)
			continue
		}
		update := false
		for _, m := range r.active() {
			if m.polls > 0 {
				m.lastSeen = now
			}
			idle := now.Sub(m.lastSeen)
			if idle > RemoveAfter {
				e.depart(r, m, "", now)
			} else if idle > DisconnectAfter && m.connected {
				m.connected = false
				update = true
			}
		}
		kept := r.members[:0]
		for _, m := range r.members {
			if !m.gone() || now.Sub(m.goneAt) <= linger {
				kept = append(kept, m)
			}
		}
		r.members = kept
		if r.closed() {
			continue
		}
		if update {
			changed(r)
			e.checkReady(r, now)
		}
		if r.playbackState == "waiting" && now.Sub(r.waitStarted) >= ReadyWait {
			e.endWait(r, now)
		}
		if r.playbackState == "playing" && r.item != nil && r.item.RuntimeSeconds != nil && *r.item.RuntimeSeconds > 0 &&
			position(r, now) >= *r.item.RuntimeSeconds-EndSlackSeconds {
			if r.endingSince.IsZero() {
				r.endingSince = now
			}
			if now.Sub(r.endingSince) >= EndGrace {
				backToLobby(r, now)
			}
		} else {
			r.endingSince = time.Time{}
		}
	}
}
