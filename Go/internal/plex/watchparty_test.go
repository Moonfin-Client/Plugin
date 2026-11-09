package plex

import (
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/watchparty"
)

func partyCompanion(t *testing.T, f *fakePlex) *Companion {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "owner-token"), []byte("owner"), 0o600); err != nil {
		t.Fatal(err)
	}
	c, err := New(Config{PMS: f.pms.URL, PlexTV: f.tv.URL, DataDir: dir, Version: "1.0.0", WatchPartyDir: "../../../shared/WatchParty"})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func party(c *Companion, method, path, token, room, body string) (int, map[string]any) {
	r := withToken(method, path, token, body)
	r.Header.Set("X-Plex-Client-Identifier", "device-"+token)
	if room != "" {
		r.Header.Set("X-Moonfin-Room-Token", room)
	}
	w, out := send(c, r)
	return w.Code, out
}

func TestWatchPartiesCheckItemsWithPlexAsTheCaller(t *testing.T) {
	f := newFakePlex(t)
	c := partyCompanion(t, f)

	code, created := party(c, "POST", "/Moonfin/WatchParty/Rooms", "owner", "", `{"content_id":"200"}`)
	room, _ := created["room"].(map[string]any)
	if code != 200 || room["content_type"] != "Episode" || room["series_name"] != "Arcane" || room["season"] != 2.0 {
		t.Fatalf("create = %d %v", code, created)
	}
	id, ownerRoom := room["room_id"].(string), created["token"].(string)
	if want := "http://example.com/Moonfin/Party/" + room["code"].(string); room["invite_url"] != want {
		t.Errorf("invite_url = %v, want %s", room["invite_url"], want)
	}

	if _, list := party(c, "GET", "/Moonfin/WatchParty/Rooms", "member", "", ""); len(list["rooms"].([]any)) != 0 {
		t.Errorf("the member sees a room they can't watch: %v", list)
	}
	if code, out := party(c, "POST", "/Moonfin/WatchParty/Join", "member", "", `{"room_id":"`+id+`"}`); code != 403 || out["code"] != "no_access" {
		t.Errorf("join = %d %v", code, out)
	}
	calls := f.metadataCalls.Load()
	party(c, "POST", "/Moonfin/WatchParty/Join", "member", "", `{"room_id":"`+id+`"}`)
	if f.metadataCalls.Load() != calls {
		t.Error("a second check within the minute asked Plex again")
	}

	if code, out := party(c, "POST", "/Moonfin/WatchParty/Rooms/"+id+"/Stage", "owner", ownerRoom, `{"content_id":"100"}`); code != 200 {
		t.Fatalf("stage = %d %v", code, out)
	}
	code, joined := party(c, "POST", "/Moonfin/WatchParty/Join", "member", "", `{"room_id":"`+id+`"}`)
	if item := c.FindItem(t.Context(), watchparty.Caller{UserID: "1", Token: "owner"}, "100"); item == nil || item.RuntimeSeconds == nil || *item.RuntimeSeconds != 192 {
		t.Errorf("the movie's runtime = %v", item)
	}
	if code != 200 || joined["room"].(map[string]any)["members"].([]any)[1].(map[string]any)["display_name"] != "kid" {
		t.Errorf("join after staging the movie = %d %v", code, joined)
	}

	if code, _ := party(c, "GET", "/Moonfin/Admin/WatchParty/Rooms", "member", "", ""); code != http.StatusForbidden {
		t.Errorf("member admin list = %d", code)
	}
	if code, out := party(c, "GET", "/Moonfin/Admin/WatchParty/Rooms", "owner", "", ""); code != 200 || len(out["rooms"].([]any)) != 1 {
		t.Errorf("owner admin list = %d %v", code, out)
	}
	if code, _ := party(c, "GET", "/Moonfin/WatchParty/Config", "", "", ""); code != http.StatusUnauthorized {
		t.Errorf("signed out = %d", code)
	}
}

func TestTheOlderPerRoomRoutesStayWithTheSharedAPI(t *testing.T) {
	c := partyCompanion(t, newFakePlex(t))
	code, out := party(c, "GET", "/Moonfin/WatchParty/31366623-4606-488f-b5c0-c837c80d0227", "owner", "", "")
	if code != 200 || len(out) != 0 {
		t.Errorf("subtitle route = %d %v", code, out)
	}
}

func TestTheInvitePageNeedsNoSignIn(t *testing.T) {
	c := partyCompanion(t, newFakePlex(t))
	r := withToken("GET", "/Moonfin/Party/k7q29m", "", "")
	r.Header.Set("X-Forwarded-Proto", "https")
	r.Header.Set("X-Forwarded-Host", "party.example.com")
	w, _ := send(c, r)
	page := w.Body.String()
	for _, want := range []string{"Lakeside", "Room code K7Q 29M", "moonfin://party?server=m1&amp;code=K7Q29M&amp;address=https%3A%2F%2Fparty.example.com"} {
		if !strings.Contains(page, want) {
			t.Errorf("page lacks %q", want)
		}
	}
	if w, _ := send(c, withToken("GET", "/Moonfin/Party/preview.png", "", "")); w.Code != 200 || w.Header().Get("Content-Type") != "image/png" {
		t.Errorf("preview = %d %s", w.Code, w.Header().Get("Content-Type"))
	}
	if w, _ := send(c, withToken("GET", "/Moonfin/Party/nope", "", "")); w.Code != 404 {
		t.Errorf("bad code = %d", w.Code)
	}
}

func TestWatchPartySettingsComeFromTheAdminPage(t *testing.T) {
	c := partyCompanion(t, newFakePlex(t))
	if code, _ := party(c, "POST", "/Moonfin/Admin/Configuration", "owner", "", `{"WatchPartiesEnabled":false,"WatchPartyMaxMembers":4}`); code != http.StatusNoContent {
		t.Fatalf("save = %d", code)
	}
	if s := c.api.WatchPartySettings(t.Context()); s.Enabled || s.MaxMembers != 4 || !s.Chat {
		t.Errorf("settings = %+v", s)
	}
}
