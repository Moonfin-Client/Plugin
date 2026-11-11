package plex

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"slices"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// The api turns the markers off for a catalog that isn't one, so a changed
// signature would switch them off without a word.
var _ api.AnimeCatalog = (*Companion)(nil)

// animePlex has a show library 2 holding Detective Conan, another show
// library 5, a movie library, and a viewer who can't see show 21.
func animePlex(t *testing.T, asked *[]string) *Companion {
	t.Helper()
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := r.Header.Get("X-Plex-Token")
		*asked = append(*asked, r.URL.Path+" "+token)
		switch r.URL.Path {
		case "/library/sections":
			io.WriteString(w, `{"MediaContainer":{"Directory":[{"key":"1","type":"movie"},{"key":"2","type":"show"},{"key":"5","type":"show"}]}}`)
		case "/library/sections/2/all":
			if r.URL.Query().Get("type") != "2" || r.URL.Query().Get("includeGuids") != "1" || token != "owner" {
				http.Error(w, "unexpected", http.StatusBadRequest)
				return
			}
			io.WriteString(w, `{"MediaContainer":{"librarySectionID":2,"Metadata":[{"ratingKey":"20","type":"show","title":"El detectiu Conan",
				"originalTitle":"Meitantei Conan","year":1996,"guid":"plex://show/5d9c08","Guid":[{"id":"imdb://tt0131179"},{"id":"tmdb://30983"},{"id":"tvdb://72454"}],
				"Genre":[{"tag":"Anime"}]}]}}`)
		case "/library/sections/5/all":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"50","type":"show","title":"Other"}]}}`)
		case "/library/metadata/20/allLeaves":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[
				{"ratingKey":"201","type":"episode","title":"Roller Coaster Murder Case","parentRatingKey":"200","grandparentRatingKey":"20","parentIndex":1,"index":1,"originallyAvailableAt":"1996-01-08"},
				{"ratingKey":"202","type":"episode","title":"No Date","parentRatingKey":"200","grandparentRatingKey":"20","parentIndex":1,"index":2}]}}`)
		case "/library/metadata/20,201,10":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[
				{"ratingKey":"20","type":"show","title":"El detectiu Conan","librarySectionID":2,"Label":[{"tag":"anime"}],"Guid":[{"id":"tvdb://72454"}]},
				{"ratingKey":"201","type":"episode","librarySectionID":"2","grandparentRatingKey":"20","parentRatingKey":"200","Media":[
				 {"Part":[{"Stream":[{"streamType":1},{"streamType":2,"languageCode":"jpn"},{"streamType":2,"languageCode":"cat"},{"streamType":3,"languageCode":"eng"}]}]},
				 {"Part":[{"Stream":[{"streamType":2,"languageCode":"eng"}]}]}]}]}}`)
		case "/library/metadata/21":
			http.NotFound(w, r)
		default:
			http.Error(w, "unexpected", http.StatusBadRequest)
		}
	}))
	t.Cleanup(pms.Close)
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "owner-token"), []byte("owner"), 0o600); err != nil {
		t.Fatal(err)
	}
	c, err := New(Config{PMS: pms.URL, PlexTV: pms.URL, DataDir: dir, Version: "1.0.0"})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestAnimeSeriesListsTheChosenShowLibrariesAsTheOwner(t *testing.T) {
	var asked []string
	c := animePlex(t, &asked)
	shows, err := c.AnimeSeries(context.Background(), []string{"2"})
	if err != nil {
		t.Fatal(err)
	}
	want := api.AnimeItem{ID: "20", Type: "show", Title: "El detectiu Conan", OriginalTitle: "Meitantei Conan", Year: 1996, LibraryID: "2",
		ProviderIDs: map[string]string{"imdb": "tt0131179", "tmdb": "30983", "tvdb": "72454"}, Genres: []string{"Anime"}, Tags: []string{}}
	if len(shows) != 1 || !reflect.DeepEqual(shows[0], want) {
		t.Fatalf("shows = %+v", shows)
	}
	if slices.Contains(asked, "/library/sections/5/all owner") {
		t.Fatal("read a library that wasn't chosen")
	}
	if all, _ := c.AnimeSeries(context.Background(), nil); len(all) != 2 {
		t.Fatalf("every library = %+v", all)
	}
}

func TestAnimeEpisodesAndItemsReadAsTheCaller(t *testing.T) {
	var asked []string
	c := animePlex(t, &asked)
	ctx := context.Background()
	viewer := api.Identity{Token: "viewer"}

	episodes, err := c.AnimeEpisodes(ctx, viewer, "20")
	if err != nil || len(episodes) != 2 {
		t.Fatalf("episodes = %+v, %v", episodes, err)
	}
	first := episodes[0]
	if first.ID != "201" || first.SeriesID != "20" || first.SeasonID != "200" || first.Season != 1 || first.Index != 1 ||
		!first.AirDate.Equal(time.Date(1996, 1, 8, 0, 0, 0, 0, time.UTC)) || !episodes[1].AirDate.IsZero() {
		t.Fatalf("episodes = %+v", episodes)
	}
	if !slices.Contains(asked, "/library/metadata/20/allLeaves viewer") {
		t.Fatalf("asked %v", asked)
	}
	if none, err := c.AnimeEpisodes(ctx, viewer, "../20"); none != nil || err != nil {
		t.Fatalf("a path id was read: %v %v", none, err)
	}

	items, err := c.AnimeItems(ctx, viewer, []string{"20", "201", "10", "not-plex"})
	if err != nil || len(items) != 2 {
		t.Fatalf("items = %+v, %v", items, err)
	}
	// Only the first version's audio counts, and its subtitles don't.
	if items[0].LibraryID != "2" || !slices.Equal(items[0].Tags, []string{"anime"}) || !slices.Equal(items[1].AudioLanguages, []string{"jpn", "cat"}) || items[1].LibraryID != "2" {
		t.Fatalf("items = %+v", items)
	}
	if hidden, err := c.AnimeItems(ctx, viewer, []string{"21"}); len(hidden) != 0 || err != nil {
		t.Fatalf("hidden = %+v, %v", hidden, err)
	}
}
