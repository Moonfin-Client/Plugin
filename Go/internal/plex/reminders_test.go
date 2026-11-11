package plex

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// reminderPlex adds to fakePlex a movie library (1) where Fight Club (100)
// arrived a minute ago and an older movie three days ago, and a TV library
// (2) where Breaking Bad S5E14 (200) arrived just now. Only the owner reads
// the libraries this way.
func reminderPlex(t *testing.T) *Companion {
	f := newFakePlex(t)
	now := time.Now().Unix()
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		q := r.URL.Query()
		listing := r.Header.Get("X-Plex-Token") == "owner" && q.Get("sort") == "addedAt:desc" && q.Get("X-Plex-Container-Start") == "0"
		switch {
		case r.URL.Path == "/library/sections/1/all" && listing && q.Get("type") == "1" && q.Get("includeGuids") == "1":
			fmt.Fprintf(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"100","addedAt":%d,"Guid":[{"id":"imdb://tt0137523"},{"id":"tmdb://550"}]},`+
				`{"ratingKey":"90","addedAt":%d,"Guid":[{"id":"tmdb://1"}]}]}}`, now-60, now-3*86400)
		case r.URL.Path == "/library/sections/2/all" && listing && q.Get("type") == "4":
			fmt.Fprintf(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"200","addedAt":%d,"grandparentRatingKey":"20","parentIndex":5,"index":14}]}}`, now)
		case r.URL.Path == "/library/metadata/20" && r.Header.Get("X-Plex-Token") == "owner":
			io.WriteString(w, `{"MediaContainer":{"Metadata":[{"ratingKey":"20","guid":"plex://show/x","Guid":[{"id":"tvdb://81189"},{"id":"tmdb://1396"}]}]}}`)
		case r.URL.Path == "/library/sections/1/all", r.URL.Path == "/library/sections/2/all":
			w.WriteHeader(http.StatusBadRequest)
		default:
			f.pms.Config.Handler.ServeHTTP(w, r)
		}
	}))
	t.Cleanup(pms.Close)
	c, err := New(Config{PMS: pms.URL, PlexTV: f.tv.URL, DataDir: t.TempDir(), Version: "1.0.0"})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestArrivalsAreReadAsTheOwner(t *testing.T) {
	ctx := context.Background()
	c := reminderPlex(t)
	if got, err := c.RecentArrivals(ctx, time.Now().Add(-24*time.Hour)); got != nil || err != nil {
		t.Fatalf("before the owner signs in = %v %v", got, err)
	}
	if err := c.saveOwnerToken("owner"); err != nil {
		t.Fatal(err)
	}
	got, err := c.RecentArrivals(ctx, time.Now().Add(-24*time.Hour))
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 2 {
		t.Fatalf("arrivals = %+v", got)
	}
	movie, episode := got[0], got[1]
	if movie.ItemID != "100" || movie.IsEpisode || movie.TmdbID != "550" || movie.ImdbID != "tt0137523" {
		t.Fatalf("movie = %+v", movie)
	}
	if episode.ItemID != "200" || !episode.IsEpisode || episode.TvdbID != "81189" || episode.TmdbID != "1396" || *episode.Season != 5 || *episode.Episode != 14 {
		t.Fatalf("episode = %+v", episode)
	}
}

// Each account sees what Plex shows them: the owner everything, and members
// what's shared with them, read with the token the share list holds for them.
func TestCanSeeAsksPlexAsTheAccount(t *testing.T) {
	ctx := context.Background()
	c := reminderPlex(t)
	if ok, err := c.CanSee(ctx, 1, "200"); ok || err != nil {
		t.Fatalf("before the owner signs in = %v %v", ok, err)
	}
	if err := c.saveOwnerToken("owner"); err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		user int
		item string
		want bool
	}{
		{1, "200", true},
		{42, "100", true},
		{42, "200", false},
		{43, "100", true},
		{99, "100", false},
	} {
		if got, err := c.CanSee(ctx, tc.user, tc.item); got != tc.want || err != nil {
			t.Errorf("user %d item %s = %v %v", tc.user, tc.item, got, err)
		}
	}
}

func TestPlexServesRemindersAndTheStream(t *testing.T) {
	c := reminderPlex(t)
	var _ api.ReminderCatalog = c
	if _, ping := send(c, withToken("GET", "/Moonfin/Ping", "owner", "")); ping["calendarReminders"] != true {
		t.Fatalf("ping = %v", ping)
	}
	if w, got := send(c, withToken("POST", "/Moonfin/Calendar/Reminders", "owner", `{"kind":"movie","tmdbId":550,"title":"Fight Club"}`)); w.Code != 200 || got["key"] != "movie:550" {
		t.Fatalf("save = %d %v", w.Code, got)
	}
	if w, _ := send(c, withToken("POST", "/Moonfin/Notifications/Register", "owner", `{"token":"phone","platform":"android"}`)); w.Code != 200 {
		t.Fatalf("register = %d", w.Code)
	}

	srv := httptest.NewServer(c)
	t.Cleanup(srv.Close)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, "GET", srv.URL+"/Moonfin/Settings/Stream", nil)
	req.Header.Set("X-Plex-Token", "owner")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if line, _ := bufio.NewReader(resp.Body).ReadString('\n'); resp.StatusCode != 200 || line != ":connected\n" || resp.Header.Get("Access-Control-Allow-Origin") != "*" {
		t.Fatalf("stream = %d %q", resp.StatusCode, line)
	}
}
