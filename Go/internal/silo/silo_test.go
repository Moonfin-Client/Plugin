package silo

import (
	"context"
	"encoding/json"
	"net/http"
	"slices"
	"strconv"
	"testing"

	pluginv1 "github.com/Silo-Server/silo-plugin-sdk/pkg/pluginproto/silo/plugin/v1"
	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtimehost"
	"google.golang.org/protobuf/types/known/structpb"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

func TestStoreLimitsMatchTheSDK(t *testing.T) {
	if store.MaxKeys != runtimehost.MaxInstanceStateKeys || store.MaxValueBytes != runtimehost.MaxInstanceStateValueBytes {
		t.Fatalf("store limits %d/%d, SDK %d/%d", store.MaxKeys, store.MaxValueBytes,
			runtimehost.MaxInstanceStateKeys, runtimehost.MaxInstanceStateValueBytes)
	}
}

func TestIdentifyTrustsOnlySiloHeaders(t *testing.T) {
	r, _ := http.NewRequest("GET", "/", nil)
	if _, err := Identify(r); err != api.ErrSignedOut {
		t.Fatalf("anonymous = %v", err)
	}
	r.Header.Set("X-Silo-User-Id", "7")
	r.Header.Set("X-Silo-User-Role", "admin")
	r.Header.Set("X-Silo-Profile-Id", "p7")
	r.Header.Set("X-Silo-Profile-Name", "Main")
	r.Header.Set("X-Silo-Profile-Primary", "true")
	id, err := Identify(r)
	if err != nil || id.UserID != 7 || !id.Admin || id.ProfileID != "p7" || id.ProfileName != "Main" || !id.Primary {
		t.Fatalf("identity = %+v %v", id, err)
	}
}

// Handle replays Silo's request through the shared handler: the query comes
// back as strings, headers pass through, and the reply's status and body
// make it out.
func TestHandleReplaysTheRequest(t *testing.T) {
	p := New("9.9.9")
	query, _ := structpb.NewStruct(map[string]any{"imdbId": "tt1", "n": 3.0})
	resp, err := p.Handle(context.Background(), &pluginv1.HandleHTTPRequest{
		Method: "GET", Path: "/Moonfin/Ping", Query: query,
	})
	if err != nil || resp.GetStatusCode() != http.StatusUnauthorized {
		t.Fatalf("anonymous ping = %d %v", resp.GetStatusCode(), err)
	}
	var body map[string]any
	if err := json.Unmarshal(resp.GetBody(), &body); err != nil || body["Error"] != "Sign in to Silo first." {
		t.Fatalf("body = %s", resp.GetBody())
	}
	if stringify(3.0) != "3" || stringify(nil) != "" || stringify("x") != "x" {
		t.Fatal("query values must arrive as strings")
	}
}

func TestConfigureHandsTheOldFormToTheServer(t *testing.T) {
	p := New("1")
	v, _ := structpb.NewStruct(map[string]any{"TmdbApiKey": "k"})
	if err := p.Configure(context.Background(), []*pluginv1.ConfigEntry{{Key: "moonbase", Value: v}}); err != nil {
		t.Fatal(err)
	}
	// There's no Silo host in a test, so storage fails and Ping runs on the
	// form's values alone.
	resp, err := p.Handle(context.Background(), &pluginv1.HandleHTTPRequest{
		Method: "GET", Path: "/Moonfin/Ping", Headers: map[string]string{"X-Silo-User-Id": "2"},
	})
	var body map[string]any
	if err != nil || json.Unmarshal(resp.GetBody(), &body) != nil || body["tmdbAvailable"] != true || body["serverName"] != "Silo" {
		t.Fatalf("ping = %s %v", resp.GetBody(), err)
	}
}

// fakeHost serves the caller's libraries and their items two to a page.
type fakeHost struct {
	libs     []*pluginv1.Library
	items    []runtimehost.CatalogMediaItem
	requests []runtimehost.ListLibraryMediaRequest
}

func (f *fakeHost) ListLibraries(context.Context, string) ([]*pluginv1.Library, error) {
	return f.libs, nil
}

func (f *fakeHost) ListLibraryMedia(_ context.Context, req runtimehost.ListLibraryMediaRequest) (*runtimehost.ListLibraryMediaResponse, error) {
	f.requests = append(f.requests, req)
	start, _ := strconv.Atoi(req.PageToken)
	end := min(start+2, len(f.items))
	next := ""
	if end < len(f.items) {
		next = strconv.Itoa(end)
	}
	return &runtimehost.ListLibraryMediaResponse{Items: f.items[start:end], NextPageToken: next}, nil
}

func newFakeCatalog(f *fakeHost) *hostCatalog {
	return &hostCatalog{host: func() siloHost { return f }, genres: map[string]cachedGenres{}}
}

func TestGenresWalkTheCallersLibrariesOnce(t *testing.T) {
	f := &fakeHost{
		libs: []*pluginv1.Library{{Id: "2", Name: "Shows", MediaType: "tv"}, {Id: "1", Name: "Movies", MediaType: "movie"}},
		items: []runtimehost.CatalogMediaItem{
			{Genres: []string{"Drama", "Action & Adventure"}},
			{Genres: []string{"drama"}},
			{Genres: []string{"Kids' (Family)*!~"}},
			{Genres: []string{""}},
			{Genres: []string{"Science Fiction"}},
		},
	}
	c := newFakeCatalog(f)
	got, err := c.Genres(context.Background(), api.Identity{UserID: 3})
	if err != nil {
		t.Fatal(err)
	}
	want := []api.Genre{
		{ID: "silo-genre:Drama", Name: "Drama"},
		{ID: "silo-genre:Action%20%26%20Adventure", Name: "Action & Adventure"},
		{ID: "silo-genre:Kids'%20(Family)*!~", Name: "Kids' (Family)*!~"},
		{ID: "silo-genre:Science%20Fiction", Name: "Science Fiction"},
	}
	if !slices.Equal(got, want) {
		t.Fatalf("genres = %v, want %v", got, want)
	}
	if len(f.requests) != 3 || !slices.Equal(f.requests[0].LibraryIDs, []string{"1", "2"}) || f.requests[2].PageToken != "4" {
		t.Fatalf("requests = %+v", f.requests)
	}
	if _, err := c.Genres(context.Background(), api.Identity{UserID: 3}); err != nil || len(f.requests) != 3 {
		t.Fatalf("a second ask within the cache time must not walk the catalog again: %d requests, %v", len(f.requests), err)
	}
}

func TestGenresWithNoLibrariesAskForNothing(t *testing.T) {
	f := &fakeHost{items: []runtimehost.CatalogMediaItem{{Genres: []string{"Drama"}}}}
	got, err := newFakeCatalog(f).Genres(context.Background(), api.Identity{UserID: 3})
	if err != nil || len(got) != 0 || len(f.requests) != 0 {
		t.Fatalf("no libraries = %v %v, %d requests", got, err, len(f.requests))
	}
}

// Silo keeps PINs on its profiles, so Moonbase's PIN routes stay unanswered there.
func TestPinRoutesAreNotServedOnSilo(t *testing.T) {
	p := New("1")
	for _, path := range []string{"/Moonfin/Pin", "/Moonfin/Admin/Pins"} {
		resp, err := p.Handle(context.Background(), &pluginv1.HandleHTTPRequest{
			Method: "GET", Path: path, Headers: map[string]string{"X-Silo-User-Id": "2", "X-Silo-User-Role": "admin"},
		})
		if err != nil || resp.GetStatusCode() != http.StatusNotFound {
			t.Fatalf("%s = %d %v", path, resp.GetStatusCode(), err)
		}
	}
}
