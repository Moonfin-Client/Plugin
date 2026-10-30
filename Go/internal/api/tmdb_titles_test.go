package api

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestTmdbTitleRoutes(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/3/movie/603/reviews":
			if r.URL.Query().Get("language") != "en-US" {
				w.Write([]byte(`{"total_results":0,"results":[]}`))
				return
			}
			w.Write([]byte(`{"total_results":2,"results":[
				{"id":"r1","author":"neo","content":"<p>Whoa.</p><p><b>Very</b> <a href=\"https://x.test/a\">good</a>&amp; <script>x</script>\r\n</p>",
				 "author_details":{"name":"","username":"neo1","rating":9,"avatar_path":"/https://www.gravatar.com/avatar/1.jpg"},
				 "created_at":"2020-01-01T00:00:00Z","url":"javascript:alert(1)"},
				{"id":"r2","author":"x","content":"   "}]}`))
		case "/3/tv/1399":
			if r.URL.Query().Get("append_to_response") == "images,videos" {
				w.Write([]byte(`{"overview":" Winter. ","vote_count":10,"vote_average":8.45,"genres":[{"name":"Drama"}],
					"episode_run_time":[60],"backdrop_path":"/titled.jpg",
					"images":{"backdrops":[{"iso_639_1":"en","file_path":"/en.jpg"},{"iso_639_1":null,"file_path":"/plain.jpg"}],
					          "logos":[{"iso_639_1":"en","file_path":"/logo.svg"},{"iso_639_1":null,"file_path":"/logo.png"}]},
					"videos":{"results":[{"site":"YouTube","key":"tease","type":"Teaser","official":true},
					                     {"site":"YouTube","key":"fan","type":"Trailer","official":false},
					                     {"site":"YouTube","key":"real","type":"Trailer","official":true,"iso_639_1":"en"}]}}`))
				return
			}
			w.Write([]byte(`{"production_companies":[{"id":76043,"name":"Revolution Sun","logo_path":"/rs.png"},{"id":1,"name":""}],
				"networks":[{"id":49,"name":"HBO","logo_path":null}]}`))
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(srv.Close)
	s := newTestServer()
	s.ext.tmdbBase = srv.URL
	withKeys(t, s, "", "v3key")

	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["tmdbReviewsSupported"] != true {
		t.Fatal("apps only ask for reviews when Ping says they're supported")
	}
	_, got := call(t, s, userMain, "GET", "/Moonfin/Tmdb/Reviews", "", map[string]any{"tmdbId": "603", "language": "de-DE"})
	reviews, _ := got["reviews"].([]any)
	if got["success"] != true || len(reviews) != 1 {
		t.Fatalf("reviews = %v (German had none, so English must be asked, and empty text dropped)", got)
	}
	rv := reviews[0].(map[string]any)
	if rv["author"] != "neo" || rv["rating"] != 9.0 || rv["avatarUrl"] != "https://www.gravatar.com/avatar/1.jpg" {
		t.Errorf("review = %v", rv)
	}
	if want := "Whoa.\n\n**Very** [good](https://x.test/a)& x"; rv["content"] != want {
		t.Errorf("content = %q, want %q", rv["content"], want)
	}
	if _, has := rv["url"]; has {
		t.Errorf("a non-web url must be left out: %v", rv["url"])
	}

	_, got = call(t, s, userMain, "GET", "/Moonfin/Tmdb/ProductionCompanies", "", map[string]any{"tmdbId": "1399", "type": "tv"})
	companies, _ := got["companies"].([]any)
	if got["success"] != true || len(companies) != 2 {
		t.Fatalf("companies = %v", got)
	}
	rs, hbo := companies[0].(map[string]any), companies[1].(map[string]any)
	if rs["id"] != 76043.0 || rs["hasLogo"] != true || rs["logoUrl"] != "https://image.tmdb.org/t/p/w500/rs.png" {
		t.Errorf("company = %v", rs)
	}
	if hbo["id"] != -49.0 || hbo["hasLogo"] != false {
		t.Errorf("a network keeps a negated id: %v", hbo)
	}

	code, got := call(t, s, userMain, "GET", "/Moonfin/Tmdb/Details", "", map[string]any{"tmdbId": "1399", "type": "tv"})
	if code != 200 || got["overview"] != "Winter." || got["rating"] != 8.4 || got["runTimeTicks"] != 36_000_000_000.0 ||
		got["backdropPath"] != "/plain.jpg" || got["logoPath"] != "/logo.png" ||
		got["trailerUrl"] != "https://www.youtube.com/watch?v=real" {
		t.Fatalf("details = %d %v", code, got)
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Tmdb/Details", "", map[string]any{"tmdbId": "5", "type": "tv"}); code != 404 {
		t.Fatalf("unknown title = %d", code)
	}
}
