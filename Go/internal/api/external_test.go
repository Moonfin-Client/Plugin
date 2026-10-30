package api

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
)

func withKeys(t *testing.T, s *Server, mdblist, tmdb string) {
	t.Helper()
	saveConfig(t, s, map[string]any{"MdblistApiKey": mdblist, "TmdbApiKey": tmdb})
}

// provider fakes MDBList and TMDB and counts the requests that reach it.
func provider(t *testing.T, hits *atomic.Int32) *httptest.Server {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.URL.Path == "/tmdb/movie/603/" && r.URL.Query().Get("apikey") == "mdb-key":
			w.Write([]byte(`{"ratings":[
				{"source":"tmdb","value":8.2,"votes":"100"},
				{"source":"imdb","value":8.7,"score":87,"url":"tt0133093"},
				{"source":"popcorn","score":85},
				{"source":"letterboxd","value":8.4,"score":84}]}`))
		case r.URL.Path == "/3/tv/1399/season/1/episode/2" && r.Header.Get("Authorization") == "Bearer eyJtoken":
			w.Write([]byte(`{"vote_average":8.1,"vote_count":20,"name":"The Kingsroad","air_date":"2011-04-24","season_number":1,"episode_number":2,"still_path":null}`))
		case r.URL.Path == "/3/find/tt0133093" && r.URL.Query().Get("api_key") == "v3key":
			w.Write([]byte(`{"movie_results":[{"id":603}],"tv_results":[]}`))
		case r.URL.Path == "/3/movie/603":
			w.Write([]byte(`{"backdrop_path":"/plain.jpg","images":{"backdrops":[{"iso_639_1":"en","file_path":"/titled.jpg"}]}}`))
		default:
			w.WriteHeader(http.StatusTooManyRequests)
		}
	}))
	t.Cleanup(srv.Close)
	return srv
}

func TestMdbListRatings(t *testing.T) {
	var hits atomic.Int32
	srv := provider(t, &hits)
	s := newTestServer()
	s.ext.mdblistBase = srv.URL
	q := map[string]any{"type": "movie", "tmdbId": "603"}

	if _, got := call(t, s, userMain, "GET", "/Moonfin/MdbList/Ratings", "", q); got["success"] != false || hits.Load() != 0 {
		t.Fatalf("no key must answer success false without calling out: %v", got)
	}

	withKeys(t, s, "mdb-key", "")
	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"mdblistRatingSources":["letterboxd","rtAudience","imdb","metacritic"]}}`, nil)
	q["profile"] = "tv"
	_, got := call(t, s, userMain, "GET", "/Moonfin/MdbList/Ratings", "", q)
	ratings := got["ratings"].([]any)
	if got["success"] != true || len(ratings) != 3 {
		t.Fatalf("ratings = %v", got)
	}
	lb, rt, imdb := ratings[0].(map[string]any), ratings[1].(map[string]any), ratings[2].(map[string]any)
	if lb["source"] != "letterboxd" || lb["value"] != 4.2 {
		t.Errorf("letterboxd must be out of 5: %v", lb)
	}
	if rt["source"] != "tomatoes_audience" || rt["score"] != 85.0 {
		t.Errorf("RT audience must come from popcorn under its resolved name: %v", rt)
	}
	if imdb["url"] != "tt0133093" {
		t.Errorf("imdb = %v", imdb)
	}

	call(t, s, userKids, "GET", "/Moonfin/MdbList/Ratings", "", map[string]any{"type": "movie", "tmdbId": "603"})
	if hits.Load() != 1 {
		t.Fatalf("second caller must be served from cache, provider saw %d requests", hits.Load())
	}

	q["tmdbId"] = "1"
	for range 2 {
		if _, got := call(t, s, userMain, "GET", "/Moonfin/MdbList/Ratings", "", q); got["error"] != "MDBList rate limit reached. Try again later." {
			t.Fatalf("rate limit = %v", got)
		}
	}
	if hits.Load() != 2 {
		t.Fatalf("a failure must be cached briefly, provider saw %d requests", hits.Load())
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/MdbList/Ratings", "", map[string]any{"type": "book", "tmdbId": "1"}); code != 400 {
		t.Fatalf("bad type = %d", code)
	}
}

func TestTmdbRoutes(t *testing.T) {
	var hits atomic.Int32
	srv := provider(t, &hits)
	s := newTestServer()
	s.ext.tmdbBase = srv.URL

	withKeys(t, s, "", "eyJtoken")
	_, got := call(t, s, userMain, "GET", "/Moonfin/Tmdb/EpisodeRating", "", map[string]any{"tmdbId": "1399", "season": "1", "episode": "2"})
	if got["success"] != true || got["name"] != "The Kingsroad" || got["voteCount"] != 20.0 {
		t.Fatalf("episode = %v", got)
	}
	if _, has := got["stillPath"]; has {
		t.Fatal("null fields must be left out")
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Tmdb/EpisodeRating", "", nil); code != 400 {
		t.Fatalf("missing tmdbId = %d", code)
	}

	withKeys(t, s, "", "v3key")
	resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Tmdb/Thumb", "", map[string]any{"imdbId": "tt0133093"})
	if resp.StatusCode != 302 || resp.Header.Get("Location") != "https://image.tmdb.org/t/p/w1280/titled.jpg" {
		t.Fatalf("thumb = %d %v", resp.StatusCode, resp.Header)
	}
}

func TestProviderErrorsNeverShowTheKey(t *testing.T) {
	srv := httptest.NewServer(http.NotFoundHandler())
	srv.Close()
	s := newTestServer()
	s.ext.tmdbBase, s.ext.mdblistBase = srv.URL, srv.URL
	withKeys(t, s, "secret-mdb", "secret-tmdb")
	for _, path := range []string{"/Moonfin/Tmdb/NextEpisode", "/Moonfin/MdbList/Ratings"} {
		_, got := call(t, s, userMain, "GET", path, "", map[string]any{"tmdbId": "1", "type": "movie"})
		msg, _ := got["error"].(string)
		if got["success"] != false || msg == "" || strings.Contains(msg, "secret") {
			t.Fatalf("%s error = %q", path, msg)
		}
	}
}

func TestCanceledCallIsNotCached(t *testing.T) {
	var hits atomic.Int32
	srv := provider(t, &hits)
	s := newTestServer()
	s.ext.mdblistBase = srv.URL
	withKeys(t, s, "mdb-key", "")
	q := map[string]any{"type": "movie", "tmdbId": "603"}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	serve(ctx, s, userMain, "GET", "/Moonfin/MdbList/Ratings", "", q)
	if _, got := call(t, s, userKids, "GET", "/Moonfin/MdbList/Ratings", "", q); got["success"] != true {
		t.Fatalf("a canceled call must not fail the next one: %v", got)
	}
}

func TestProviderIDsCantChangeThePath(t *testing.T) {
	var hits atomic.Int32
	srv := provider(t, &hits)
	s := newTestServer()
	s.ext.tmdbBase, s.ext.mdblistBase = srv.URL, srv.URL
	withKeys(t, s, "mdb-key", "v3key")
	for _, tc := range []struct {
		path  string
		query map[string]any
	}{
		{"/Moonfin/Tmdb/EpisodeRating", map[string]any{"tmdbId": "..", "season": "1"}},
		{"/Moonfin/Tmdb/PersonLinks", map[string]any{"tmdbId": "1/../../x"}},
		{"/Moonfin/MdbList/Ratings", map[string]any{"type": "movie", "tmdbId": ".."}},
	} {
		if code, _ := call(t, s, userMain, "GET", tc.path, "", tc.query); code != 400 {
			t.Errorf("%s %v = %d, want 400", tc.path, tc.query, code)
		}
	}
	if hits.Load() != 0 {
		t.Fatalf("a bad id reached the provider %d times", hits.Load())
	}
}
