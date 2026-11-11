package api

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// reminderCatalog has whatever arrived, and lets the accounts in visible see
// every item.
type reminderCatalog struct {
	fakeCatalog
	arrivals []Arrival
	visible  map[int]bool
}

func (c *reminderCatalog) RecentArrivals(context.Context, time.Time) ([]Arrival, error) {
	return c.arrivals, nil
}

func (c *reminderCatalog) CanSee(_ context.Context, userID int, _ string) (bool, error) {
	return c.visible[userID], nil
}

func reminderServer(kv store.KV, catalog *reminderCatalog) *Server {
	h := testHost(kv)
	h.Catalog, h.Notify = catalog, true
	return NewServer("1.2.3", h)
}

// atTime pins the reminders' clock for the test.
func atTime(t *testing.T, at time.Time) *time.Time {
	t.Helper()
	now := at
	reminderNow = func() time.Time { return now }
	t.Cleanup(func() { reminderNow = time.Now })
	return &now
}

func ptr(n int) *int { return &n }

// asJSON is m as the server reads it from a request body.
func asJSON(m M) M {
	b, _ := json.Marshal(m)
	var out M
	_ = json.Unmarshal(b, &out)
	return out
}

func episodeReminder(t *testing.T, tvdb, season, episode int) calendarReminder {
	t.Helper()
	r, refusal := normalizeReminder(asJSON(M{"kind": "episode", "tvdbid": tvdb, "tmdbid": 1396, "seasonnumber": season, "episodenumber": episode, "title": "Breaking Bad", "subtitle": "Ozymandias"}))
	if refusal != "" {
		t.Fatal(refusal)
	}
	return r
}

func movieReminder(t *testing.T, tmdb any, imdb string) calendarReminder {
	t.Helper()
	r, refusal := normalizeReminder(asJSON(M{"kind": "movie", "tmdbid": tmdb, "imdbid": imdb, "title": "Fight Club"}))
	if refusal != "" {
		t.Fatal(refusal)
	}
	return r
}

func TestReminderKeysAreTheOnesTheAppComputes(t *testing.T) {
	for got, want := range map[string]string{
		episodeReminder(t, 81189, 5, 14).Key:   "episode:81189:5:14",
		episodeReminder(t, 81189, 0, 1).Key:    "episode:81189:0:1",
		movieReminder(t, 550, "tt0137523").Key: "movie:550",
		movieReminder(t, nil, "tt0137523").Key: "movie:imdb:tt0137523",
	} {
		if got != want {
			t.Errorf("key = %q, want %q", got, want)
		}
	}
}

func TestAKeyTheAppSentIsReplaced(t *testing.T) {
	r, _ := normalizeReminder(asJSON(M{"key": "anything", "kind": "Movie", "tmdbid": 550, "title": "Fight Club"}))
	if r.Key != "movie:550" || r.Kind != "movie" {
		t.Fatalf("reminder = %+v", r)
	}
}

func TestAReminderWithoutWhatItsKeyNeedsIsRefused(t *testing.T) {
	for _, in := range []M{
		{"kind": "episode", "seasonnumber": 5, "episodenumber": 14, "title": "Show"},
		{"kind": "episode", "tvdbid": 81189, "episodenumber": 14, "title": "Show"},
		{"kind": "movie", "title": "Movie"},
		{"kind": "movie", "imdbid": "nm123", "title": "Movie"},
		{"kind": "show", "tvdbid": 81189, "seasonnumber": 5, "episodenumber": 14, "title": "Show"},
		{"kind": "movie", "tmdbid": 1, "imdbid": "tt1", "title": ""},
		nil,
	} {
		if r, refusal := normalizeReminder(asJSON(in)); refusal == "" {
			t.Errorf("%v was taken as %+v", in, r)
		}
	}
}

func TestNumbersSentAsStringsAreRead(t *testing.T) {
	var in M
	_ = json.Unmarshal([]byte(`{"kind":"episode","tvdbid":"81189","seasonnumber":"5","episodenumber":14,"title":"Breaking Bad"}`), &in)
	if r, refusal := normalizeReminder(in); r.Key != "episode:81189:5:14" {
		t.Fatalf("key = %q (%s)", r.Key, refusal)
	}
}

func TestRemindersSurviveANewServer(t *testing.T) {
	now := atTime(t, time.Date(2026, 11, 5, 12, 0, 0, 0, time.UTC))
	ctx, kv := context.Background(), store.NewMemKV()
	first := reminderServer(kv, &reminderCatalog{})
	stored, _, err := first.upsertReminder(ctx, 3, episodeReminder(t, 81189, 5, 14))
	if err != nil || !stored.CreatedAt.Equal(*now) {
		t.Fatalf("upsert = %+v %v", stored, err)
	}
	all, _ := reminderServer(kv, &reminderCatalog{}).loadReminders(ctx)
	reread := all["3"]
	if len(reread) != 1 || reread[0].Key != "episode:81189:5:14" || reread[0].Subtitle != "Ozymandias" || *reread[0].TmdbID != 1396 || !reread[0].CreatedAt.Equal(*now) {
		t.Fatalf("reread = %+v", reread)
	}
	if len(all["4"]) != 0 {
		t.Fatal("another account has no reminders")
	}
}

func TestUpsertingTheSameKeyReplacesItAndKeepsTheFirstCreatedAt(t *testing.T) {
	now := atTime(t, time.Date(2026, 11, 5, 12, 0, 0, 0, time.UTC))
	ctx, s := context.Background(), reminderServer(store.NewMemKV(), &reminderCatalog{})
	first, _, _ := s.upsertReminder(ctx, 3, episodeReminder(t, 81189, 5, 14))
	*now = now.AddDate(0, 0, 3)
	changed := episodeReminder(t, 81189, 5, 14)
	changed.Subtitle = "Renamed"
	second, _, _ := s.upsertReminder(ctx, 3, changed)
	all, _ := s.loadReminders(ctx)
	if !second.CreatedAt.Equal(first.CreatedAt) || len(all["3"]) != 1 || all["3"][0].Subtitle != "Renamed" {
		t.Fatalf("second = %+v, stored = %+v", second, all["3"])
	}
}

func TestRemoveIsTrueOnlyForAReminderThatWasThere(t *testing.T) {
	ctx, s := context.Background(), reminderServer(store.NewMemKV(), &reminderCatalog{})
	s.upsertReminder(ctx, 3, movieReminder(t, 550, "tt0137523"))
	if ok, _ := s.removeReminder(ctx, 3, "movie:550"); !ok {
		t.Fatal("the reminder was there")
	}
	if ok, _ := s.removeReminder(ctx, 3, "movie:550"); ok {
		t.Fatal("the reminder was already gone")
	}
	if all, _ := s.loadReminders(ctx); len(all) != 0 {
		t.Fatalf("left %v", all)
	}
}

func TestRemindersOlderThanAYearAreDroppedOnLoad(t *testing.T) {
	now := atTime(t, time.Date(2026, 11, 5, 12, 0, 0, 0, time.UTC))
	ctx, s := context.Background(), reminderServer(store.NewMemKV(), &reminderCatalog{})
	s.upsertReminder(ctx, 3, movieReminder(t, 550, ""))
	*now = now.AddDate(0, 0, 200)
	s.upsertReminder(ctx, 3, episodeReminder(t, 81189, 5, 14))
	*now = now.AddDate(0, 0, 200)
	all, _ := s.loadReminders(ctx)
	if len(all["3"]) != 1 || all["3"][0].Key != "episode:81189:5:14" {
		t.Fatalf("left %+v", all["3"])
	}
}

func TestAUserAtTheLimitCanReplaceButNotAdd(t *testing.T) {
	ctx, s := context.Background(), reminderServer(store.NewMemKV(), &reminderCatalog{})
	for i := 1; i <= maxRemindersPerUser; i++ {
		if _, refusal, err := s.upsertReminder(ctx, 3, movieReminder(t, i, "")); refusal != "" || err != nil {
			t.Fatalf("reminder %d: %s %v", i, refusal, err)
		}
	}
	if _, refusal, _ := s.upsertReminder(ctx, 3, movieReminder(t, 100000, "")); refusal != tooManyReminders {
		t.Fatalf("over the limit = %q", refusal)
	}
	if _, refusal, _ := s.upsertReminder(ctx, 3, movieReminder(t, 1, "")); refusal != "" {
		t.Fatalf("replacing = %q", refusal)
	}
}

func TestEpisodeMatching(t *testing.T) {
	r := episodeReminder(t, 81189, 5, 14)
	for _, tc := range []struct {
		tvdb, tmdb      string
		season, episode *int
		want            bool
	}{
		{"81189", "", ptr(5), ptr(14), true},
		{"", "1396", ptr(5), ptr(14), true},
		{"81189", "", ptr(5), ptr(13), false},
		{"81189", "", ptr(4), ptr(14), false},
		{"12345", "", ptr(5), ptr(14), false},
		{"81189", "", nil, nil, false},
	} {
		a := Arrival{IsEpisode: true, TvdbID: tc.tvdb, TmdbID: tc.tmdb, Season: tc.season, Episode: tc.episode}
		if got := reminderMatches(r, a); got != tc.want {
			t.Errorf("%+v matched %v", tc, got)
		}
	}
}

func TestAMovieMatchesOnTmdbOrImdbButNeverAnEpisode(t *testing.T) {
	if !reminderMatches(movieReminder(t, 550, "tt0137523"), Arrival{TmdbID: "550"}) ||
		!reminderMatches(movieReminder(t, nil, "tt0137523"), Arrival{ImdbID: "TT0137523"}) ||
		reminderMatches(movieReminder(t, 550, "tt0137523"), Arrival{IsEpisode: true, TmdbID: "550", Season: ptr(1), Episode: ptr(1)}) ||
		reminderMatches(movieReminder(t, 550, "tt0137523"), Arrival{TmdbID: "551", ImdbID: "tt1"}) {
		t.Fatal("movie matching is off")
	}
}

func TestTheMessageNamesTheTitleAndEpisode(t *testing.T) {
	if title, body := reminderMessage(episodeReminder(t, 81189, 5, 14)); title != "Breaking Bad is in your library" || body != "S5E14 Ozymandias" {
		t.Fatalf("episode = %q %q", title, body)
	}
	if title, body := reminderMessage(movieReminder(t, 550, "")); title != "Fight Club is in your library" || body != "Ready to watch" {
		t.Fatalf("movie = %q %q", title, body)
	}
	unnamed := episodeReminder(t, 81189, 5, 14)
	unnamed.Subtitle = ""
	if _, body := reminderMessage(unnamed); body != "S5E14" {
		t.Fatalf("unnamed = %q", body)
	}
}

func TestReminderRoutes(t *testing.T) {
	s := reminderServer(store.NewMemKV(), &reminderCatalog{})
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["calendarReminders"] != true {
		t.Fatalf("ping = %v", ping)
	}
	code, got := call(t, s, userMain, "POST", "/Moonfin/Calendar/Reminders", `{"Kind":"episode","tvdbId":81189,"seasonNumber":5,"episodeNumber":14,"title":"Breaking Bad"}`, nil)
	if code != 200 || got["key"] != "episode:81189:5:14" || got["createdAt"] == nil {
		t.Fatalf("save = %d %v", code, got)
	}
	if code, got := call(t, s, userMain, "POST", "/Moonfin/Calendar/Reminders", `{"kind":"movie","title":"No ids"}`, nil); code != 400 || got["error"] == nil {
		t.Fatalf("a reminder without ids = %d %v", code, got)
	}
	if _, got := call(t, s, other, "GET", "/Moonfin/Calendar/Reminders", "", nil); len(got["reminders"].([]any)) != 0 {
		t.Fatalf("another account sees %v", got)
	}
	if code, _ := call(t, s, userMain, "DELETE", "/Moonfin/Calendar/Reminders/episode:81189:5:14", "", nil); code != 204 {
		t.Fatalf("delete = %d", code)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Calendar/Reminders", "", nil); len(got["reminders"].([]any)) != 0 {
		t.Fatalf("after delete = %v", got)
	}

	silo := newTestServer()
	if _, ping := call(t, silo, userMain, "GET", "/Moonfin/Ping", "", nil); ping["calendarReminders"] != false {
		t.Fatalf("Silo ping = %v", ping)
	}
	if code, _ := call(t, silo, userMain, "GET", "/Moonfin/Calendar/Reminders", "", nil); code != 404 {
		t.Fatalf("Silo has no reminders, got %d", code)
	}
	if code, _ := call(t, silo, userMain, "POST", "/Moonfin/Notifications/Register", `{"token":"t"}`, nil); code != 404 {
		t.Fatalf("Silo has no push, got %d", code)
	}
}

// fakeRelay answers like the push relay: every token is delivered except
// "gone", which FCM no longer knows.
type fakeRelay struct {
	*httptest.Server
	mu    sync.Mutex
	sends []map[string]any
}

func newFakeRelay(t *testing.T) *fakeRelay {
	f := &fakeRelay{}
	f.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer relay-key" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		f.mu.Lock()
		f.sends = append(f.sends, body)
		f.mu.Unlock()
		var results []map[string]any
		for _, tok := range body["tokens"].([]any) {
			results = append(results, map[string]any{"token": tok, "ok": tok != "gone", "dead": tok == "gone"})
		}
		json.NewEncoder(w).Encode(map[string]any{"results": results})
	}))
	t.Cleanup(f.Close)
	return f
}

// A reminder fires once, for the account that set it, and only when they can
// see what arrived. It reaches their settings stream and their phones, and a
// phone FCM has forgotten is dropped.
func TestAnArrivalFiresTheReminderOnce(t *testing.T) {
	ctx := context.Background()
	relay := newFakeRelay(t)
	catalog := &reminderCatalog{visible: map[int]bool{3: true}}
	s := reminderServer(store.NewMemKV(), catalog)
	saveConfig(t, s, map[string]any{"PushRelayUrl": relay.URL, "PushRelayAppKey": "relay-key"})
	call(t, s, userMain, "POST", "/Moonfin/Notifications/Register", `{"token":"phone","platform":"android","deviceId":"d1"}`, nil)
	call(t, s, userMain, "POST", "/Moonfin/Notifications/Register", `{"token":"gone","platform":"ios","deviceId":"d2"}`, nil)
	s.upsertReminder(ctx, 3, episodeReminder(t, 81189, 5, 14))
	s.upsertReminder(ctx, 4, episodeReminder(t, 81189, 5, 14))
	s.upsertReminder(ctx, 3, episodeReminder(t, 81189, 5, 15))

	events := make(chan []byte, 4)
	s.notify.streams = map[int]map[chan []byte]struct{}{3: {events: {}}}
	catalog.arrivals = []Arrival{{ItemID: "777", IsEpisode: true, TvdbID: "81189", Season: ptr(5), Episode: ptr(14)}}
	s.fireReminders(ctx, catalog)
	s.fireReminders(ctx, catalog)

	var event map[string]any
	_ = json.Unmarshal(<-events, &event)
	if event["type"] != "seerrNotification" || event["title"] != "Breaking Bad is in your library" || event["body"] != "S5E14 Ozymandias" || event["route"] != "/item/777" {
		t.Fatalf("event = %v", event)
	}
	if len(events) != 0 {
		t.Fatal("the reminder fired twice")
	}
	relay.mu.Lock()
	sends := relay.sends
	relay.mu.Unlock()
	if len(sends) != 1 || sends[0]["route"] != "/item/777" || sends[0]["title"] != "Breaking Bad is in your library" || len(sends[0]["tokens"].([]any)) != 2 {
		t.Fatalf("pushes = %v", sends)
	}
	all, _ := s.loadReminders(ctx)
	if len(all["3"]) != 1 || all["3"][0].Key != "episode:81189:5:15" || len(all["4"]) != 1 {
		t.Fatalf("left = %v", all)
	}
	devices, _ := s.loadDevices(ctx)
	if len(devices["3"]) != 1 || devices["3"][0].Token != "phone" {
		t.Fatalf("devices = %v", devices)
	}
}

func TestDeviceRegistrationReplacesTheSameDevice(t *testing.T) {
	s := reminderServer(store.NewMemKV(), &reminderCatalog{})
	if code, got := call(t, s, userMain, "POST", "/Moonfin/Notifications/Register", `{"platform":"ios"}`, nil); code != 400 || got["error"] != "Token is required" {
		t.Fatalf("no token = %d %v", code, got)
	}
	call(t, s, userMain, "POST", "/Moonfin/Notifications/Register", `{"token":"a","deviceId":"d1"}`, nil)
	call(t, s, userMain, "POST", "/Moonfin/Notifications/Register", `{"token":"b","deviceId":"d1"}`, nil)
	call(t, s, userMain, "POST", "/Moonfin/Notifications/Register", `{"token":"c","deviceId":"d2"}`, nil)
	devices, _ := s.loadDevices(context.Background())
	if tokens := []string{devices["3"][0].Token, devices["3"][1].Token}; len(devices["3"]) != 2 || !slices.Equal(tokens, []string{"b", "c"}) {
		t.Fatalf("devices = %v", devices)
	}
	call(t, s, userMain, "DELETE", "/Moonfin/Notifications/Register", `{"deviceId":"d1"}`, nil)
	if devices, _ := s.loadDevices(context.Background()); len(devices["3"]) != 1 || devices["3"][0].Token != "c" {
		t.Fatalf("after unregister = %v", devices)
	}
}

// The stream carries each event for the account as a data line.
func TestTheSettingsStreamCarriesEvents(t *testing.T) {
	s := reminderServer(store.NewMemKV(), &reminderCatalog{})
	srv := httptest.NewServer(s)
	t.Cleanup(srv.Close)
	req, _ := http.NewRequest("GET", srv.URL+"/Moonfin/Settings/Stream", nil)
	for k, v := range userMain.headers() {
		req.Header.Set(k, v)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 || !strings.HasPrefix(resp.Header.Get("Content-Type"), "text/event-stream") {
		t.Fatalf("stream = %d %s", resp.StatusCode, resp.Header.Get("Content-Type"))
	}
	lines := bufio.NewReader(resp.Body)
	if line, _ := lines.ReadString('\n'); line != ":connected\n" {
		t.Fatalf("first line = %q", line)
	}
	s.notifyUser(other.user, M{"type": "seerrNotification", "title": "not yours"})
	s.notifyUser(userMain.user, M{"type": "seerrNotification", "title": "yours"})
	_, _ = lines.ReadString('\n')
	line, err := lines.ReadString('\n')
	if err != nil && err != io.EOF || line != `data: {"title":"yours","type":"seerrNotification"}`+"\n" {
		t.Fatalf("event line = %q %v", line, err)
	}
}
