package plex

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// calendarPlex is a Plex server with a movie library (1), a TV library (2)
// and an anime library (5) that isn't shared with the member, plus Sonarr and
// Radarr. The anime library is only listed for the owner.
func calendarPlex(t *testing.T) *Companion {
	f := newFakePlex(t)
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := r.Header.Get("X-Plex-Token")
		if r.URL.Path == "/identity" {
			io.WriteString(w, `{"MediaContainer":{"machineIdentifier":"m1"}}`)
			return
		}
		if token != "owner" && token != "member" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		guids := r.URL.Query().Get("includeGuids") == "1"
		switch r.URL.Path {
		case "/myplex/account":
			if token == "member" {
				w.WriteHeader(http.StatusForbidden)
			}
		case "/library/sections":
			anime := `,{"key":"5","type":"show","Location":[{"path":"/data/anime"}]}`
			if token == "member" {
				anime = ""
			}
			io.WriteString(w, `{"MediaContainer":{"Directory":[{"key":"1","type":"movie","Location":[{"id":1,"path":"/data/movies"}]},`+
				`{"key":"2","type":"show","Location":[{"path":"/data/tv"}]}`+anime+`]}}`)
		case "/library/sections/2/all":
			if !guids {
				io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"500"},{"ratingKey":"510"}]}}`)
				return
			}
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"500","guid":"plex://show/a","Guid":[{"id":"tvdb://100"}]},`+
				`{"ratingKey":"510","Guid":[{"id":"imdb://tt0944947"},{"id":"tmdb://1399"}]}]}}`)
		case "/library/sections/5/all":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[]}}`)
		case "/library/sections/1/all":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"600","Guid":[{"id":"imdb://tt0133093"},{"id":"tmdb://603"}]}]}}`)
		case "/library/metadata/500/allLeaves":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"501","parentIndex":1,"index":1},{"ratingKey":"502","parentIndex":1,"index":2}]}}`)
		case "/library/metadata/510/allLeaves":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"511","parentIndex":1,"index":1}]}}`)
		default:
			http.NotFound(w, r)
		}
	}))
	arr := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.Header.Get("X-Api-Key") {
		case "sonarr-key":
			io.WriteString(w, `[`+sonarrPilot+`,
				{"id":12,"seasonNumber":2,"episodeNumber":3,"series":{"title":"Game of Thrones","tvdbId":121361,"tmdbId":1399,"path":"/tv/Game of Thrones"}},
				{"id":13,"seasonNumber":1,"episodeNumber":1,"series":{"title":"Frieren","tvdbId":999,"path":"/anime/Frieren"}},
				{"id":14,"seasonNumber":1,"episodeNumber":1,"series":{"title":"Lost","tvdbId":998,"path":"/elsewhere/Lost"}}]`)
		case "radarr-key":
			io.WriteString(w, `[{"id":21,"title":"The Matrix","tmdbId":603},
				{"id":22,"title":"Also The Matrix","tmdbId":0,"imdbId":"tt0133093"},
				{"id":23,"title":"Upcoming","tmdbId":777,"path":"/movies/Upcoming (2026)"}]`)
		default:
			w.WriteHeader(http.StatusUnauthorized)
		}
	}))
	t.Cleanup(func() { pms.Close(); arr.Close() })
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "owner-token"), []byte("owner"), 0o600); err != nil {
		t.Fatal(err)
	}
	c, err := New(Config{PMS: pms.URL, PlexTV: f.tv.URL, DataDir: dir, Version: "1.0.0"})
	if err != nil {
		t.Fatal(err)
	}
	config, _ := json.Marshal(map[string]any{"SonarrUrl": arr.URL, "SonarrApiKey": "sonarr-key", "RadarrUrl": arr.URL, "RadarrApiKey": "radarr-key"})
	if w, _ := send(c, withToken("POST", "/Moonfin/Admin/Configuration", "owner", string(config))); w.Code != http.StatusNoContent {
		t.Fatalf("save config = %d %s", w.Code, w.Body)
	}
	return c
}

const sonarrPilot = `{"seriesId":1,"tvdbId":7001,"episodeFileId":0,"seasonNumber":1,"episodeNumber":1,"title":"Good News About Hell","airDate":"2026-11-06","airDateUtc":"2026-11-06T02:00:00Z","hasFile":false,"monitored":true,"id":11,"series":{"title":"Severance","tvdbId":100,"tmdbId":95396,"imdbId":"tt11280740","path":"/tv/Severance"},"images":[{"coverType":"screenshot","remoteUrl":"https://artworks.thetvdb.com/e1.jpg"}]}`

func arrCalendar(t *testing.T, c *Companion, token, service string) map[float64]map[string]any {
	t.Helper()
	w, _ := send(c, withToken("GET", "/Moonfin/Seerr/"+service+"/Calendar", token, ""))
	var out []map[string]any
	if w.Code != 200 || json.Unmarshal(w.Body.Bytes(), &out) != nil {
		t.Fatalf("%s calendar as %s = %d %s", service, token, w.Code, w.Body)
	}
	byID := map[float64]map[string]any{}
	for _, e := range out {
		byID[e["id"].(float64)] = e
	}
	return byID
}

func annotation(e map[string]any) []any {
	libs, _ := json.Marshal(e["moonfinLibraryIds"])
	return []any{e["moonfinItemId"], e["moonfinEpisodeId"], string(libs)}
}

func TestSonarrCalendarIsMatchedToTheCallersPlex(t *testing.T) {
	c := calendarPlex(t)
	owner := arrCalendar(t, c, "owner", "Sonarr")
	member := arrCalendar(t, c, "member", "Sonarr")

	want := map[float64][]any{
		11: {"500", "501", `["section-2"]`},
		12: {"510", nil, `["section-2"]`},
		13: {nil, nil, `["section-5"]`},
		14: {nil, nil, `[]`},
	}
	for id, w := range want {
		if got := annotation(owner[id]); !equal(got, w) {
			t.Errorf("owner's episode %v = %v, want %v", id, got, w)
		}
	}
	if len(member) != 3 || member[13] != nil {
		t.Fatalf("the member must lose the episode only in the anime library: %v", member)
	}
	if got := annotation(member[11]); !equal(got, want[11]) {
		t.Errorf("member's episode 11 = %v", got)
	}

	// The element as the app gets it: Sonarr's own fields, untouched, plus
	// Moonbase's three.
	w, _ := send(c, withToken("GET", "/Moonfin/Seerr/Sonarr/Calendar", "member", ""))
	var raw []json.RawMessage
	_ = json.Unmarshal(w.Body.Bytes(), &raw)
	const pilot = `{"airDate":"2026-11-06","airDateUtc":"2026-11-06T02:00:00Z","episodeFileId":0,"episodeNumber":1,"hasFile":false,"id":11,` +
		`"images":[{"coverType":"screenshot","remoteUrl":"https://artworks.thetvdb.com/e1.jpg"}],"monitored":true,` +
		`"moonfinEpisodeId":"501","moonfinItemId":"500","moonfinLibraryIds":["section-2"],"seasonNumber":1,` +
		`"series":{"title":"Severance","tvdbId":100,"tmdbId":95396,"imdbId":"tt11280740","path":"/tv/Severance"},` +
		`"seriesId":1,"title":"Good News About Hell","tvdbId":7001}`
	if len(raw) == 0 || !bytes.Equal(raw[0], []byte(pilot)) {
		t.Fatalf("annotated element =\n%s\nwant\n%s", raw[0], pilot)
	}
}

func TestRadarrCalendarIsMatchedToTheCallersPlex(t *testing.T) {
	got := arrCalendar(t, calendarPlex(t), "member", "Radarr")
	want := map[float64][]any{
		21: {"600", nil, `["section-1"]`},
		22: {"600", nil, `["section-1"]`},
		23: {nil, nil, `["section-1"]`},
	}
	for id, w := range want {
		if a := annotation(got[id]); !equal(a, w) {
			t.Errorf("movie %v = %v, want %v", id, a, w)
		}
		if _, has := got[id]["moonfinEpisodeId"]; has {
			t.Errorf("movie %v has an episode id", id)
		}
	}
}

func TestPingOffersThePlexCalendar(t *testing.T) {
	_, ping := send(calendarPlex(t), withToken("GET", "/Moonfin/Ping", "member", ""))
	if ping["calendarAvailable"] != true || ping["calendarReminders"] != false || ping["calendarDaysAhead"] != 90.0 {
		t.Fatalf("ping = %v", ping)
	}
}

func equal(a, b []any) bool {
	x, _ := json.Marshal(a)
	y, _ := json.Marshal(b)
	return bytes.Equal(x, y)
}
