package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"sync"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// arrFakeCatalog has series tvdb 100 as item s100 with S01E01 as e1, and
// movie tmdb 603 as item m603. Library tv is at /media/tv for everyone, and
// kids at D:\Kids only admins see.
type arrFakeCatalog struct{ fakeCatalog }

func (arrFakeCatalog) FindArrTitles(_ context.Context, _ Identity, series bool, titles []ArrTitle) ([]ArrItem, error) {
	out := make([]ArrItem, len(titles))
	for i, t := range titles {
		switch {
		case series && t.TvdbID == "100":
			out[i] = ArrItem{ItemID: "s100", LibraryIDs: []string{"tv"}, Episodes: map[EpisodeNumber]string{{1, 1}: "e1"}}
		case !series && t.TmdbID == "603":
			out[i] = ArrItem{ItemID: "m603", LibraryIDs: []string{"movies"}}
		}
	}
	return out, nil
}

func (arrFakeCatalog) ArrFolders(_ context.Context, caller Identity, _ bool) ([]ArrFolder, error) {
	return []ArrFolder{
		{LibraryID: "tv", Locations: []string{"/media/tv/"}, Visible: true},
		{LibraryID: "kids", Locations: []string{`D:\Kids\`}, Visible: caller.Admin},
	}, nil
}

const sonarrCalendar = `[
	{"id":1,"seasonNumber":1,"episodeNumber":1,"title":"Pilot","series":{"tvdbId":100,"tmdbId":0,"path":"/tv/Found"}},
	{"id":2,"seasonNumber":1,"episodeNumber":2,"series":{"tvdbId":100,"tmdbId":0,"path":"/tv/Found"}},
	{"id":3,"seasonNumber":2,"episodeNumber":1,"series":{"tvdbId":200,"path":"/Media/TV/Prefix"}},
	{"id":4,"seasonNumber":1,"episodeNumber":1,"series":{"tvdbId":300,"path":"/srv/tv/Elsewhere"}},
	{"id":5,"seasonNumber":1,"episodeNumber":1,"series":{"tvdbId":400,"path":"/srv/kids/Cartoon"}},
	{"id":6,"seasonNumber":1,"episodeNumber":1,"series":{"tvdbId":"not a number","path":"/nowhere/Show"}}
]`

// fakeArr answers Sonarr's and Radarr's calendar for their keys and records
// each query it gets.
type fakeArr struct {
	*httptest.Server
	mu      sync.Mutex
	queries []url.Values
}

func newFakeArr(t *testing.T) *fakeArr {
	f := &fakeArr{}
	f.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		f.queries = append(f.queries, r.URL.Query())
		f.mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.URL.Path != "/api/v3/calendar":
			http.NotFound(w, r)
		case r.Header.Get("X-Api-Key") == "sonarr-key":
			w.Write([]byte(sonarrCalendar))
		case r.Header.Get("X-Api-Key") == "radarr-key":
			w.Write([]byte(`[{"id":9,"title":"The Matrix","tmdbId":603,"path":"/movies/The Matrix (1999)"}]`))
		default:
			w.WriteHeader(http.StatusUnauthorized)
		}
	}))
	t.Cleanup(f.Close)
	return f
}

func (f *fakeArr) hits() []url.Values {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]url.Values(nil), f.queries...)
}

func newCalendarServer(t *testing.T, config map[string]any) (*Server, *fakeArr) {
	t.Helper()
	arr := newFakeArr(t)
	h := testHost(store.NewMemKV())
	h.Catalog = arrFakeCatalog{}
	s := NewServer("1.2.3", h)
	cfg := map[string]any{"SonarrUrl": arr.URL + "/", "SonarrApiKey": " sonarr-key ", "RadarrUrl": arr.URL, "RadarrApiKey": "radarr-key"}
	for k, v := range config {
		cfg[k] = v
	}
	saveConfig(t, s, cfg)
	return s, arr
}

func calendar(t *testing.T, s *Server, c caller, service string, query map[string]any) (int, []map[string]any) {
	t.Helper()
	resp := serve(context.Background(), s, c, "GET", "/Moonfin/Seerr/"+service+"/Calendar", "", query)
	raw := readAll(t, resp)
	var out []map[string]any
	if resp.StatusCode == http.StatusOK {
		if err := json.Unmarshal(raw, &out); err != nil || out == nil {
			t.Fatalf("%s calendar: want a JSON array, got %q", service, raw)
		}
	}
	return resp.StatusCode, out
}

func day(offset int) string {
	return time.Now().UTC().AddDate(0, 0, offset).Format(time.DateOnly)
}

func TestArrCalendarClampsToTheWindow(t *testing.T) {
	s, arr := newCalendarServer(t, map[string]any{"CalendarDaysBack": 3, "CalendarDaysAhead": 10.0})

	calendar(t, s, userMain, "Sonarr", map[string]any{"start": day(-30), "end": day(200)})
	calendar(t, s, userMain, "Sonarr", map[string]any{"start": day(1), "end": day(2)})
	calendar(t, s, userMain, "Radarr", nil)
	got := arr.hits()
	if len(got) != 3 {
		t.Fatalf("arr calls = %v", got)
	}
	for i, want := range [][2]string{{day(-3), day(10)}, {day(1), day(2)}, {day(0), day(10)}} {
		if got[i].Get("start") != want[0] || got[i].Get("end") != want[1] {
			t.Errorf("call %d asked for %s to %s, want %s to %s", i, got[i].Get("start"), got[i].Get("end"), want[0], want[1])
		}
	}

	if code, out := calendar(t, s, userMain, "Sonarr", map[string]any{"start": day(11)}); code != 200 || len(out) != 0 || len(arr.hits()) != 3 {
		t.Fatalf("a range past the window must answer [] without asking Sonarr: %d %v", code, out)
	}
	calendar(t, s, userMain, "Sonarr", map[string]any{"start": "tomorrow", "end": day(2) + "T10:00:00Z"})
	if q := arr.hits()[3]; q.Get("start") != day(0) || q.Get("end") != day(2) {
		t.Fatalf("an unreadable start is today and a timestamp is its date: %v", q)
	}
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["calendarDaysBack"] != 3.0 || ping["calendarDaysAhead"] != 10.0 {
		t.Fatalf("ping window = %v", ping)
	}
}

func TestArrCalendarDefaultWindow(t *testing.T) {
	s, arr := newCalendarServer(t, nil)
	calendar(t, s, userMain, "Sonarr", map[string]any{"start": day(-60)})
	if q := arr.hits()[0]; q.Get("start") != day(-14) || q.Get("end") != day(90) {
		t.Fatalf("default window asked for %s to %s", q.Get("start"), q.Get("end"))
	}
	_, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil)
	if ping["calendarAvailable"] != true || ping["calendarReminders"] != false || ping["calendarDaysBack"] != 14.0 || ping["calendarDaysAhead"] != 90.0 {
		t.Fatalf("ping = %v", ping)
	}
}

func TestArrCalendarAdminsOnly(t *testing.T) {
	s, arr := newCalendarServer(t, map[string]any{"CalendarAdminsOnly": true})
	resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Seerr/Sonarr/Calendar", "", nil)
	if body := string(readAll(t, resp)); resp.StatusCode != http.StatusForbidden || body != `{"error":"The calendar is limited to admins on this server"}`+"\n" {
		t.Fatalf("a user = %d %s", resp.StatusCode, body)
	}
	if len(arr.hits()) != 0 {
		t.Fatal("a refused user must not reach Sonarr")
	}
	if code, _ := calendar(t, s, adminMain, "Sonarr", nil); code != 200 {
		t.Fatalf("the admin = %d", code)
	}
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["calendarAvailable"] != false {
		t.Fatalf("ping must not offer the calendar to a user: %v", ping)
	}
	if _, ping := call(t, s, adminMain, "GET", "/Moonfin/Ping", "", nil); ping["calendarAvailable"] != true {
		t.Fatalf("ping must offer the calendar to the admin: %v", ping)
	}
}

func TestArrCalendarQuery(t *testing.T) {
	s, arr := newCalendarServer(t, nil)
	calendar(t, s, userMain, "Sonarr", nil)
	calendar(t, s, userMain, "Sonarr", map[string]any{"unmonitored": "true"})
	calendar(t, s, userMain, "Radarr", map[string]any{"unmonitored": "true"})
	calendar(t, s, userMain, "Radarr", map[string]any{"unmonitored": "false"})
	got := arr.hits()
	if len(got) != 4 {
		t.Fatalf("arr calls = %v", got)
	}
	for i, q := range got[:2] {
		if q.Get("includeSeries") != "true" || q.Get("includeEpisodeImages") != "true" {
			t.Errorf("Sonarr call %d = %v", i, q)
		}
	}
	if got[0].Has("unmonitored") || got[1].Get("unmonitored") != "true" {
		t.Errorf("unmonitored goes to Sonarr only when asked for: %v %v", got[0], got[1])
	}
	if got[2].Get("unmonitored") != "true" || got[3].Has("unmonitored") || got[2].Has("includeSeries") {
		t.Errorf("Radarr calls = %v %v", got[2], got[3])
	}
}

func TestArrCalendarCache(t *testing.T) {
	s, arr := newCalendarServer(t, nil)
	calendar(t, s, userMain, "Sonarr", nil)
	calendar(t, s, other, "Sonarr", map[string]any{"start": day(0), "end": day(500)})
	if n := len(arr.hits()); n != 1 {
		t.Fatalf("the same clamped window must be fetched once, got %d", n)
	}
	calendar(t, s, userMain, "Sonarr", map[string]any{"unmonitored": "true"})
	calendar(t, s, userMain, "Radarr", nil)
	if n := len(arr.hits()); n != 3 {
		t.Fatalf("unmonitored and Radarr are cached apart, got %d calls", n)
	}

	saveConfig(t, s, map[string]any{"SonarrUrl": arr.URL, "SonarrApiKey": "sonarr-key", "CalendarCacheMinutes": 0})
	calendar(t, s, userMain, "Sonarr", map[string]any{"start": day(1)})
	calendar(t, s, userMain, "Sonarr", map[string]any{"start": day(1)})
	if n := len(arr.hits()); n != 5 {
		t.Fatalf("0 cache minutes must fetch every time, got %d calls", n)
	}
}

func TestArrCacheDropsExpiredAnswersAndStaysBounded(t *testing.T) {
	c := ttlCache{m: map[string]cached{"old": {time.Now().Add(-time.Hour), nil}}}
	for i := range maxArrCached {
		c.setBounded(strconv.Itoa(i), nil, time.Minute, maxArrCached)
	}
	if _, kept := c.m["old"]; kept || len(c.m) != maxArrCached {
		t.Fatalf("an expired answer must go first, then %d fit: %d kept", maxArrCached, len(c.m))
	}
	c.setBounded("one more", nil, time.Minute, maxArrCached)
	if len(c.m) != 1 {
		t.Fatalf("a full cache starts over: %d kept", len(c.m))
	}
}

func TestArrCalendarAnnotationAndVisibility(t *testing.T) {
	s, _ := newCalendarServer(t, nil)
	byID := func(out []map[string]any) map[float64]map[string]any {
		m := map[float64]map[string]any{}
		for _, e := range out {
			m[e["id"].(float64)] = e
		}
		return m
	}
	libs := func(e map[string]any) []any { return e["moonfinLibraryIds"].([]any) }

	code, out := calendar(t, s, userMain, "Sonarr", nil)
	got := byID(out)
	if code != 200 || len(out) != 5 || got[5] != nil {
		t.Fatalf("the user must get every episode but the one only in a library they can't see: %d %v", code, out)
	}
	if e := got[1]; e["moonfinItemId"] != "s100" || e["moonfinEpisodeId"] != "e1" || len(libs(e)) != 1 || libs(e)[0] != "tv" || e["title"] != "Pilot" {
		t.Errorf("found by id = %v", e)
	}
	if e := got[2]; e["moonfinItemId"] != "s100" || e["moonfinEpisodeId"] != nil || len(libs(e)) != 1 {
		t.Errorf("an episode not in the library = %v", e)
	}
	if e := got[3]; e["moonfinItemId"] != nil || e["moonfinEpisodeId"] != nil || len(libs(e)) != 1 || libs(e)[0] != "tv" {
		t.Errorf("placed by path prefix = %v", e)
	}
	if e := got[4]; len(libs(e)) != 1 || libs(e)[0] != "tv" {
		t.Errorf("placed by the parent folder's name = %v", e)
	}
	if e := got[6]; e["moonfinItemId"] != nil || len(libs(e)) != 0 {
		t.Errorf("an unmatched title must stay with no libraries = %v", e)
	}

	_, out = calendar(t, s, adminMain, "Sonarr", nil)
	if e := byID(out)[5]; len(out) != 6 || e == nil || libs(e)[0] != "kids" {
		t.Fatalf("the admin sees the kids library: %v", out)
	}

	_, out = calendar(t, s, userMain, "Radarr", nil)
	if e := out[0]; e["moonfinItemId"] != "m603" || libs(e)[0] != "movies" || e["title"] != "The Matrix" {
		t.Fatalf("radarr = %v", out)
	}
	if _, has := out[0]["moonfinEpisodeId"]; has {
		t.Fatal("Radarr elements have no episode id")
	}
}

func TestArrKeysNeverGoBackToThePage(t *testing.T) {
	s, arr := newCalendarServer(t, nil)
	_, cfg := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	if cfg["SonarrApiKey"] != secretMask || cfg["RadarrApiKey"] != secretMask || cfg["CalendarDaysBack"] != 14.0 || cfg["CalendarCacheMinutes"] != 5.0 {
		t.Fatalf("config = %v", cfg)
	}
	cfg["CalendarDaysAhead"] = 30
	saveConfig(t, s, cfg)
	if code, _ := calendar(t, s, userMain, "Sonarr", nil); code != 200 || arr.hits()[0].Get("end") != day(30) {
		t.Fatalf("sending the mask back must keep the key: %d", code)
	}

	cfg["RadarrApiKey"] = nil
	saveConfig(t, s, cfg)
	if code, _ := calendar(t, s, userMain, "Radarr", nil); code != http.StatusNotFound {
		t.Fatalf("a cleared key turns Radarr off: %d", code)
	}
}

func TestArrCalendarOffWithoutAHostCatalog(t *testing.T) {
	s := newTestServer()
	arr := newFakeArr(t)
	saveConfig(t, s, map[string]any{"SonarrUrl": arr.URL, "SonarrApiKey": "sonarr-key"})
	if _, ping := call(t, s, adminMain, "GET", "/Moonfin/Ping", "", nil); ping["calendarAvailable"] != false {
		t.Fatalf("Silo has no calendar: %v", ping)
	}
	if code, _ := calendar(t, s, adminMain, "Sonarr", nil); code != http.StatusNotFound || len(arr.hits()) != 0 {
		t.Fatalf("Silo's calendar route = %d", code)
	}
}
