package api

import (
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"sync"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

const blockedMovie = `{"id":7,"movieId":3,"title":"Some.Movie.2026.1080p-GRP","status":"completed","trackedDownloadState":"importBlocked",
	"trackedDownloadStatus":"warning","statusMessages":[{"title":"Some.Movie.2026.1080p-GRP","messages":["Caution: Found executable file with extension: '.exe'"]}],
	"size":1000,"sizeleft":0,"timeleft":"00:00:00","downloadId":"ABC","protocol":"torrent","downloadClient":"qBittorrent",
	"quality":{"quality":{"id":7,"name":"Bluray-1080p"}},"outputPath":"/downloads/complete/Some.Movie.2026.1080p-GRP",
	"movie":{"path":"/movies/Some Movie (2026)"},"customFormats":[]}`

// queueArr answers the requests it's told about, by method and path with
// query, and records every request it gets.
type queueArr struct {
	*httptest.Server
	mu       sync.Mutex
	answers  map[string]string
	status   map[string]int
	requests []string
	bodies   []string
}

func newQueueArr(t *testing.T) *queueArr {
	f := &queueArr{answers: map[string]string{}, status: map[string]int{}}
	f.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		key := r.Method + " " + r.URL.RequestURI()
		body, _ := io.ReadAll(r.Body)
		f.mu.Lock()
		f.requests = append(f.requests, key)
		if len(body) > 0 {
			f.bodies = append(f.bodies, string(body))
		}
		answer, ok := f.answers[key]
		status := f.status[key]
		f.mu.Unlock()
		if r.Header.Get("X-Api-Key") != "radarr-key" && r.Header.Get("X-Api-Key") != "sonarr-key" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		if !ok {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		if status != 0 {
			w.WriteHeader(status)
		}
		io.WriteString(w, answer)
	}))
	t.Cleanup(f.Close)
	return f
}

func (f *queueArr) on(key, answer string) *queueArr { return f.onStatus(key, answer, 0) }

func (f *queueArr) onStatus(key, answer string, status int) *queueArr {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.answers[key] = answer
	f.status[key] = status
	return f
}

func (f *queueArr) seen() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.requests...)
}

func newQueueServer(t *testing.T, config map[string]any) (*Server, *queueArr) {
	t.Helper()
	arr := newQueueArr(t)
	s := NewServer("1.2.3", testHost(store.NewMemKV()))
	cfg := map[string]any{"SonarrUrl": arr.URL + "/", "SonarrApiKey": "sonarr-key", "RadarrUrl": arr.URL, "RadarrApiKey": "radarr-key"}
	for k, v := range config {
		cfg[k] = v
	}
	saveConfig(t, s, cfg)
	return s, arr
}

func TestArrQueueShowsWhyADownloadIsStuck(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]`).
		on("GET /api/v3/queue/details?movieId=3", "["+blockedMovie+"]")

	code, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if code != 200 || body["service"] != "radarr" || body["inArr"] != true || body["monitored"] != true || body["hasFile"] != false || body["isAvailable"] != true || body["canRemove"] != false {
		t.Fatalf("queue = %d %v", code, body)
	}
	items := body["items"].([]any)
	if len(items) != 1 {
		t.Fatalf("items = %v", items)
	}
	item := items[0].(map[string]any)
	for k, want := range map[string]any{"id": 7.0, "trackedDownloadState": "importBlocked", "trackedDownloadStatus": "warning", "status": "completed", "timeleft": "00:00:00", "downloadId": "ABC", "quality": "Bluray-1080p"} {
		if item[k] != want {
			t.Errorf("%s = %v, want %v", k, item[k], want)
		}
	}
	msg := item["statusMessages"].([]any)[0].(map[string]any)["messages"].([]any)[0]
	if msg != "Caution: Found executable file with extension: '.exe'" {
		t.Errorf("message = %v", msg)
	}
	for _, k := range []string{"outputPath", "movie", "customFormats"} {
		if _, ok := item[k]; ok {
			t.Errorf("a user must not get %s", k)
		}
	}

	_, admin := call(t, s, adminMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if admin["canRemove"] != true || admin["items"].([]any)[0].(map[string]any)["outputPath"] != "/downloads/complete/Some.Movie.2026.1080p-GRP" {
		t.Fatalf("admin = %v", admin)
	}
}

func TestArrQueueSonarrEntriesNameTheirEpisode(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/series?tvdbId=280619", `[{"id":1,"monitored":true}]`).
		on("GET /api/v3/queue/details?seriesId=1&includeEpisode=true",
			`[{"id":9,"episodeId":58,"seasonNumber":1,"status":"warning","trackedDownloadState":"downloading","errorMessage":"No files found are eligible for import in /downloads/tv/Show.S01","episode":{"seasonNumber":1,"episodeNumber":4}}]`)

	_, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Sonarr/Queue", "", map[string]any{"tvdbId": "280619"})
	if _, ok := body["hasFile"]; ok {
		t.Errorf("Sonarr has no series hasFile: %v", body)
	}
	item := body["items"].([]any)[0].(map[string]any)
	if item["episodeId"] != 58.0 || item["seasonNumber"] != 1.0 || item["episodeNumber"] != 4.0 {
		t.Errorf("episode = %v", item)
	}
	if item["errorMessage"] != "No files found are eligible for import in Show.S01" {
		t.Errorf("a user's message keeps only the folder name: %v", item["errorMessage"])
	}
	if _, ok := item["timeleft"]; ok {
		t.Errorf("a field the arr left out stays out: %v", item)
	}
}

func TestRedactPaths(t *testing.T) {
	for in, want := range map[string]string{
		"No files found are eligible for import in /downloads/tv/Show.S01": "No files found are eligible for import in Show.S01",
		"/downloads/tv/Show.S01E01.mkv":                                    "Show.S01E01.mkv",
		`Import failed, path does not exist: C:\downloads\Show.S01`:        "Import failed, path does not exist: Show.S01",
		"The download is stalled with no connections":                      "The download is stalled with no connections",
		"Release title S01/E02 is ambiguous":                               "Release title S01/E02 is ambiguous",
		"Caution: Found executable file with extension: '.exe'":            "Caution: Found executable file with extension: '.exe'",
	} {
		if got := redactPaths(in); got != want {
			t.Errorf("redactPaths(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestArrQueueTitleNotInTheArrIsEmptyNotAnError(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/movie?tmdbId=550", `[]`)

	code, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if code != 200 || body["inArr"] != false || len(body["items"].([]any)) != 0 {
		t.Fatalf("queue = %d %v", code, body)
	}
	if got := arr.seen(); len(got) != 1 {
		t.Fatalf("only the lookup is asked: %v", got)
	}
}

func TestArrQueueRefusesBadIdsWithoutAskingTheArr(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	for _, id := range []string{"", "0", "-4", "12abc", "+5"} {
		if code, _ := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": id}); code != 400 {
			t.Errorf("tmdbId %q = %d", id, code)
		}
	}
	if got := arr.seen(); len(got) != 0 {
		t.Fatalf("arr calls = %v", got)
	}
}

func TestArrQueueArrErrorsAre502s(t *testing.T) {
	s, arr := newQueueServer(t, map[string]any{"RadarrApiKey": "wrong"})
	code, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if code != 502 || body["arrStatus"] != 401.0 {
		t.Fatalf("a 401 from Radarr = %d %v", code, body)
	}
	arr.on("GET /api/v3/series?tvdbId=1", `<html>login</html>`)
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Seerr/Sonarr/Queue", "", map[string]any{"tvdbId": "1"}); code != 502 {
		t.Fatalf("an unreadable answer = %d", code)
	}
	arr.Close()
	code, body = call(t, s, userMain, "GET", "/Moonfin/Seerr/Sonarr/Queue", "", map[string]any{"tvdbId": "1"})
	if code != 502 || body["error"] != "Could not reach the sonarr server" || body["arrStatus"] != nil {
		t.Fatalf("an unreachable Sonarr = %d %v", code, body)
	}
}

func TestArrQueueWithoutAnArrSaysSo(t *testing.T) {
	s := NewServer("1.2.3", testHost(store.NewMemKV()))
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"}); code != 404 {
		t.Fatalf("no Radarr = %d", code)
	}
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["arrQueueAvailable"] != false {
		t.Fatalf("ping without an arr = %v", ping)
	}
	s2, _ := newQueueServer(t, nil)
	if _, ping := call(t, s2, userMain, "GET", "/Moonfin/Ping", "", nil); ping["arrQueueAvailable"] != true {
		t.Fatalf("ping with an arr = %v", ping)
	}
}

func TestArrQueueRemovalIsForAdmins(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	if code, _ := call(t, s, userMain, "DELETE", "/Moonfin/Seerr/Radarr/Queue/7", "", nil); code != http.StatusForbidden && code != http.StatusUnauthorized {
		t.Fatalf("a user removing = %d", code)
	}
	if got := arr.seen(); len(got) != 0 {
		t.Fatalf("a refused user must not reach Radarr: %v", got)
	}
}

func TestArrQueueRemovalBlocklistsAndSearchesAgain(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/queue?page=1&pageSize=1000", `{"records":[{"id":7,"downloadId":"ABC"},{"id":8,"downloadId":"DEF"}]}`).
		on("DELETE /api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", ``)

	code, body := call(t, s, adminMain, "DELETE", "/Moonfin/Seerr/Radarr/Queue/7", "", nil)
	if code != 200 || len(body["removed"].([]any)) != 1 || body["removed"].([]any)[0] != 7.0 {
		t.Fatalf("removal = %d %v", code, body)
	}
	// The fake answers 404 to any other path, which removal accepts, so the exact delete is checked.
	if want := "DELETE /api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false"; !slices.Contains(arr.seen(), want) {
		t.Fatalf("missing %q in %v", want, arr.seen())
	}
}

func TestArrQueueSeasonPackGoesAsOneBulkRemoval(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/queue?page=1&pageSize=1000", `{"records":[{"id":11,"downloadId":"pack"},{"id":10,"downloadId":"PACK"},{"id":12,"downloadId":"other"},{"id":13,"downloadId":"pack"}]}`).
		on("DELETE /api/v3/queue/bulk?removeFromClient=true&blocklist=true&skipRedownload=false", ``)

	code, body := call(t, s, adminMain, "DELETE", "/Moonfin/Seerr/Sonarr/Queue/11", "", nil)
	if code != 200 || len(body["removed"].([]any)) != 3 {
		t.Fatalf("removal = %d %v", code, body)
	}
	arr.mu.Lock()
	got := arr.bodies[len(arr.bodies)-1]
	arr.mu.Unlock()
	if got != `{"ids":[10,11,13]}` {
		t.Fatalf("bulk body = %s", got)
	}
	if want := "DELETE /api/v3/queue/bulk?removeFromClient=true&blocklist=true&skipRedownload=false"; !slices.Contains(arr.seen(), want) {
		t.Fatalf("missing %q in %v", want, arr.seen())
	}
}

func TestArrQueueRemovingAGoneDownloadIs404(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/queue?page=1&pageSize=1000", `{"records":[{"id":8,"downloadId":"DEF"}]}`)

	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Seerr/Radarr/Queue/7", "", nil); code != 404 {
		t.Fatalf("gone = %d", code)
	}
	for _, r := range arr.seen() {
		if r[:6] == "DELETE" {
			t.Fatalf("nothing may be deleted: %v", arr.seen())
		}
	}
}

func TestArrQueueFailedRemovalIs502(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/queue?page=1&pageSize=1000", `{"records":[{"id":7,"downloadId":"ABC"}]}`).
		onStatus("DELETE /api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", `{"message":"boom"}`, 500)

	if code, body := call(t, s, adminMain, "DELETE", "/Moonfin/Seerr/Radarr/Queue/7", "", nil); code != 502 || body["arrStatus"] != 500.0 {
		t.Fatalf("failed removal = %d %v", code, body)
	}
}

func TestArrQueueOddAnswers(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/movie?tmdbId=1", `null`).
		on("GET /api/v3/movie?tmdbId=2", `[{"id":0}]`).
		on("GET /api/v3/movie?tmdbId=3", `[{"id":3}]`).
		on("GET /api/v3/queue/details?movieId=3", `[5,{"id":7,"status":"queued"}]`)

	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "1"}); code != 502 {
		t.Errorf("a null lookup = %d", code)
	}
	if _, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "2"}); body["inArr"] != false {
		t.Errorf("a title without a positive id = %v", body)
	}
	if _, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "3"}); len(body["items"].([]any)) != 1 {
		t.Errorf("an entry that isn't an object is skipped: %v", body)
	}
}

func TestArrQueueRemovalOddAnswers(t *testing.T) {
	for _, page := range []string{`{}`, `[{"id":7}]`, `null`} {
		s, arr := newQueueServer(t, nil)
		arr.on("GET /api/v3/queue?page=1&pageSize=1000", page)
		if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Seerr/Radarr/Queue/7", "", nil); code != 502 || len(arr.seen()) != 1 {
			t.Errorf("queue page %s = %d, calls %v", page, code, arr.seen())
		}
	}
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/queue?page=1&pageSize=1000", `{"records":[{"id":7,"downloadId":"ABC"}]}`).
		onStatus("DELETE /api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", `{"message":"Not Found"}`, 404)
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Seerr/Radarr/Queue/7", "", nil); code != 200 {
		t.Errorf("an entry the arr already dropped = %d", code)
	}
}

func TestArrQueueUnreleasedMovieCarriesItsReleaseDateAndLastSearch(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":false,"isAvailable":false,"status":"inCinemas","inCinemas":"2026-11-01T00:00:00Z",
		"releaseDate":"2027-01-15T00:00:00Z","lastSearchTime":"2026-11-12T08:00:00Z","path":"/movies/Some Movie (2026)"}]`).
		on("GET /api/v3/queue/details?movieId=3", "[]")

	_, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if body["isAvailable"] != false || body["releaseDate"] != "2027-01-15T00:00:00Z" || body["lastSearchTime"] != "2026-11-12T08:00:00Z" {
		t.Fatalf("movie = %v", body)
	}
	for _, k := range []string{"status", "inCinemas", "path", "seasons", "searching"} {
		if _, ok := body[k]; ok {
			t.Errorf("%s must stay out: %v", k, body)
		}
	}
}

func TestArrQueueSeriesCarriesEachSeasonsCounts(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/series?tvdbId=280619", `[{"id":1,"monitored":true,"status":"continuing","nextAiring":"2026-11-20T02:00:00Z","seasons":[
		{"seasonNumber":1,"monitored":true,"statistics":{"episodeFileCount":10,"episodeCount":10,"totalEpisodeCount":10,"previousAiring":"2025-05-01T02:00:00Z","sizeOnDisk":1}},
		{"seasonNumber":2,"monitored":true,"statistics":{"episodeFileCount":2,"episodeCount":3,"totalEpisodeCount":8,"nextAiring":"2026-11-20T02:00:00Z"}},
		{"monitored":true}]}]`).
		on("GET /api/v3/queue/details?seriesId=1&includeEpisode=true", "[]")

	_, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Sonarr/Queue", "", map[string]any{"tvdbId": "280619"})
	for _, k := range []string{"status", "nextAiring"} {
		if _, ok := body[k]; ok {
			t.Errorf("%s must stay out: %v", k, body)
		}
	}
	seasons := body["seasons"].([]any)
	if len(seasons) != 2 {
		t.Fatalf("seasons = %v", seasons)
	}
	first, second := seasons[0].(map[string]any), seasons[1].(map[string]any)
	if first["episodeFileCount"] != 10.0 || first["previousAiring"] != nil || first["sizeOnDisk"] != nil {
		t.Errorf("season 1 = %v", first)
	}
	if second["seasonNumber"] != 2.0 || second["monitored"] != true || second["episodeCount"] != 3.0 || second["totalEpisodeCount"] != nil || second["nextAiring"] != "2026-11-20T02:00:00Z" {
		t.Errorf("season 2 = %v", second)
	}
}

func TestArrQueueReportsASearchInProgress(t *testing.T) {
	cases := []struct {
		sonarr   bool
		commands string
		want     any
	}{
		{false, `[{"name":"MoviesSearch","status":"started","body":{"movieIds":[9,3]}}]`, true},
		{false, `[{"name":"MoviesSearch","status":"queued","body":{"movieIds":[3]}}]`, true},
		{false, `[{"name":"MoviesSearch","status":"completed","body":{"movieIds":[3]}}]`, false},
		{false, `[{"name":"MoviesSearch","status":"failed","body":{"movieIds":[3]}}]`, false},
		{false, `[{"name":"MoviesSearch","status":"started","body":{"movieIds":[9]}}]`, false},
		{false, `[{"name":"MissingMoviesSearch","status":"started","body":{}}]`, false},
		{false, `[]`, false},
		{false, `["odd",{"name":"MoviesSearch","status":"started","body":{"movieIds":[3]}}]`, true},
		{false, `not json`, nil},
		{true, `[{"name":"SeriesSearch","status":"started","body":{"seriesId":1}}]`, true},
		{true, `[{"name":"SeasonSearch","status":"queued","body":{"seriesId":1,"seasonNumber":2}}]`, true},
		{true, `[{"name":"MissingEpisodeSearch","status":"started","body":{"seriesId":1,"seriesIds":[]}}]`, true},
		{true, `[{"name":"MissingEpisodeSearch","status":"queued","body":{"seriesIds":[4,1]}}]`, true},
		{true, `[{"name":"MissingEpisodeSearch","status":"started","body":{"seriesIds":[]}}]`, false},
		{true, `[{"name":"EpisodeSearch","status":"started","body":{"episodeIds":[58]}}]`, false},
		{true, `[{"name":"SeriesSearch","status":"started","body":{"seriesId":4}}]`, false},
	}
	for _, c := range cases {
		s, arr := newQueueServer(t, nil)
		arr.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]`).
			on("GET /api/v3/queue/details?movieId=3", "[]").
			on("GET /api/v3/series?tvdbId=280619", `[{"id":1,"monitored":true}]`).
			on("GET /api/v3/queue/details?seriesId=1&includeEpisode=true", "[]").
			on("GET /api/v3/command", c.commands)
		path, query := "/Moonfin/Seerr/Radarr/Queue", map[string]any{"tmdbId": "550"}
		if c.sonarr {
			path, query = "/Moonfin/Seerr/Sonarr/Queue", map[string]any{"tvdbId": "280619"}
		}
		code, body := call(t, s, userMain, "GET", path, "", query)
		if code != 200 || body["searching"] != c.want {
			t.Errorf("%s: %d searching = %v, want %v", c.commands, code, body["searching"], c.want)
		}
	}
}

func TestArrQueueACommandListThatFailsKeepsTheQueue(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]`).
		on("GET /api/v3/queue/details?movieId=3", "[]").
		onStatus("GET /api/v3/command", "[]", http.StatusInternalServerError)

	code, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if _, ok := body["searching"]; code != 200 || ok || body["items"] == nil {
		t.Fatalf("queue = %d %v", code, body)
	}
}

func TestArrQueueDoesntAskAboutSearchesWhenTheAnswerIsKnown(t *testing.T) {
	for name, setup := range map[string]func(*queueArr){
		"queued": func(a *queueArr) {
			a.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]`).
				on("GET /api/v3/queue/details?movieId=3", "["+blockedMovie+"]")
		},
		"on disk": func(a *queueArr) {
			a.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":true,"isAvailable":true}]`).
				on("GET /api/v3/queue/details?movieId=3", "[]")
		},
		"not in the arr": func(a *queueArr) { a.on("GET /api/v3/movie?tmdbId=550", `[]`) },
	} {
		s, arr := newQueueServer(t, nil)
		setup(arr)
		_, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
		if _, ok := body["searching"]; ok || slices.Contains(arr.seen(), "GET /api/v3/command") {
			t.Errorf("%s: asked about searches: %v %v", name, body, arr.seen())
		}
	}
}

func TestArrQueueAnOddNewFieldOnlyLeavesItselfOut(t *testing.T) {
	s, arr := newQueueServer(t, nil)
	arr.on("GET /api/v3/series?tvdbId=280619", `[{"id":1,"monitored":true,"seasons":[
		"odd",
		{"seasonNumber":"two"},
		{"seasonNumber":1,"monitored":"yes","statistics":{"episodeCount":"many","episodeFileCount":2,"nextAiring":5}}]}]`).
		on("GET /api/v3/queue/details?seriesId=1&includeEpisode=true", "[]")
	code, body := call(t, s, userMain, "GET", "/Moonfin/Seerr/Sonarr/Queue", "", map[string]any{"tvdbId": "280619"})
	if code != 200 {
		t.Fatalf("queue = %d %v", code, body)
	}
	seasons := body["seasons"].([]any)
	if len(seasons) != 1 {
		t.Fatalf("seasons = %v", seasons)
	}
	season := seasons[0].(map[string]any)
	if season["seasonNumber"] != 1.0 || season["episodeFileCount"] != 2.0 || len(season) != 2 {
		t.Errorf("season = %v", season)
	}

	arr.on("GET /api/v3/movie?tmdbId=550", `[{"id":3,"monitored":true,"hasFile":false,"isAvailable":false,"releaseDate":7,"lastSearchTime":"2026-11-12T08:00:00Z"}]`).
		on("GET /api/v3/queue/details?movieId=3", "[]")
	code, body = call(t, s, userMain, "GET", "/Moonfin/Seerr/Radarr/Queue", "", map[string]any{"tmdbId": "550"})
	if _, ok := body["releaseDate"]; code != 200 || ok || body["lastSearchTime"] != "2026-11-12T08:00:00Z" {
		t.Errorf("movie = %d %v", code, body)
	}
}
