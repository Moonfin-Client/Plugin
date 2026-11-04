package plex

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// smartPlex is a Plex server with one movie library, one music library, and a
// member who can open movie 10 but not movie 11.
func smartPlex(t *testing.T) *Companion {
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := r.Header.Get("X-Plex-Token")
		switch r.URL.Path + "?" + r.URL.Query().Get("type") {
		case "/library/sections?":
			io.WriteString(w, `{"MediaContainer":{"Directory":[{"key":"1","type":"movie"},{"key":"3","type":"artist"}]}}`)
		case "/library/sections/1/all?1":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"10"},{"ratingKey":"11"}]}}`)
		case "/library/metadata/10,11?":
			if token != "owner" {
				w.WriteHeader(http.StatusUnauthorized)
				return
			}
			io.WriteString(w, `{"MediaContainer":{"Metadata":[
				{"ratingKey":"10","type":"movie","guid":"plex://movie/5d776","title":"Cast Away","year":2000,"summary":"Stranded.","Genre":[{"tag":"Drama"}],
				 "Director":[{"id":7,"tag":"Robert Zemeckis"}],"Role":[{"id":5,"tag":"Tom Hanks","thumb":"http://img/hanks.jpg"}],
				 "Guid":[{"id":"imdb://tt0162222"},{"id":"tmdb://8358"}],"guid":"plex://movie/5d776"},
				{"ratingKey":"11","type":"movie","title":"Big","year":1988,"Role":[{"id":5,"tag":"Tom Hanks"}]}]}}`)
		case "/library/metadata/10?":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"10","type":"movie","title":"Cast Away","viewCount":2}]}}`)
		case "/library/metadata/11?":
			w.WriteHeader(http.StatusUnauthorized)
		case "/library/sections/3/all?9":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"30","title":"Discovery","parentTitle":"Daft Punk","year":2001,"Genre":[{"tag":"House"}]}]}}`)
		case "/library/sections/3/all?8":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"31","title":"Daft Punk","summary":"French duo."}]}}`)
		default:
			http.NotFound(w, r)
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

func TestSmartLibraryReadsEveryoneCreditedAndTheTmdbID(t *testing.T) {
	lib, err := smartPlex(t).SmartLibrary(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(lib.Titles) != 2 || lib.Titles[0].TmdbID != "8358" || lib.Titles[0].Directors[0] != "Robert Zemeckis" {
		t.Fatalf("titles = %+v", lib.Titles)
	}
	if len(lib.Albums) != 1 || lib.Albums[0].Artists[0] != "Daft Punk" || len(lib.Artists) != 1 {
		t.Fatalf("music = %+v %+v", lib.Albums, lib.Artists)
	}
	// The person keeps the thumb from the title that had one.
	var hanks map[string]any
	if err := json.Unmarshal(lib.People["Tom Hanks"], &hanks); err != nil {
		t.Fatal(err)
	}
	if hanks["type"] != "person" || hanks["id"] != float64(5) || hanks["thumb"] != "http://img/hanks.jpg" {
		t.Fatalf("Tom Hanks = %v", hanks)
	}
}

func TestSmartItemsAreTheCallersView(t *testing.T) {
	items, err := smartPlex(t).SmartItems(context.Background(), api.Identity{Token: "member"}, []string{"10", "11"})
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 1 || items["10"] == nil {
		t.Fatalf("items = %v", items)
	}
	var movie map[string]any
	_ = json.Unmarshal(items["10"], &movie)
	if movie["viewCount"] != float64(2) {
		t.Fatalf("the caller's own watched state is lost: %v", movie)
	}
}
