package plex

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// similarPlex is a Plex server where "member" can open movie libraries 1 and
// 3 and show library 2. Library 3 holds a copy of the seed and of Total
// Recall. The listings cut genres and cast short, as Plex's do.
type similarPlex struct {
	*Companion
	listings atomic.Int32
	types    sync.Map
}

var similarFull = map[string]string{
	"10": `{"ratingKey":"10","type":"movie","guid":"plex://movie/br","title":"Blade Runner","year":1982,"audienceRating":8.1,
		"Genre":[{"tag":"Science Fiction"},{"tag":"Thriller"}],"Director":[{"tag":"Ridley Scott"}],"Guid":[{"id":"imdb://tt0083658"}],"Rating":[{"image":"imdb://image.rating","value":8.1,"type":"audience"}]}`,
	"11": `{"ratingKey":"11","type":"movie","guid":"plex://movie/legend","title":"Legend","year":1985,"originallyAvailableAt":"1985-08-28",
		"Genre":[{"tag":"Fantasy"},{"tag":"Thriller"}],"Director":[{"tag":"Ridley Scott"}],"Rating":[{"image":"imdb://image.rating","value":6.5,"type":"audience"}]}`,
	"12": `{"ratingKey":"12","type":"movie","guid":"plex://movie/recall","title":"Total Recall","year":1990,"originallyAvailableAt":"1990-06-01",
		"Genre":[{"tag":"Science Fiction"},{"tag":"Thriller"}],"Director":[{"tag":"Paul Verhoeven"}]}`,
	"13": `{"ratingKey":"13","type":"movie","guid":"plex://movie/wargames","title":"WarGames","year":1983,"originallyAvailableAt":"1983-06-03",
		"Genre":[{"tag":"Drama"},{"tag":"Science Fiction"},{"tag":"Thriller"}],"Director":[{"tag":"John Badham"}]}`,
	"20": `{"ratingKey":"20","type":"episode","title":"Pilot"}`,
	"40": `{"ratingKey":"40","type":"show","guid":"plex://show/arcane","title":"Arcane","year":2021,"Genre":[{"tag":"Animation"}]}`,
	"41": `{"ratingKey":"41","type":"show","guid":"plex://show/bebop","title":"Cowboy Bebop","year":1998,"Genre":[{"tag":"Animation"}]}`,
}

var similarListingsBySection = map[string]string{
	"1": `[{"ratingKey":"10","guid":"plex://movie/br","title":"Blade Runner","year":1982,"Genre":[{"tag":"Science Fiction"}]},
		{"ratingKey":"11","guid":"plex://movie/legend","title":"Legend","year":1985,"originallyAvailableAt":"1985-08-28","Genre":[{"tag":"Fantasy"}]},
		{"ratingKey":"12","guid":"plex://movie/recall","title":"Total Recall","year":1990,"originallyAvailableAt":"1990-06-01","Genre":[{"tag":"Science Fiction"}]},
		{"ratingKey":"13","guid":"plex://movie/wargames","title":"WarGames","year":1983,"originallyAvailableAt":"1983-06-03","Genre":[{"tag":"Drama"}]}]`,
	"2": `[{"ratingKey":"40","guid":"plex://show/arcane","title":"Arcane","year":2021},{"ratingKey":"41","guid":"plex://show/bebop","title":"Cowboy Bebop","year":1998}]`,
	"3": `[{"ratingKey":"30","guid":"plex://movie/br","title":"Blade Runner","year":1982,"Genre":[{"tag":"Science Fiction"}]},
		{"ratingKey":"32","guid":"plex://movie/recall","title":"Total Recall","year":1990,"Genre":[{"tag":"Science Fiction"}]}]`,
}

func newSimilarPlex(t *testing.T) *similarPlex {
	f := &similarPlex{}
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-Plex-Token") != "member" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		switch path := r.URL.Path; {
		case path == "/library/sections":
			io.WriteString(w, `{"MediaContainer":{"Directory":[{"key":"1","type":"movie"},{"key":"2","type":"show"},{"key":"3","type":"movie"},{"key":"4","type":"artist"}]}}`)
		case strings.HasPrefix(path, "/library/sections/") && strings.HasSuffix(path, "/all"):
			f.listings.Add(1)
			section := strings.Split(path, "/")[3]
			f.types.Store(section, r.URL.Query().Get("type"))
			io.WriteString(w, `{"MediaContainer":{"Metadata":`+similarListingsBySection[section]+`}}`)
		case strings.HasPrefix(path, "/library/metadata/"):
			var found []string
			for _, key := range strings.Split(strings.TrimPrefix(path, "/library/metadata/"), ",") {
				if m, ok := similarFull[key]; ok {
					found = append(found, m)
				}
			}
			if len(found) == 0 {
				w.WriteHeader(http.StatusNotFound)
				return
			}
			io.WriteString(w, `{"MediaContainer":{"Metadata":[`+strings.Join(found, ",")+`]}}`)
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(pms.Close)
	c, err := New(Config{PMS: pms.URL, PlexTV: pms.URL, DataDir: t.TempDir(), Version: "1.0.0"})
	if err != nil {
		t.Fatal(err)
	}
	f.Companion = c
	return f
}

var member = api.Identity{UserID: 7, Token: "member"}

func similarKeys(t *testing.T, items []json.RawMessage, err error) []string {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
	keys := []string{}
	for _, raw := range items {
		var it struct {
			RatingKey string `json:"ratingKey"`
		}
		if err := json.Unmarshal(raw, &it); err != nil {
			t.Fatal(err)
		}
		keys = append(keys, it.RatingKey)
	}
	return keys
}

func TestSimilarItemsRankTheCallersMoviesOnTheirFullMetadata(t *testing.T) {
	f := newSimilarPlex(t)
	items, err := f.SimilarItems(context.Background(), member, "10", 20, nil)
	// On the listing alone Total Recall leads on its genre. Legend's director
	// and the rest of WarGames' genres only show in the full metadata, and
	// they put both ahead. Neither the seed nor the copies in library 3 come
	// back.
	if got := similarKeys(t, items, err); !slices.Equal(got, []string{"11", "13", "12"}) {
		t.Fatalf("ranked %v", got)
	}
	var legend map[string]any
	_ = json.Unmarshal(items[0], &legend)
	if legend["title"] != "Legend" || legend["Director"] == nil {
		t.Fatalf("the reply isn't the caller's full metadata: %v", legend)
	}
	if typ, _ := f.types.Load("1"); typ != "1" {
		t.Fatalf("movies were listed as type %v", typ)
	}
}

func TestSimilarItemsLeaveOutExcludedItems(t *testing.T) {
	items, err := newSimilarPlex(t).SimilarItems(context.Background(), member, "10", 20, []string{"13", "12"})
	if got := similarKeys(t, items, err); !slices.Equal(got, []string{"11"}) {
		t.Fatalf("ranked %v", got)
	}
}

func TestSimilarItemsHonorTheLimit(t *testing.T) {
	items, err := newSimilarPlex(t).SimilarItems(context.Background(), member, "10", 2, nil)
	if got := similarKeys(t, items, err); len(got) != 2 {
		t.Fatalf("ranked %v", got)
	}
}

func TestSimilarItemsForAShowListTheShowLibraries(t *testing.T) {
	f := newSimilarPlex(t)
	items, err := f.SimilarItems(context.Background(), member, "40", 20, nil)
	if got := similarKeys(t, items, err); !slices.Equal(got, []string{"41"}) {
		t.Fatalf("ranked %v", got)
	}
	if typ, _ := f.types.Load("2"); typ != "2" {
		t.Fatalf("shows were listed as type %v", typ)
	}
	if _, listed := f.types.Load("1"); listed {
		t.Fatal("a movie library was listed for a show")
	}
}

func TestSimilarItemsForAnEpisodeAreEmptyWithoutListingTheLibrary(t *testing.T) {
	f := newSimilarPlex(t)
	items, err := f.SimilarItems(context.Background(), member, "20", 20, nil)
	if err != nil || len(items) != 0 || f.listings.Load() != 0 {
		t.Fatalf("items %v, err %v, listings %d", items, err, f.listings.Load())
	}
}

func TestSimilarItemsForAnItemTheCallerCantOpenAreNotFound(t *testing.T) {
	f := newSimilarPlex(t)
	if _, err := f.SimilarItems(context.Background(), member, "99", 20, nil); !errors.Is(err, api.ErrItemNotFound) {
		t.Fatalf("missing item: err = %v", err)
	}
	if _, err := f.SimilarItems(context.Background(), api.Identity{UserID: 8, Token: "revoked"}, "10", 20, nil); !errors.Is(err, api.ErrItemNotFound) {
		t.Fatalf("revoked token: err = %v", err)
	}
}

func TestSimilarItemsReuseTheListing(t *testing.T) {
	f := newSimilarPlex(t)
	var wg sync.WaitGroup
	for range 5 {
		wg.Go(func() {
			if _, err := f.SimilarItems(context.Background(), member, "10", 20, nil); err != nil {
				t.Error(err)
			}
		})
	}
	wg.Wait()
	if n := f.listings.Load(); n != 2 {
		t.Fatalf("listed %d times, want once for each movie library", n)
	}
	// Another account reads its own.
	if _, err := f.SimilarItems(context.Background(), api.Identity{UserID: 8, Token: "member"}, "10", 20, nil); err != nil {
		t.Fatal(err)
	}
	if n := f.listings.Load(); n != 4 {
		t.Fatalf("listed %d times after a second account, want 4", n)
	}
}
