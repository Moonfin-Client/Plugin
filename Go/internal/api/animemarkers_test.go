package api

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// animeFakeCatalog has Detective Conan as series s1 in library tv, the same
// show under its Catalan name as s2 that only an admin can see, and a movie.
type animeFakeCatalog struct {
	fakeCatalog
	items    []AnimeItem
	episodes map[string][]AnimeItem
}

func (c animeFakeCatalog) AnimeSeries(_ context.Context, libraryIDs []string) ([]AnimeItem, error) {
	var out []AnimeItem
	for _, it := range c.items {
		if it.Type == "show" && (len(libraryIDs) == 0 || slices.Contains(libraryIDs, it.LibraryID)) {
			out = append(out, it)
		}
	}
	return out, nil
}

func (c animeFakeCatalog) AnimeItems(_ context.Context, caller Identity, ids []string) ([]AnimeItem, error) {
	var out []AnimeItem
	for _, it := range c.items {
		if slices.Contains(ids, it.ID) && (it.ID != "s2" || caller.Admin) {
			out = append(out, it)
		}
	}
	for _, list := range c.episodes {
		for _, it := range list {
			if slices.Contains(ids, it.ID) {
				out = append(out, it)
			}
		}
	}
	return out, nil
}

func (c animeFakeCatalog) AnimeEpisodes(_ context.Context, _ Identity, seriesID string) ([]AnimeItem, error) {
	return c.episodes[seriesID], nil
}

func animeDay(n int) time.Time { return time.Date(1996, 1, 8+7*(n-1), 0, 0, 0, 0, time.UTC) }

func newAnimeCatalog() animeFakeCatalog {
	episodes := func(series string, languages ...string) []AnimeItem {
		var out []AnimeItem
		for n := 1; n <= 3; n++ {
			out = append(out, AnimeItem{ID: fmt.Sprintf("%s-e%d", series, n), Type: "episode", Title: fmt.Sprint("Episode ", n),
				LibraryID: "tv", SeriesID: series, SeasonID: series + "-season1", Season: 1, Index: n, AirDate: animeDay(n), AudioLanguages: languages})
		}
		return out
	}
	return animeFakeCatalog{
		items: []AnimeItem{
			{ID: "s1", Type: "show", Title: "Detective Conan", Year: 1996, LibraryID: "tv", Genres: []string{"Anime"}},
			{ID: "s2", Type: "show", Title: "El detectiu Conan", LibraryID: "tv", ProviderIDs: map[string]string{"tvdb": "72454"}},
			{ID: "s3", Type: "show", Title: "Breaking Bad", LibraryID: "tv"},
			{ID: "m1", Type: "movie", Title: "Your Name", LibraryID: "movies", Genres: []string{"Anime"}, AudioLanguages: []string{"jpn"}},
		},
		episodes: map[string][]AnimeItem{"s1": episodes("s1", "jpn", "eng"), "s2": episodes("s2", "jpn")},
	}
}

// animeSites stands in for AnimeFillerList, the mapping table, AniList and
// the episode API.
func animeSites(t *testing.T) *httptest.Server {
	t.Helper()
	var index strings.Builder
	for i := range 60 {
		fmt.Fprintf(&index, `<li><a href="/shows/show-%d">Show %d</a></li>`, i, i)
	}
	index.WriteString(`<li><a href="/shows/detective-conan">Detective Conan (Case Closed)</a></li>`)
	sites := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/shows":
			io.WriteString(w, index.String())
		case "/shows/detective-conan":
			for n, kind := range []string{"manga_canon", "filler", "mixed_canon/filler"} {
				fmt.Fprintf(w, `<tr class="%s odd" id="eps-%d"><td class="Date">%s</td></tr>`, kind, n+1, animeDay(n+1).Format(time.DateOnly))
			}
		case "/mapping.json":
			io.WriteString(w, `[{"mal_id":235,"tvdb_id":72454}]`)
		case "/graphql":
			if query, _ := io.ReadAll(r.Body); strings.Contains(string(query), "search") {
				io.WriteString(w, `{"data":{"Media":{"idMal":235,"title":{"english":"Detective Conan"}}}}`)
				return
			}
			io.WriteString(w, `{"data":{"Page":{"media":[{"id":235,"idMal":235,"title":{"english":"Detective Conan"}}]}}}`)
		case "/v1/anime/235/episodes":
			io.WriteString(w, `{"pagination":{"has_next_page":false},"data":[{"mal_id":1},{"mal_id":2,"recap":true},{"mal_id":3}]}`)
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(sites.Close)
	return sites
}

func newAnimeServer(t *testing.T, config map[string]any) *Server {
	t.Helper()
	h := testHost(store.NewMemKV())
	h.Catalog = newAnimeCatalog()
	h.AnimeDir = t.TempDir()
	s := NewServer("1.2.3", h)
	sites := animeSites(t)
	src := s.anime.src
	src.FillerListBase, src.MappingURL, src.AniListURL, src.EpisodeAPIBase = sites.URL, sites.URL+"/mapping.json", sites.URL+"/graphql", sites.URL+"/v1"
	src.CrawlDelay, src.AniListSpacing, src.EpisodeSpacing, src.RetryDelay = 0, 0, 0, 0
	saveConfig(t, s, config)
	return s
}

func TestAnimeMarkersNeedAnAnimeCatalogAndAFolder(t *testing.T) {
	if status, _ := call(t, newTestServer(), userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s1"}); status != http.StatusNotFound {
		t.Fatalf("Silo answered %d", status)
	}
	h := testHost(store.NewMemKV())
	h.Catalog = newAnimeCatalog()
	if NewServer("1.2.3", h).anime != nil {
		t.Fatal("on without a folder")
	}
}

func TestAnimeSeriesSaysOffWhenBothFeaturesAre(t *testing.T) {
	s := newAnimeServer(t, map[string]any{})
	status, body := call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s1"})
	if status != http.StatusOK || body["enabled"] != false {
		t.Fatalf("%d %v", status, body)
	}
}

func TestAnimeSeriesMarksEpisodesOnceTheShowIsFetched(t *testing.T) {
	s := newAnimeServer(t, map[string]any{"AnimeMarkersEnabled": true})

	// A series the caller can't see is unknown, with an error the apps read
	// as "no such series" rather than "no such route".
	status, body := call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s2"})
	if status != http.StatusNotFound || body["error"] == nil {
		t.Fatalf("hidden series: %d %v", status, body)
	}

	status, body = call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s1"})
	if status != http.StatusOK || body["matched"] != true || body["pending"] != true || body["slug"] != "detective-conan" {
		t.Fatalf("before the fetch: %d %v", status, body)
	}
	if status, _ := call(t, s, userMain, "POST", "/Moonfin/AnimeMarkers/Refresh", "", map[string]any{"seriesId": "s1"}); status != http.StatusForbidden {
		t.Fatalf("a user refreshed: %d", status)
	}
	status, body = call(t, s, adminMain, "POST", "/Moonfin/AnimeMarkers/Refresh", "", map[string]any{"seriesId": "s1"})
	if status != http.StatusOK || body["fetched"] != true || body["episodes"] != float64(3) {
		t.Fatalf("refresh: %d %v", status, body)
	}

	_, body = call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s1"})
	episodes, _ := body["episodes"].(map[string]any)
	// Manga canon is off by default, so episode 1 is listed without a kind.
	want := map[string]any{"s1-e1": nil, "s1-e2": "Filler", "s1-e3": "Mixed"}
	for id, kind := range want {
		row, _ := episodes[id].(map[string]any)
		if row == nil || row["kind"] != kind || row["recap"] != false || row["audio"] != nil {
			t.Fatalf("%s = %v in %v", id, row, body)
		}
	}
	if body["pending"] != false || body["recapKnown"] != false || body["placement"] != "below" || body["labelStyle"] != "full" {
		t.Fatalf("series = %v", body)
	}
}

func TestAnimeSyncFetchesMatchedShowsAndTheirRecaps(t *testing.T) {
	s := newAnimeServer(t, map[string]any{"AnimeMarkersEnabled": true, "AnimeMarkerVerboseLogging": true})
	s.syncAnimeMarkers(context.Background())

	// The Catalan name matched through the English title behind its TVDB id.
	_, body := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s2"})
	episodes, _ := body["episodes"].(map[string]any)
	recap, _ := episodes["s2-e2"].(map[string]any)
	if body["pending"] != false || body["recapKnown"] != true || recap["recap"] != true || recap["kind"] != "Filler" {
		t.Fatalf("after the sync: %v", body)
	}

	_, diag := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Diagnostics", "", nil)
	if diag["seriesConsidered"] != float64(3) || diag["seriesMatched"] != float64(2) || diag["showsCached"] != float64(1) ||
		diag["showsPending"] != float64(0) || diag["episodesFlagged"] != float64(2) || diag["unmatchedCount"] != float64(1) {
		t.Fatalf("diagnostics = %v", diag)
	}

	_, log := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Log", "", map[string]any{"lines": "500"})
	lines, _ := log["lines"].([]any)
	if log["enabled"] != true || !slices.ContainsFunc(lines, func(l any) bool {
		return strings.Contains(l.(string), "recap detective-conan: MAL 235 covers 3 episodes, 1 of them recaps")
	}) {
		t.Fatalf("log = %v", log)
	}
	if _, cleared := call(t, s, adminMain, "POST", "/Moonfin/AnimeMarkers/ClearLog", "", nil); cleared["cleared"] != true {
		t.Fatalf("clear log = %v", cleared)
	}
	if _, cleared := call(t, s, adminMain, "POST", "/Moonfin/AnimeMarkers/ClearCache", "", nil); cleared["cleared"] != float64(1) {
		t.Fatalf("clear cache = %v", cleared)
	}
	if _, body := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s2"}); body["pending"] != true {
		t.Fatalf("after clearing: %v", body)
	}
}

func TestAnimePreviewShowsWhereEachMarkerLanded(t *testing.T) {
	s := newAnimeServer(t, map[string]any{"AnimeMarkersEnabled": true})
	_, search := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Search", "", map[string]any{"term": "detective conan"})
	items, _ := search["Items"].([]any)
	if len(items) != 1 || items[0].(map[string]any)["Id"] != "s1" {
		t.Fatalf("search = %v", search)
	}
	if _, body := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Preview", "", map[string]any{"seriesId": "s1"}); body["pending"] != true {
		t.Fatalf("before the fetch: %v", body)
	}
	call(t, s, adminMain, "POST", "/Moonfin/AnimeMarkers/Refresh", "", map[string]any{"seriesId": "s1"})
	_, body := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Preview", "", map[string]any{"seriesId": "s1"})
	rows, _ := body["episodes"].([]any)
	if body["numbering"] != "absolute" || body["matchedBy"] != "number" || body["markedEpisodes"] != float64(3) ||
		body["fillerEpisodes"] != float64(1) || body["airDatesKnown"] != true || len(rows) != 3 {
		t.Fatalf("preview = %v", body)
	}
	if row := rows[1].(map[string]any); row["season"] != float64(1) || row["index"] != float64(2) || row["kind"] != "Filler" || row["siteNumber"] != float64(2) {
		t.Fatalf("row = %v", row)
	}
	_, none := call(t, s, adminMain, "GET", "/Moonfin/AnimeMarkers/Preview", "", map[string]any{"seriesId": "s3"})
	if none["matched"] != false {
		t.Fatalf("Breaking Bad = %v", none)
	}
}

func TestAnimeAudioPillsFollowTheTracks(t *testing.T) {
	s := newAnimeServer(t, map[string]any{"AnimeAudioMarkersEnabled": true})
	_, body := call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s1"})
	episodes, _ := body["episodes"].(map[string]any)
	seasons, _ := body["seasons"].(map[string]any)
	// Dual audio folds into Dubbed unless the admin asks for both.
	if row, _ := episodes["s1-e1"].(map[string]any); row["audio"] != "Dubbed" || row["kind"] != nil {
		t.Fatalf("episodes = %v", episodes)
	}
	if season, _ := seasons["s1-season1"].(map[string]any); season["audio"] != "Dubbed" {
		t.Fatalf("seasons = %v", seasons)
	}

	// The episode is judged by its series' Anime genre. The movie needs the
	// movie option, and the series has no file of its own.
	_, items := call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Items", "", map[string]any{"ids": "s1-e1,m1,s1,nope"})
	answer, _ := items["items"].(map[string]any)
	if len(answer) != 1 || answer["s1-e1"].(map[string]any)["audio"] != "Dubbed" {
		t.Fatalf("items = %v", items)
	}
	saveConfig(t, s, map[string]any{"AnimeAudioMarkersEnabled": true, "AnimeAudioMarkersMovies": true, "AnimeAudioSeparateDualAudio": true})
	_, items = call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Items", "", map[string]any{"ids": "s1-e1,m1"})
	answer, _ = items["items"].(map[string]any)
	if answer["m1"].(map[string]any)["audio"] != "Subbed" || answer["s1-e1"].(map[string]any)["audio"] != "SubbedAndDubbed" {
		t.Fatalf("items = %v", items)
	}

	// With libraries chosen, only theirs count, and the anime check still applies.
	saveConfig(t, s, map[string]any{"AnimeAudioMarkersEnabled": true, "AnimeMarkerLibraryIds": []any{"tv"}})
	if _, body := call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Series", "", map[string]any{"seriesId": "s3"}); len(body["episodes"].(map[string]any)) != 0 {
		t.Fatalf("Breaking Bad got pills: %v", body)
	}
	if status, body := call(t, s, userMain, "GET", "/Moonfin/AnimeMarkers/Items", "", nil); status != http.StatusBadRequest || body["error"] == nil {
		t.Fatalf("no ids: %d %v", status, body)
	}
}
