package plex

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"sync/atomic"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

func TestGameLibrariesAreTheCallersWithTheirFolders(t *testing.T) {
	var ownerCalls atomic.Int32
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/library/sections" {
			http.NotFound(w, r)
			return
		}
		switch r.Header.Get("X-Plex-Token") {
		case "owner":
			ownerCalls.Add(1)
			io.WriteString(w, `{"MediaContainer":{"Directory":[
				{"key":"1","title":"Movies","Location":[{"path":"/data/movies"}]},
				{"key":"5","title":"Games","Location":[{"path":"/data/games"},{"path":"/mnt/roms"}]},
				{"key":"6","title":"Private","Location":[{"path":"/data/private"}]}]}}`)
		case "member":
			io.WriteString(w, `{"MediaContainer":{"Directory":[{"key":"1","title":"Movies"},{"key":"5","title":"Games"}]}}`)
		default:
			w.WriteHeader(http.StatusUnauthorized)
		}
	}))
	defer pms.Close()
	c, err := New(Config{PMS: pms.URL, PlexTV: pms.URL, DataDir: t.TempDir(), EmulatorJSDir: "/emulatorjs"})
	if err != nil {
		t.Fatal(err)
	}

	owner, err := c.GameLibraries(context.Background(), api.Identity{Admin: true, Token: "owner"})
	if err != nil || len(owner) != 3 || owner[1].ID != "section-5" || owner[1].ConfigID != "5" || owner[1].Name != "Games" ||
		!reflect.DeepEqual(owner[1].Folders, []string{"/data/games", "/mnt/roms"}) || ownerCalls.Load() != 1 {
		t.Fatalf("owner = %+v, %v", owner, err)
	}

	// Before the owner signs in to the admin page, a member's folders aren't known.
	member, err := c.GameLibraries(context.Background(), api.Identity{Token: "member"})
	if err != nil || len(member) != 2 || member[1].Folders != nil {
		t.Fatalf("member before the owner signs in = %+v, %v", member, err)
	}
	if err := c.saveOwnerToken("owner"); err != nil {
		t.Fatal(err)
	}
	member, err = c.GameLibraries(context.Background(), api.Identity{Token: "member"})
	want := []api.GameLibrary{
		{ID: "section-1", ConfigID: "1", Name: "Movies", Folders: []string{"/data/movies"}},
		{ID: "section-5", ConfigID: "5", Name: "Games", Folders: []string{"/data/games", "/mnt/roms"}},
	}
	if err != nil || !reflect.DeepEqual(member, want) {
		t.Fatalf("member = %+v, %v", member, err)
	}
	if c.GamesDir() == "" || c.EmulatorJSDir() != "/emulatorjs" {
		t.Fatal("the games folders aren't set")
	}
}
