package api

import (
	"context"
	"io"
	"maps"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// rowFakeCatalog has The Shawshank Redemption (tt0111161) as movie m1 and
// Game of Thrones (tmdb 1399) as show s1, and counts its library reads.
type rowFakeCatalog struct {
	fakeCatalog
	reads *atomic.Int32
}

func (c rowFakeCatalog) LibraryGUIDs(_ context.Context, _ Identity, series bool) (map[string]string, error) {
	c.reads.Add(1)
	if series {
		return map[string]string{"tmdb://1399": "s1"}, nil
	}
	return map[string]string{"imdb://tt0111161": "m1", "tmdb://278": "m1"}, nil
}

// rowProviders fakes IMDb, TMDB, MDBList and Letterboxd and counts the
// requests that reach them.
func rowProviders(t *testing.T, hits *atomic.Int32) *httptest.Server {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		q := r.URL.Query()
		switch {
		case r.URL.Path == "/imdb" && r.Method == http.MethodPost:
			body, _ := io.ReadAll(r.Body)
			if !strings.Contains(string(body), "TOP_RATED_MOVIES") {
				w.WriteHeader(http.StatusBadRequest)
				return
			}
			io.WriteString(w, `{"data":{"chartTitles":{"edges":[
				{"node":{"id":"tt0111161","titleText":{"text":"The Shawshank Redemption"},"primaryImage":{"url":"https://m.media-amazon.com/images/M/x@._V1_.jpg"},"releaseYear":{"year":1994},"titleType":{"id":"movie"}}},
				{"node":{"id":"","titleText":{"text":"skipped"}}},
				{"node":{"id":"tt0944947","titleText":{"text":"Game of Thrones"},"primaryImage":null,"releaseYear":{"year":2011},"titleType":{"id":"tvSeries"}}}]}}}`)
		case r.URL.Path == "/3/find/tt0111161" && q.Get("api_key") == "tmdb-key":
			io.WriteString(w, `{"movie_results":[{"id":278}],"tv_results":[]}`)
		case r.URL.Path == "/3/find/tt0944947" && q.Get("api_key") == "tmdb-key":
			io.WriteString(w, `{"movie_results":[],"tv_results":[{"id":1399}]}`)
		case r.URL.Path == "/3/movie/278" && q.Get("append_to_response") == "release_dates":
			io.WriteString(w, `{"overview":"Two imprisoned men bond.","runtime":142,"backdrop_path":"/shawshank.jpg","genres":[{"name":"Drama"}],
				"release_dates":{"results":[{"iso_3166_1":"GB","release_dates":[{"certification":"15","type":3}]},
				{"iso_3166_1":"US","release_dates":[{"certification":"","type":1},{"certification":"NR","type":1},{"certification":"R","type":3}]}]}}`)
		case r.URL.Path == "/3/tv/1399" && q.Get("append_to_response") == "content_ratings":
			io.WriteString(w, `{"overview":"Seven noble families.","episode_run_time":[60],"poster_path":"/got.jpg","content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]}}`)
		case r.URL.Path == "/3/trending/all/week" && q.Get("language") == "fr-FR":
			io.WriteString(w, `{"results":[{"id":603,"media_type":"movie","title":"Matrix","release_date":"1999-03-31","poster_path":"/m.jpg"},
				{"id":17419,"media_type":"person","name":"Someone"},{"id":1399,"media_type":"tv","name":"Le Trône de fer","first_air_date":"2011-04-17"}]}`)
		case r.URL.Path == "/3/movie/603" || r.URL.Path == "/3/tv/1399":
			io.WriteString(w, `{}`)
		case r.URL.Path == "/lists/someone/best/items" && q.Get("apikey") == "mdb-key" && q.Get("cursor") == "":
			io.WriteString(w, `{"movies":[{"id":278,"rank":2,"title":"The Shawshank Redemption","imdb_id":"tt0111161","release_year":1994,"poster":"https://image.tmdb.org/t/p/w200/s.jpg"}],
				"shows":[{"rank":3,"title":"Game of Thrones","ids":{"tmdb":1399,"tvdb":121361}}],"pagination":{"next_cursor":"p2"}}`)
		case r.URL.Path == "/lists/someone/best/items" && q.Get("cursor") == "p2":
			io.WriteString(w, `{"movies":[{"id":603,"rank":1,"title":"The Matrix","mediatype":"movie","poster":false}],"shows":[],"pagination":{}}`)
		case r.URL.Path == "/lists/official" && q.Get("apikey") == "mdb-key":
			io.WriteString(w, `[{"slug":"top","name":"Top Watched","mediatype":null,"items":2},{"slug":"","name":"no slug"}]`)
		case r.URL.Path == "/lists/official/top/items" && q.Get("apikey") == "mdb-key":
			io.WriteString(w, `{"movies":[{"id":278,"rank":1,"title":"The Shawshank Redemption","imdb_id":"tt0111161","release_year":1994}],
				"shows":[{"id":1399,"rank":2,"title":"Game of Thrones"}],"pagination":{}}`)
		case r.URL.Path == "/dave/rss/":
			io.WriteString(w, `<?xml version='1.0' encoding='utf-8'?><rss xmlns:letterboxd="https://letterboxd.com" xmlns:tmdb="https://themoviedb.org"><channel>
				<item><title>Coward, 2026 - ★★★½</title><letterboxd:filmTitle>Coward</letterboxd:filmTitle><letterboxd:filmYear>2026</letterboxd:filmYear><letterboxd:memberRating>3.5</letterboxd:memberRating><tmdb:movieId>1437981</tmdb:movieId></item>
				<item><title>A list</title></item></channel></rss>`)
		default:
			w.WriteHeader(http.StatusTooManyRequests)
		}
	}))
	t.Cleanup(srv.Close)
	return srv
}

func rowServer(t *testing.T) (*Server, *atomic.Int32, *atomic.Int32) {
	var hits, reads atomic.Int32
	srv := rowProviders(t, &hits)
	h := testHost(store.NewMemKV())
	h.Catalog = rowFakeCatalog{reads: &reads}
	s := NewServer("1.2.3", h)
	s.ext.tmdbBase, s.ext.mdblistBase, s.ext.imdbBase, s.ext.letterboxdBase = srv.URL, srv.URL, srv.URL+"/imdb", srv.URL
	return s, &hits, &reads
}

func rowItems(t *testing.T, s *Server, c caller, query map[string]any) []map[string]any {
	t.Helper()
	query = maps.Clone(query)
	query["includeLocalItemIds"] = "true"
	code, got := call(t, s, c, "GET", "/Moonfin/CustomRows/Items", "", query)
	if code != 200 || got["success"] != true {
		t.Fatalf("%v = %d %v", query, code, got)
	}
	var out []map[string]any
	for _, it := range got["items"].([]any) {
		out = append(out, it.(map[string]any))
	}
	return out
}

func TestCustomRowsNeedAHostThatMatchesTitles(t *testing.T) {
	q := map[string]any{"source": "imdb", "type": "imdb_top_250_movies", "params": "{}"}
	if code, _ := call(t, newTestServer(), userMain, "GET", "/Moonfin/CustomRows/Items", "", q); code != 404 {
		t.Fatalf("Silo = %d, want 404", code)
	}
	if code, _ := call(t, newTestServer(), userMain, "GET", "/Moonfin/MdbList/Lists", "", nil); code != 404 {
		t.Fatalf("Silo official lists = %d, want 404", code)
	}

	s, hits, _ := rowServer(t)
	for _, bad := range []map[string]any{
		{"source": "imdb", "type": "x"},
		{"source": "imdb", "type": "x", "params": `{"id":5}`},
		{"source": "nope", "type": "x", "params": "{}"},
	} {
		if code, _ := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", bad); code != 400 {
			t.Errorf("%v = %d, want 400", bad, code)
		}
	}
	if hits.Load() != 0 {
		t.Fatalf("a bad request must not call out, providers saw %d", hits.Load())
	}
}

func TestImdbChartIsFilledFromTmdbAndMatchedToTheLibrary(t *testing.T) {
	s, hits, reads := rowServer(t)
	withKeys(t, s, "", "tmdb-key")
	q := map[string]any{"source": "imdb", "type": "imdb_top_250_movies", "params": "{}"}

	items := rowItems(t, s, userMain, q)
	if len(items) != 2 {
		t.Fatalf("items = %v", items)
	}
	movie, show := items[0], items[1]
	ids := movie["providerIds"].(map[string]any)
	if movie["localItemId"] != "m1" || ids["Tmdb"] != "278" || movie["officialRating"] != "R" || movie["rank"] != 1.0 ||
		movie["overview"] != "Two imprisoned men bond." || movie["runTimeTicks"] != 142*600_000_000.0 || movie["backdropUrl"] != "/shawshank.jpg" {
		t.Errorf("the owned movie = %v", movie)
	}
	if movie["posterUrl"] != "https://m.media-amazon.com/images/M/x@._V1_UX500_.jpg" {
		t.Errorf("IMDb's poster must be asked for at 500 pixels: %v", movie["posterUrl"])
	}
	// The library knows the show by the TMDB id that TMDB gave its IMDb id.
	if show["type"] != "Series" || show["officialRating"] != "TV-MA" || show["posterUrl"] != "/got.jpg" || show["rank"] != 2.0 || show["localItemId"] != "s1" {
		t.Errorf("the show = %v", show)
	}

	before := hits.Load()
	rowItems(t, s, userMain, q)
	if hits.Load() != before || reads.Load() != 2 {
		t.Fatalf("a second ask must come from the caches, providers saw %d more and the library was read %d times", hits.Load()-before, reads.Load())
	}
	q["params"] = `{"_nocache":"1"}`
	rowItems(t, s, userMain, q)
	if hits.Load() == before {
		t.Fatal("an older app's _nocache must refetch")
	}
}

func TestLocalItemIdsOnlyComeWhenAskedFor(t *testing.T) {
	s, _, reads := rowServer(t)
	withKeys(t, s, "", "tmdb-key")
	_, got := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", map[string]any{"source": "imdb", "type": "imdb_top_250_movies", "params": "{}"})
	items, _ := got["items"].([]any)
	if len(items) != 2 || items[0].(map[string]any)["localItemId"] != nil || reads.Load() != 0 {
		t.Fatalf("without the opt-in = %v, library read %d times", got, reads.Load())
	}
}

func TestTmdbChartSkipsPeopleAndAsksInTheAppsLanguage(t *testing.T) {
	s, _, _ := rowServer(t)
	q := map[string]any{"source": "tmdb_chart", "type": "trending/all/week", "params": "{}", "language": "fr_FR"}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", q); got["success"] != false || got["error"] != "TMDB API key is not configured." {
		t.Fatalf("without a key = %v", got)
	}
	withKeys(t, s, "", "tmdb-key")
	items := rowItems(t, s, userMain, q)
	if len(items) != 2 || items[0]["productionYear"] != 1999.0 || items[1]["type"] != "Series" || items[1]["localItemId"] != "s1" {
		t.Fatalf("items = %v", items)
	}
	q["type"] = "../account"
	if _, got := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", q); got["success"] != false {
		t.Fatalf("a path that isn't a chart must be refused: %v", got)
	}
}

func TestMdbListUserListFollowsTheCursorInRankOrder(t *testing.T) {
	s, _, _ := rowServer(t)
	q := map[string]any{"source": "mdblist", "type": "user_list", "params": `{"username":"someone","listname":"best"}`}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", q); got["error"] != "MDBList API key is not configured." {
		t.Fatalf("without a key = %v", got)
	}
	withKeys(t, s, "mdb-key", "")
	items := rowItems(t, s, userMain, q)
	var names []string
	for _, it := range items {
		names = append(names, it["name"].(string))
	}
	if strings.Join(names, ",") != "The Matrix,The Shawshank Redemption,Game of Thrones" || items[1]["posterUrl"] != "/s.jpg" ||
		items[1]["localItemId"] != "m1" || items[2]["type"] != "Series" || items[2]["localItemId"] != "s1" || items[0]["rank"] != 1.0 {
		t.Fatalf("items = %v", items)
	}
	q["params"] = `{"username":"..","listname":"best"}`
	if _, got := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", q); got["success"] != false {
		t.Fatalf("a name that climbs the path must be refused: %v", got)
	}
}

func TestLetterboxdDiaryCarriesTheMembersStars(t *testing.T) {
	s, _, _ := rowServer(t)
	items := rowItems(t, s, userMain, map[string]any{"source": "letterboxd", "type": "user_diary", "params": `{"user":"Dave"}`})
	if len(items) != 1 || items[0]["userRating"] != "★★★½" || items[0]["rating"] != 3.5 || items[0]["productionYear"] != 2026.0 {
		t.Fatalf("items = %v", items)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/CustomRows/Items", "", map[string]any{"source": "letterboxd", "type": "watchlist", "params": `{"user":"dave"}`}); got["success"] != false {
		t.Fatalf("scraping a watchlist must be refused: %v", got)
	}
}

func TestOfficialListsUseTheServersKey(t *testing.T) {
	s, _, _ := rowServer(t)
	if _, got := call(t, s, userMain, "GET", "/Moonfin/MdbList/Lists", "", nil); got["success"] != false {
		t.Fatalf("without a key = %v", got)
	}
	withKeys(t, s, "mdb-key", "")
	_, got := call(t, s, userMain, "GET", "/Moonfin/MdbList/Lists", "", nil)
	lists, _ := got["lists"].([]any)
	if got["success"] != true || len(lists) != 1 || lists[0].(map[string]any)["count"] != 2.0 {
		t.Fatalf("catalog = %v", got)
	}

	_, got = call(t, s, userMain, "GET", "/Moonfin/MdbList/Lists/top/Items", "", map[string]any{"mediatype": "show"})
	items, _ := got["items"].([]any)
	if got["success"] != true || len(items) != 1 {
		t.Fatalf("shows = %v", got)
	}
	if show := items[0].(map[string]any); show["type"] != "show" || show["id"] != 1399.0 {
		t.Errorf("show = %v", show)
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/MdbList/Lists/top/Items", "", map[string]any{"mediatype": "book"}); code != 400 {
		t.Errorf("bad mediatype = %d", code)
	}

	saveConfig(t, s, map[string]any{"MdblistApiKey": "mdb-key", "MdblistOfficialListsEnabled": false})
	if _, got := call(t, s, userMain, "GET", "/Moonfin/MdbList/Lists/top/Items", "", nil); got["success"] != false {
		t.Fatalf("switched off = %v", got)
	}
}

func TestNormalizeLanguage(t *testing.T) {
	for in, want := range map[string]string{
		"fr": "fr", "pt_BR": "pt-BR", "zh-Hant-TW": "zh-TW", "zh-Hant": "zh-TW", "zh-Hans": "zh-CN",
		"es-419": "es-MX", "en-001": "en", "": "", "x; drop": "",
	} {
		if got := normalizeLanguage(in); got != want {
			t.Errorf("normalizeLanguage(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestRowLocksWaitAndAreDroppedOnceFree(t *testing.T) {
	unlock := lockRow("test-row")
	done := make(chan struct{})
	go func() {
		lockRow("test-row")()
		close(done)
	}()
	select {
	case <-done:
		t.Fatal("a second lock on the same key must wait for the first")
	case <-time.After(50 * time.Millisecond):
	}
	unlock()
	<-done
	rowLocks.Lock()
	_, left := rowLocks.m["test-row"]
	rowLocks.Unlock()
	if left {
		t.Fatal("a key nobody holds must not keep its lock")
	}
}
