package api

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"reflect"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

const (
	christmasKeyword = 207317
	halloweenKeyword = 3335
)

type seasonalFakeCatalog struct {
	fakeCatalog
	movies  *[]SeasonalMovie
	visible map[string]bool
	asked   *[][]string
}

func (c seasonalFakeCatalog) SeasonalMovies(context.Context) ([]SeasonalMovie, error) {
	return *c.movies, nil
}

func (c seasonalFakeCatalog) SmartItems(_ context.Context, _ Identity, keys []string) (map[string]json.RawMessage, error) {
	*c.asked = append(*c.asked, keys)
	out := map[string]json.RawMessage{}
	for _, k := range keys {
		if c.visible == nil || c.visible[k] {
			out[k] = json.RawMessage(`{"ratingKey":"` + k + `"}`)
		}
	}
	return out, nil
}

// seasonalTMDB fakes TMDB's keywords, discover and details, and counts each
// path asked for.
func seasonalTMDB(t *testing.T) (*httptest.Server, map[string]int) {
	var mu sync.Mutex
	hits := map[string]int{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		hits[r.URL.Path]++
		mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		var id int
		switch {
		case r.URL.Path == "/3/movie/404/keywords":
			w.WriteHeader(http.StatusNotFound)
		case strings.HasSuffix(r.URL.Path, "/keywords"):
			fmt.Sscanf(r.URL.Path, "/3/movie/%d/keywords", &id)
			keyword := christmasKeyword
			if id == 109 {
				keyword = halloweenKeyword
			}
			fmt.Fprintf(w, `{"id":%d,"keywords":[{"id":%d,"name":"x"}]}`, id, keyword)
		case r.URL.Path == "/3/discover/movie":
			q := r.URL.Query()
			if !strings.Contains(q.Get("with_keywords"), fmt.Sprint(christmasKeyword)) || q.Get("vote_count.gte") != "100" || q.Get("api_key") != "v3key" {
				t.Errorf("discover asked %v", q)
			}
			if q.Get("page") != "1" {
				w.Write([]byte(`{"results":[]}`))
				return
			}
			w.Write([]byte(`{"results":[
				{"id":101,"title":"Owned"},
				{"id":500,"title":"Elf","release_date":"2003-10-09","poster_path":"/elf.jpg","backdrop_path":"/elf-wide.jpg"},
				{"id":500,"title":"Elf again"},
				{"id":501,"title":"Unknown to details"}]}`))
		case r.URL.Path == "/3/movie/500":
			w.Write([]byte(`{"overview":"Buddy","vote_count":10,"vote_average":7.04,"imdb_id":"tt0319343","runtime":97,
				"genres":[{"name":"Comedy"}],"poster_path":"/other.jpg",
				"release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"type":3,"certification":"PG"}]}]}}`))
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(srv.Close)
	return srv, hits
}

func newSeasonalServer(t *testing.T, catalog Catalog, dir string, at time.Time) *Server {
	t.Helper()
	s := NewServer("1.2.3", Host{Name: "Plex", KV: store.NewMemKV(), Catalog: catalog, Identify: headerIdentity,
		SeasonalDir: dir, SeasonalRules: "../../../shared/Seasonal/seasonal-holidays.json"})
	if s.seasonal == nil {
		t.Fatal("the seasonal row is off")
	}
	s.seasonal.now = func() time.Time { return at }
	return s
}

func rowKeys(t *testing.T, got map[string]any) []string {
	t.Helper()
	var keys []string
	items, _ := got["items"].([]any)
	for _, it := range items {
		keys = append(keys, it.(map[string]any)["ratingKey"].(string))
	}
	return keys
}

func TestSeasonalRowOnPlex(t *testing.T) {
	tmdb, hits := seasonalTMDB(t)
	movies := []SeasonalMovie{{"1", "101"}, {"2", "102"}, {"3", "103"}, {"4", "104"}, {"5", "105"}, {"6", "106"},
		{"7", "107"}, {"8", "101"}, {"9", "109"}, {"10", "404"}}
	// 7 isn't shared with the caller, and 8 is a second copy of 1.
	visible := map[string]bool{"1": true, "2": true, "3": true, "4": true, "5": true, "6": true, "8": true, "9": true}
	var asked [][]string
	catalog := seasonalFakeCatalog{movies: &movies, visible: visible, asked: &asked}
	dir := t.TempDir()
	s := newSeasonalServer(t, catalog, dir, time.Date(2026, 12, 10, 18, 0, 0, 0, time.Local))
	s.ext.tmdbBase = tmdb.URL
	saveConfig(t, s, map[string]any{"TmdbApiKey": "v3key", "SeerrEnabled": true, "SeerrUrl": "http://seerr.local:5055"})

	s.syncSeasonalKeywords(context.Background())
	if len(s.seasonal.keywords) != 9 || s.seasonal.keywords["10"].TmdbID != "" {
		t.Fatalf("keywords = %v, want every movie TMDB knows", s.seasonal.keywords)
	}

	code, got := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "us"})
	keys := rowKeys(t, got)
	if code != http.StatusOK || got["holiday"] != "christmas" || got["country"] != "US" || len(keys) != 6 ||
		slices.Contains(keys, "7") || slices.Contains(keys, "9") || (slices.Contains(keys, "1") && slices.Contains(keys, "8")) {
		t.Fatalf("row = %d %v", code, got)
	}
	if suggestions, _ := got["suggestions"].([]any); len(suggestions) != 0 {
		t.Fatalf("suggestions without a Seerr session = %v", suggestions)
	}
	_, again := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "US"})
	if !slices.Equal(rowKeys(t, again), keys) {
		t.Fatalf("the order changed within the day: %v then %v", keys, rowKeys(t, again))
	}

	if err := s.putSeerrSession(context.Background(), Identity{UserID: userMain.user}, &seerrSession{Cookie: "c", LastValidated: time.Now().UnixMilli()}); err != nil {
		t.Fatal(err)
	}
	_, got = call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "US"})
	var want []any
	json.Unmarshal([]byte(`[{"name":"Elf","type":"Movie","productionYear":2003,"rank":2,"providerIds":{"Imdb":"tt0319343","Tmdb":"500"},`+
		`"rating":7,"officialRating":"PG","posterUrl":"/elf.jpg","backdropUrl":"/elf-wide.jpg","overview":"Buddy","genres":["Comedy"],"runTimeTicks":58200000000},`+
		`{"name":"Unknown to details","type":"Movie","rank":3,"providerIds":{"Tmdb":"501"}}]`), &want)
	if !reflect.DeepEqual(got["suggestions"], want) {
		t.Fatalf("suggestions = %v\nwant %v", got["suggestions"], want)
	}
	call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "US"})
	if hits["/3/discover/movie"] != 2 {
		t.Fatalf("discover was asked %d times, want once a page for everyone", hits["/3/discover/movie"])
	}

	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/global", `{"profile":{"seasonalRowHiddenHolidays":["Christmas"]}}`, nil)
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "US"}); got["holiday"] != nil {
		t.Fatalf("a hidden holiday = %v", got)
	}

	// A new start reads what was kept, a movie gone from Plex is dropped and
	// one matched again is looked up again.
	movies = slices.DeleteFunc(movies, func(m SeasonalMovie) bool { return m.ID == "9" })
	movies[1].TmdbID = "110"
	s = newSeasonalServer(t, catalog, dir, time.Date(2026, 12, 10, 18, 0, 0, 0, time.Local))
	s.ext.tmdbBase = tmdb.URL
	saveConfig(t, s, map[string]any{"TmdbApiKey": "v3key"})
	if len(s.seasonal.keywords) != 9 || len(s.seasonal.suggestions) != 1 {
		t.Fatalf("after a restart: %d movies, %d suggestion lists", len(s.seasonal.keywords), len(s.seasonal.suggestions))
	}
	before := hits["/3/movie/101/keywords"]
	s.syncSeasonalKeywords(context.Background())
	if _, gone := s.seasonal.keywords["9"]; gone || s.seasonal.keywords["2"].TmdbID != "110" || hits["/3/movie/101/keywords"] != before {
		t.Fatalf("keywords after the library changed = %v", s.seasonal.keywords)
	}
}

func TestSeasonalRowFillsUpAFewMoviesAtATime(t *testing.T) {
	var asked [][]string
	movies := []SeasonalMovie{}
	s := newSeasonalServer(t, seasonalFakeCatalog{movies: &movies, asked: &asked}, t.TempDir(), time.Date(2026, 12, 10, 18, 0, 0, 0, time.Local))
	for i := range 120 {
		s.seasonal.keywords[fmt.Sprint(i)] = seasonalKeywords{TmdbID: fmt.Sprint(1000 + i), IDs: []int{christmasKeyword}}
	}
	_, got := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "US"})
	if len(rowKeys(t, got)) != seasonalMaxOwned || len(asked) != 1 || len(asked[0]) != seasonalBatch {
		t.Fatalf("%d titles from %d reads", len(rowKeys(t, got)), len(asked))
	}
}

func TestSeasonalRowGivesWayToTheNextHoliday(t *testing.T) {
	var asked [][]string
	movies := []SeasonalMovie{}
	// Canada's Thanksgiving week runs inside Halloween's month and comes
	// first, but there's nothing for it here.
	s := newSeasonalServer(t, seasonalFakeCatalog{movies: &movies, asked: &asked}, t.TempDir(), time.Date(2026, 10, 8, 18, 0, 0, 0, time.Local))
	for i := range 5 {
		s.seasonal.keywords[fmt.Sprint(i)] = seasonalKeywords{TmdbID: fmt.Sprint(1000 + i), IDs: []int{halloweenKeyword}}
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "CA"}); got["holiday"] != nil {
		t.Fatalf("5 titles make a row: %v", got)
	}
	s.seasonal.keywords["5"] = seasonalKeywords{TmdbID: "1005", IDs: []int{halloweenKeyword}}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", map[string]any{"country": "CA"}); got["holiday"] != "halloween" {
		t.Fatalf("row = %v", got)
	}
}

func TestSeasonalRowIsOffWithoutAHostThatListsMovies(t *testing.T) {
	if code, _ := call(t, newTestServer(), userMain, "GET", "/Moonfin/Seasonal/Row", "", nil); code != http.StatusNotFound {
		t.Fatalf("Silo = %d", code)
	}
	var asked [][]string
	movies := []SeasonalMovie{}
	s := NewServer("1.2.3", Host{Name: "Plex", KV: store.NewMemKV(), Catalog: seasonalFakeCatalog{movies: &movies, asked: &asked},
		Identify: headerIdentity, SeasonalDir: t.TempDir(), SeasonalRules: "missing.json"})
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Seasonal/Row", "", nil); code != http.StatusNotFound {
		t.Fatalf("without the holidays file = %d", code)
	}
}
