package api

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"reflect"
	"slices"
	"strconv"
	"strings"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

type fakeCatalog struct{}

func (fakeCatalog) Libraries(context.Context, Identity) ([]Library, error) {
	return []Library{{ID: "1", Name: "Movies", MediaType: "movie"}}, nil
}

// caller is the identity Silo's proxy stamps on a request. The zero value is
// an anonymous caller. admin is Silo's acting-admin decision, so an admin
// account's child profile reaches the plugin as a user.
type caller struct {
	user      int
	admin     bool
	profileID string
	profile   string
	name      string
	primary   bool
}

var (
	anon      = caller{}
	adminMain = caller{user: 2, admin: true, profileID: "20", profile: "test"}
	adminKid  = caller{user: 2, profileID: "21", profile: "Kid"}
	userMain  = caller{user: 3, profileID: "30", profile: "Main", name: "pat", primary: true}
	userKids  = caller{user: 3, profileID: "31", profile: "Kids", name: "pat"}
	other     = caller{user: 4, profileID: "40", profile: "Main"}
)

func (c caller) headers() map[string]string {
	h := map[string]string{"Content-Type": "application/json"}
	if c.user == 0 {
		return h
	}
	h["X-Silo-User-Id"] = fmt.Sprint(c.user)
	h["X-Silo-User-Role"] = "user"
	if c.primary {
		h["X-Silo-Profile-Primary"] = "true"
	}
	if c.admin {
		h["X-Silo-User-Role"] = "admin"
	}
	if c.profileID != "" {
		h["X-Silo-Profile-Id"] = c.profileID
	}
	if c.profile != "" {
		h["X-Silo-Profile-Name"] = c.profile
	}
	if c.name != "" {
		h["X-Silo-User-Name"] = c.name
	}
	return h
}

// headerIdentity reads the headers caller.headers sets, the way silo.Identify
// does.
func headerIdentity(r *http.Request) (Identity, error) {
	id, err := strconv.Atoi(r.Header.Get("X-Silo-User-Id"))
	if err != nil || id <= 0 {
		return Identity{}, ErrSignedOut
	}
	return Identity{
		UserID:      id,
		Admin:       r.Header.Get("X-Silo-User-Role") == "admin",
		UserName:    r.Header.Get("X-Silo-User-Name"),
		ProfileID:   r.Header.Get("X-Silo-Profile-Id"),
		ProfileName: r.Header.Get("X-Silo-Profile-Name"),
		Primary:     r.Header.Get("X-Silo-Profile-Primary") == "true",
	}, nil
}

func testHost(kv store.KV) Host {
	return Host{Name: "Silo", KV: kv, Catalog: fakeCatalog{}, Identify: headerIdentity, AdminBackURL: "/admin/plugins"}
}

func newTestServer() *Server {
	return NewServer("1.2.3", testHost(store.NewMemKV()))
}

// serve runs one request through the handler. Query values that aren't
// strings are sent as JSON, the way Silo's proxy passes them on.
func serve(ctx context.Context, s *Server, c caller, method, path, body string, query map[string]any) *http.Response {
	q := url.Values{}
	for k, v := range query {
		if str, ok := v.(string); ok {
			q.Set(k, str)
		} else {
			b, _ := json.Marshal(v)
			q.Set(k, string(b))
		}
	}
	target := (&url.URL{Path: path, RawQuery: q.Encode()}).RequestURI()
	r := httptest.NewRequestWithContext(ctx, method, target, strings.NewReader(body))
	for k, v := range c.headers() {
		r.Header.Set(k, v)
	}
	w := httptest.NewRecorder()
	s.ServeHTTP(w, r)
	return w.Result()
}

func readAll(t *testing.T, resp *http.Response) []byte {
	t.Helper()
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func call(t *testing.T, s *Server, c caller, method, path, body string, query map[string]any) (int, map[string]any) {
	t.Helper()
	resp := serve(context.Background(), s, c, method, path, body, query)
	raw := readAll(t, resp)
	var out map[string]any
	if len(raw) > 0 && strings.HasPrefix(resp.Header.Get("Content-Type"), "application/json") {
		if err := json.Unmarshal(raw, &out); err != nil {
			t.Fatalf("%s %s: bad JSON %q", method, path, raw)
		}
	}
	return resp.StatusCode, out
}

func TestAuthorizationMatrix(t *testing.T) {
	s := newTestServer()
	cases := []struct {
		c            caller
		method, path string
		want         int
	}{
		{anon, "GET", "/Moonfin/Ping", 401},
		{userKids, "GET", "/Moonfin/Ping", 200},
		{anon, "GET", "/Moonfin/Settings/Resolved/tv", 401},
		{userKids, "GET", "/Moonfin/Settings/Resolved/tv", 200},
		{userKids, "GET", "/Moonfin/Settings/Resolved/phone", 400},
		{userMain, "GET", "/Moonfin/Admin/Configuration", 403},
		{adminKid, "GET", "/Moonfin/Admin/Configuration", 403},
		{adminMain, "GET", "/Moonfin/Admin/Configuration", 200},
		{userMain, "GET", "/admin", 403},
		{adminMain, "GET", "/admin", 200},
		{userMain, "GET", "/Moonfin/Admin/Users", 403},
		{userMain, "POST", "/Moonfin/Admin/PushDefaults", 403},
		{adminMain, "GET", "/Moonfin/Settings/Stream", 501},
		{userMain, "GET", "/Moonfin/Nope", 404},
	}
	for _, tc := range cases {
		if got, _ := call(t, s, tc.c, tc.method, tc.path, "", nil); got != tc.want {
			t.Errorf("%+v %s %s = %d, want %d", tc.c, tc.method, tc.path, got, tc.want)
		}
	}
}

func TestSettingsStayWithTheirProfile(t *testing.T) {
	s := newTestServer()
	if code, _ := call(t, s, userKids, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"posterSize":"small"},"clientId":"x"}`, nil); code != 200 {
		t.Fatalf("save = %d", code)
	}
	if _, got := call(t, s, userKids, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["posterSize"] != "small" {
		t.Fatalf("kids profile lost its setting: %v", got)
	}
	for _, c := range []caller{userMain, other, adminMain} {
		if _, got := call(t, s, c, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["posterSize"] != nil {
			t.Fatalf("%+v sees another profile's setting: %v", c, got)
		}
		if code, _ := call(t, s, c, "GET", "/Moonfin/Settings", "", nil); code != 404 {
			t.Fatalf("%+v reads another profile's envelope: %d", c, code)
		}
	}
	renamed := userKids
	renamed.profile = "Little ones"
	if _, got := call(t, s, renamed, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["posterSize"] != "small" {
		t.Fatalf("a renamed profile lost its settings: %v", got)
	}
	if code, _ := call(t, s, userKids, "DELETE", "/Moonfin/Settings", "", nil); code != 200 {
		t.Fatal("delete failed")
	}
	if code, _ := call(t, s, userKids, "GET", "/Moonfin/Settings", "", nil); code != 404 {
		t.Fatal("settings survived delete")
	}
}

func TestAdminDefaultsAndPush(t *testing.T) {
	s := newTestServer()
	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"b": 2, "c": nil}})
	if _, got := call(t, s, other, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); got["b"] != 2.0 {
		t.Fatalf("a new profile must get the defaults: %v", got)
	}
	if _, got := call(t, s, userKids, "GET", "/Moonfin/Ping", "", nil); got["defaultSettings"].(map[string]any)["b"] != 2.0 {
		t.Fatalf("ping must carry the defaults: %v", got)
	}

	code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", map[string]any{"overwrite": "true"})
	if code != 200 || got["usersAffected"] != 1.0 {
		t.Fatalf("push = %d %v", code, got)
	}
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["tv"] != nil || env["global"].(map[string]any)["b"] != 2.0 {
		t.Fatalf("overwrite must reset to the defaults: %v", env)
	}
	if _, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", map[string]any{"overwrite": "True"}); got["overwrite"] != true {
		t.Fatalf("overwrite must ignore case: %v", got)
	}
}

// As on Jellyfin, changing a default leaves what users have stored alone.
// Apply Defaults is what moves them onto it.
func TestChangedDefaultsWaitForApplyDefaults(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"style": "classic"}})
	// Apps send every setting they hold, the ones from the defaults included.
	call(t, s, userMain, "POST", "/Moonfin/Settings/Profile/global", `{"profile":{"style":"classic"}}`, nil)
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"style": "spotlight"}})
	style := func() any {
		_, got := call(t, s, userMain, "GET", "/Moonfin/Settings/Resolved/mobile", "", nil)
		return got["style"]
	}
	if got := style(); got != "classic" {
		t.Fatalf("a changed default must not replace a stored value, got %v", got)
	}
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", nil); code != 200 {
		t.Fatalf("push = %d", code)
	}
	if got := style(); got != "spotlight" {
		t.Fatalf("Apply Defaults must move the user onto the new default, got %v", got)
	}
}

func TestDeviceDefaults(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{
		"DefaultUserSettings":   map[string]any{"style": "classic", "size": "m"},
		"DefaultDeviceSettings": map[string]any{"tv": map[string]any{"style": "modern", "x": nil}, "bogus": map[string]any{"a": 1}},
	})
	if _, cfg := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil); !reflect.DeepEqual(cfg["DefaultDeviceSettings"], map[string]any{"tv": map[string]any{"style": "modern"}}) {
		t.Fatalf("device defaults must keep only cleaned device profiles: %v", cfg["DefaultDeviceSettings"])
	}
	resolved := func(c caller, profile string) map[string]any {
		_, got := call(t, s, c, "GET", "/Moonfin/Settings/Resolved/"+profile, "", nil)
		return got
	}
	if tv, mobile := resolved(userMain, "tv"), resolved(userMain, "mobile"); tv["style"] != "modern" || tv["size"] != "m" || mobile["style"] != "classic" {
		t.Fatalf("tv must get its own default over the shared ones: tv %v mobile %v", tv, mobile)
	}

	// As on Jellyfin, what users stored stays when a default changes, and
	// Apply Defaults moves each profile onto its own defaults.
	call(t, s, other, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"style":"spotlight","theme":"dark"}}`, nil)
	call(t, s, other, "POST", "/Moonfin/Settings/Profile/mobile", `{"profile":{"theme":"dark"}}`, nil)
	saveConfig(t, s, map[string]any{
		"DefaultUserSettings":   map[string]any{"style": "classic", "size": "m"},
		"DefaultDeviceSettings": map[string]any{"tv": map[string]any{"style": "retro", "theme": "light"}},
	})
	if got := resolved(other, "tv"); got["style"] != "spotlight" || got["theme"] != "dark" {
		t.Fatalf("a changed default must not replace stored values: %v", got)
	}
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", nil); code != 200 {
		t.Fatalf("push = %d", code)
	}
	if got := resolved(other, "tv"); got["style"] != "retro" || got["theme"] != "light" {
		t.Fatalf("Apply Defaults must give tv its own defaults: %v", got)
	}
	if got := resolved(other, "mobile"); got["style"] != "classic" || got["theme"] != "dark" {
		t.Fatalf("a tv default must leave mobile alone: %v", got)
	}

	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", map[string]any{"overwrite": "true"}); code != 200 {
		t.Fatalf("reset = %d", code)
	}
	if tv, mobile := resolved(other, "tv"), resolved(other, "mobile"); tv["style"] != "retro" || tv["size"] != "m" || mobile["style"] != "classic" || mobile["theme"] != nil {
		t.Fatalf("a reset must keep each device on its own defaults: tv %v mobile %v", tv, mobile)
	}
}

func TestWatchPartySubtitlesBelongToTheirHost(t *testing.T) {
	s := newTestServer()
	const path = "/Moonfin/WatchParty/31366623-4606-488f-b5c0-c837c80d0227"
	if _, got := call(t, s, other, "GET", path, "", nil); len(got) != 0 {
		t.Fatalf("an unknown room has no choice: %v", got)
	}
	if code, _ := call(t, s, userMain, "PUT", path, `{"subtitle":{"index":3,"language":"eng"}}`, nil); code != 200 {
		t.Fatalf("the first writer sets it, got %d", code)
	}
	if _, got := call(t, s, other, "GET", path, "", nil); got["subtitle"].(map[string]any)["index"] != 3.0 {
		t.Fatalf("members read the host's choice: %v", got)
	}
	if code, _ := call(t, s, other, "PUT", path, `{"subtitle":{"off":true}}`, nil); code != http.StatusForbidden {
		t.Fatalf("only the room's owner may change it, got %d", code)
	}
	if code, _ := call(t, s, userKids, "PUT", path, `{"subtitle":null}`, nil); code != http.StatusForbidden {
		t.Fatalf("another profile of the owner's account is not the owner, got %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", path, `{"subtitle":null}`, nil); code != 200 {
		t.Fatalf("the owner clears it, got %d", code)
	}
	for _, bad := range []string{`{}`, `null`, `{"other":1}`, `{"subtitle":3}`} {
		if code, _ := call(t, s, userMain, "PUT", path, bad, nil); code != 400 {
			t.Fatalf("%s must be refused, got %d", bad, code)
		}
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/WatchParty/not-a-room", "", nil); code != 400 {
		t.Fatalf("a malformed room id is refused, got %d", code)
	}
}

func TestWatchPartyAvatarsAreEachMembersOwn(t *testing.T) {
	s := newTestServer()
	const path = "/Moonfin/WatchParty/41366623-4606-488f-b5c0-c837c80d0227"
	// A guest posting first doesn't make them the subtitles' owner.
	if code, _ := call(t, s, other, "PUT", path+"/Avatar", `{"url":"/avatars/presets/fox.svg"}`, nil); code != 200 {
		t.Fatalf("a member posts their avatar, got %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", path+"/Avatar", `{"url":"https://api.dicebear.com/9.x/x/svg?seed=a"}`, nil); code != 200 {
		t.Fatalf("another member posts theirs, got %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", path, `{"subtitle":{"off":true}}`, nil); code != 200 {
		t.Fatalf("the host still claims the subtitles, got %d", code)
	}
	_, got := call(t, s, userKids, "GET", path, "", nil)
	avatars, _ := got["avatars"].(map[string]any)
	if avatars["40"] != "/avatars/presets/fox.svg" || avatars["30"] == nil {
		t.Fatalf("avatars by profile id: %v", got)
	}
	for _, bad := range []string{`{"url":"javascript:alert(1)"}`, `{"url":"avatars/x.svg"}`, `{"url":""}`} {
		if code, _ := call(t, s, other, "PUT", path+"/Avatar", bad, nil); code != 400 {
			t.Fatalf("%s must be refused, got %d", bad, code)
		}
	}
}

func TestAdminTargetsOneProfile(t *testing.T) {
	s := newTestServer()
	for _, c := range []caller{userMain, userKids} {
		call(t, s, c, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	}
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"b": 2}})
	body := readAll(t, serve(context.Background(), s, adminMain, "GET", "/Moonfin/Admin/Users", "", nil))
	if !strings.Contains(string(body), `"Profiles":[{"Id":"3~30","Name":"Main","Primary":true},{"Id":"3~31","Name":"Kids","Primary":false}]`) {
		t.Fatalf("users must list their profiles: %s", body)
	}
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults/3~31", "", map[string]any{"overwrite": "true"}); code != 200 {
		t.Fatalf("push to one profile = %d", code)
	}
	if _, env := call(t, s, userKids, "GET", "/Moonfin/Settings", "", nil); env["tv"] != nil {
		t.Fatalf("the picked profile must be reset: %v", env)
	}
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["tv"] == nil {
		t.Fatalf("the account's other profile must stay: %v", env)
	}
	if code, got := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Users/3~31", "", nil); code != 200 || got["profilesRemoved"] != 1.0 {
		t.Fatalf("delete one profile = %d %v", code, got)
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); code != 200 {
		t.Fatal("the account's other profile must keep its settings")
	}
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Users/x~31", "", nil); code != 400 {
		t.Fatalf("a malformed target is refused, got %d", code)
	}
}

func TestDeleteUserRemovesEveryProfile(t *testing.T) {
	s := newTestServer()
	for _, c := range []caller{userMain, userKids, other} {
		call(t, s, c, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
		call(t, s, c, "POST", "/Moonfin/Collections/42/Order", `["a"]`, nil)
	}
	if code, _ := call(t, s, userMain, "DELETE", "/Moonfin/Admin/Users/3", "", nil); code != http.StatusForbidden {
		t.Fatalf("only an admin may remove a user's data, got %d", code)
	}
	if code, got := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Users/3", "", nil); code != 200 || got["profilesRemoved"] != 2.0 {
		t.Fatalf("delete = %d %v", code, got)
	}
	for _, c := range []caller{userMain, userKids} {
		if code, _ := call(t, s, c, "GET", "/Moonfin/Settings", "", nil); code != http.StatusNotFound {
			t.Fatalf("%v kept settings", c)
		}
		if raw := readAll(t, serve(context.Background(), s, c, "GET", "/Moonfin/Collections/42/Order", "", nil)); string(bytes.TrimSpace(raw)) != "[]" {
			t.Fatalf("%v kept a collection order: %s", c, raw)
		}
	}
	if code, _ := call(t, s, other, "GET", "/Moonfin/Settings", "", nil); code != 200 {
		t.Fatal("another account's settings must stay")
	}
	var index []indexEntry
	if _, err := s.loadJSON(context.Background(), keyIndex, &index); err != nil || len(index) != 1 || index[0].UserID != 4 {
		t.Fatalf("index = %v %v", index, err)
	}
}

func TestStorageLimitLeavesNoPhantomProfile(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"DefaultUserSettings": map[string]any{"b": 2}})
	refused := 0
	for i := range store.MaxKeys {
		if code, _ := call(t, s, caller{user: 100 + i, profile: "P"}, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil); code != 200 {
			refused = 100 + i
			break
		}
	}
	if refused == 0 {
		t.Fatal("the key limit was never reached")
	}
	var index []indexEntry
	if _, err := s.loadJSON(context.Background(), keyIndex, &index); err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(index, func(e indexEntry) bool { return e.UserID == refused }) {
		t.Fatal("a refused save must not leave its profile in the index")
	}
	for _, q := range []map[string]any{nil, {"overwrite": "true"}} {
		if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/PushDefaults", "", q); code != 200 {
			t.Fatalf("push %v at the key limit = %d %v", q, code, got)
		}
	}
}

func TestWholeEnvelopeSaveKeepsSyncOn(t *testing.T) {
	s := newTestServer()
	call(t, s, userMain, "POST", "/Moonfin/Settings", `{"settings":{"global":{"a":1}},"mergeMode":"replace"}`, nil)
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["syncEnabled"] != true {
		t.Fatalf("a replace without syncEnabled must store it on: %v", env)
	}
	call(t, s, userMain, "POST", "/Moonfin/Settings", `{"settings":{"syncEnabled":false}}`, nil)
	if _, env := call(t, s, userMain, "GET", "/Moonfin/Settings", "", nil); env["syncEnabled"] != false {
		t.Fatalf("a merge must keep the sent syncEnabled: %v", env)
	}
}

func TestDetailsBlurRoundTrip(t *testing.T) {
	s := newTestServer()
	code, got := call(t, s, userMain, "POST", "/Moonfin/Settings/detailsScreenBlur/tv", `{"detailsScreenBlur":"55"}`, nil)
	if code != 200 || got["DetailsScreenBlur"] != "40" {
		t.Fatalf("blur save = %d %v", code, got)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Settings/detailsScreenBlur/tv", "", nil); got["DetailsScreenBlur"] != "40" {
		t.Fatalf("blur read = %v", got)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Settings/detailsScreenBlur", `{"detailsScreenBlur":"lots"}`, nil); code != 400 {
		t.Fatalf("non-numeric blur = %d", code)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Settings/detailsScreenOpacity", "", nil); got["DetailsScreenOpacity"] != 90.0 {
		t.Fatalf("default opacity = %v", got)
	}
}

func TestSyncDisabledTurnsSettingsOff(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"EnableSettingsSync": false, "TmdbApiKey": " k "})
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Settings/Resolved/tv", "", nil); code != http.StatusServiceUnavailable {
		t.Fatalf("resolved = %d", code)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); got["settingsSyncEnabled"] != false || got["tmdbAvailable"] != true {
		t.Fatalf("ping = %v", got)
	}
}

func TestMessagesReachTheirAudience(t *testing.T) {
	s := newTestServer()
	post := func(body string) map[string]any {
		t.Helper()
		code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Messages", body, nil)
		if code != 200 {
			t.Fatalf("post %s = %d %v", body, code, got)
		}
		return got["item"].(map[string]any)
	}
	post(`{"title":"For all","color":"purple","actionUrl":"javascript:alert(1)","actionLabel":"Go"}`)
	post(`{"title":"For admins","audience":"admins"}`)
	post(`{"title":"For user 3","audience":"users","targetUserIds":["3","x","3"]}`)
	post(`{"title":"Over","endUtc":"2000-01-01T00:00:00Z"}`)
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Admin/Messages", `{"title":"x"}`, nil); code != 403 {
		t.Fatal("a user must not post messages")
	}

	titles := func(c caller) []string {
		_, got := call(t, s, c, "GET", "/Moonfin/Messages", "", nil)
		var out []string
		for _, item := range got["items"].([]any) {
			out = append(out, item.(map[string]any)["Title"].(string))
		}
		return out
	}
	if got := strings.Join(titles(userKids), ","); got != "For user 3,For all" {
		t.Errorf("user 3 sees %q", got)
	}
	if got := strings.Join(titles(other), ","); got != "For all" {
		t.Errorf("user 4 sees %q", got)
	}
	if got := strings.Join(titles(adminKid), ","); got != "For all" {
		t.Errorf("an admin's child profile sees %q", got)
	}
	if got := strings.Join(titles(adminMain), ","); got != "For admins,For all" {
		t.Errorf("admin sees %q", got)
	}

	_, all := call(t, s, adminMain, "GET", "/Moonfin/Admin/Messages", "", nil)
	first := all["items"].([]any)[2].(map[string]any)
	if first["Color"] != "white" || first["ActionUrl"] != nil || first["ActionLabel"] != nil {
		t.Errorf("sanitize let through %v", first)
	}
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Messages/"+first["Id"].(string), "", nil); code != 200 {
		t.Fatal("delete failed")
	}
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Messages/nope", "", nil); code != 404 {
		t.Fatal("deleting a missing message must 404")
	}
}

func TestDeletingNothingUsesNoStorage(t *testing.T) {
	kv := store.NewMemKV()
	s := NewServer("1", testHost(kv))
	for i := range 5 {
		c := caller{user: 10 + i, profile: "P"}
		if code, _ := call(t, s, c, "DELETE", "/Moonfin/Settings", "", nil); code != 200 {
			t.Fatalf("delete = %d", code)
		}
	}
	if _, found, _ := kv.Get(context.Background(), settingsKey(Identity{UserID: 10, ProfileName: "P"})); found {
		t.Fatal("deleting settings that were never stored must not write a key")
	}
}

// saveConfig saves plugin settings the way the admin page does.
func saveConfig(t *testing.T, s *Server, config map[string]any) {
	t.Helper()
	body, _ := json.Marshal(config)
	if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Configuration", string(body), nil); code != http.StatusNoContent {
		t.Fatalf("save config = %d %v", code, got)
	}
}

func TestAdminConfigurationRoundTrip(t *testing.T) {
	s := newTestServer()
	call(t, s, adminMain, "POST", "/Moonfin/Admin/Messages", `{"title":"Hi"}`, nil)
	_, got := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	if got["EnableSettingsSync"] != true || got["MdblistOfficialListsMaxItems"] != 250.0 || len(got["Messages"].([]any)) != 1 {
		t.Fatalf("a fresh install must answer Jellyfin's defaults and the stored messages: %v", got)
	}

	got["TmdbApiKey"] = "k"
	got["DefaultUserSettings"] = map[string]any{"NavbarPosition": "bottom", "x": nil}
	got["Messages"] = []any{}
	saveConfig(t, s, got)

	_, again := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	if again["TmdbApiKey"] != "k" || len(again["Messages"].([]any)) != 1 {
		t.Fatalf("saving the page must keep its fields and leave messages alone: %v", again)
	}
	if d := again["DefaultUserSettings"].(map[string]any); d["navbarPosition"] != "bottom" || len(d) != 1 {
		t.Fatalf("defaults must be stored cleaned: %v", d)
	}
	if _, ping := call(t, s, userKids, "GET", "/Moonfin/Ping", "", nil); ping["tmdbAvailable"] != true {
		t.Fatal("ping must read the saved key")
	}
}

func TestAdminUsersComeFromSyncedProfiles(t *testing.T) {
	s := newTestServer()
	for _, c := range []caller{userMain, userKids, adminMain} {
		call(t, s, c, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	}
	resp := serve(context.Background(), s, adminMain, "GET", "/Moonfin/Admin/Users", "", nil)
	var users []map[string]any
	if err := json.Unmarshal(readAll(t, resp), &users); err != nil {
		t.Fatal(err)
	}
	if len(users) != 2 || users[0]["Id"] != "3" || users[0]["Name"] != "pat" || users[1]["Name"] != "Account 2" ||
		users[1]["Policy"].(map[string]any)["IsAdministrator"] != true {
		t.Fatalf("users = %v", users)
	}
}

func TestAdminPageServesTheJellyfinPage(t *testing.T) {
	s := newTestServer()
	page := string(readAll(t, serve(context.Background(), s, adminMain, "GET", "/admin", "", nil)))
	for _, want := range []string{`<script src="admin-assets/host.js?v=1.2.3">`, `id="MoonfinConfigPage"`, "MoonfinConfig.pluginUniqueId",
		`window.moonbaseHost = {"name":"Silo"};`, `href="/admin/plugins" title="Back to Silo"`, "Moonbase 1.2.3 for Silo"} {
		if !strings.Contains(page, want) {
			t.Errorf("page is missing %q", want)
		}
	}
	for _, asset := range []string{"/admin-assets/host.js", "/admin-assets/host.css", "/admin-assets/fonts/MaterialIcons-Regular.woff2", "/Moonfin/Assets/icon.png"} {
		resp := serve(context.Background(), s, adminMain, "GET", asset, "", nil)
		if resp.StatusCode != 200 || resp.Header.Get("Content-Type") == "" || len(readAll(t, resp)) == 0 {
			t.Errorf("%s = %d %q", asset, resp.StatusCode, resp.Header.Get("Content-Type"))
		}
	}
	if resp := serve(context.Background(), s, adminMain, "GET", "/admin-assets/configPage.html", "", nil); resp.StatusCode != 404 {
		t.Error("the raw page must not be served as an asset")
	}
}

// Values saved on Silo's form by earlier releases still count until the
// page is first saved.
func TestOldFormValuesReachTheRoutes(t *testing.T) {
	s := newTestServer()
	entries := byCard(t, map[string]any{
		"EnableSettingsSync": true, "TmdbApiKey": "native-key", "SeerrEnabled": true,
		"navbarPosition": "bottom", "use24HourClock": "true", "confirmExit": "",
		"mediaBarItemCount": "10", "browsingBlur": 12.0, "themeMusicVolume": 40.0,
		"mediaSegmentActionIntro": "skip", "mediaSegmentActionOutro": "askToSkip",
		"bottomNavbarTab1": "home", "bottomNavbarTab2": "", "bottomNavbarTab3": "home",
		"seasonalRowShownHolidays": []any{"christmas"},
	})
	s.SetNative(entries)
	_, got := call(t, s, userKids, "GET", "/Moonfin/Settings/Resolved/tv", "", nil)
	want := map[string]any{"navbarPosition": "bottom", "use24HourClock": true, "mediaBarItemCount": 10.0,
		"browsingBlur": "12", "themeMusicVolume": 40.0, "tmdbApiKey": "native-key",
		"mediaSegmentActions": "intro:skip,outro:askToSkip"}
	for k, v := range want {
		if got[k] != v {
			t.Errorf("%s = %#v, want %#v", k, got[k], v)
		}
	}
	if _, set := got["confirmExit"]; set {
		t.Error("Not set must stay unset")
	}
	if tabs, _ := got["bottomNavbarTabs"].([]any); len(tabs) != 1 || tabs[0] != "home" {
		t.Errorf("bottom tabs must skip blanks and repeats: %v", got["bottomNavbarTabs"])
	}
	if hidden, _ := got["seasonalRowHiddenHolidays"].([]any); len(hidden) == 0 || slices.Contains(hidden, any("christmas")) {
		t.Errorf("hidden holidays must be every holiday not ticked: %v", got["seasonalRowHiddenHolidays"])
	}
}

// byCard puts form values on the cards the generated table assigns them to.
func byCard(t *testing.T, values map[string]any) map[string]map[string]any {
	t.Helper()
	out := map[string]map[string]any{}
	for k, v := range values {
		i := slices.IndexFunc(formFields, func(f formField) bool { return f.Field == k })
		if i < 0 {
			t.Fatalf("%s isn't on the form", k)
		}
		if out[formFields[i].Entry] == nil {
			out[formFields[i].Entry] = map[string]any{}
		}
		out[formFields[i].Entry][k] = v
	}
	return out
}

func TestPageSaveKeepsTheOldFormValues(t *testing.T) {
	s := newTestServer()
	s.SetNative(byCard(t, map[string]any{"TmdbApiKey": "form-key", "navbarPosition": "bottom", "use24HourClock": "true"}))
	_, cfg := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	if cfg["TmdbApiKey"] != "form-key" || cfg["DefaultUserSettings"].(map[string]any)["navbarPosition"] != "bottom" {
		t.Fatalf("the page must read the old form's values: %v", cfg)
	}
	delete(cfg["DefaultUserSettings"].(map[string]any), "use24HourClock")
	saveConfig(t, s, cfg)

	_, again := call(t, s, adminMain, "GET", "/Moonfin/Admin/Configuration", "", nil)
	d := again["DefaultUserSettings"].(map[string]any)
	if again["TmdbApiKey"] != "form-key" || d["navbarPosition"] != "bottom" {
		t.Fatalf("a page save must keep what it read: %v", again)
	}
	if _, back := d["use24HourClock"]; back {
		t.Fatal("a value cleared on the page must not come back from the old form")
	}
	if _, shown := again[keyFormMoved]; shown {
		t.Fatal("the marker is internal")
	}
}

func theme(t *testing.T, id, name string) string {
	t.Helper()
	raw, err := os.ReadFile("testdata/theme.json")
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]any
	_ = json.Unmarshal(raw, &m)
	m["id"], m["displayName"] = id, name
	out, _ := json.Marshal(m)
	return string(out)
}

func TestThemes(t *testing.T) {
	// Silo's store, where themes must stay out of the packed buckets.
	s := NewServer("1.2.3", testHost(&store.Packed{KV: store.NewMemKV(), Pack: PerProfileKey}))
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Admin/Themes", theme(t, "dusk", "Dusk"), nil); code != http.StatusForbidden {
		t.Fatalf("only an admin may upload, got %d", code)
	}
	if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Themes", `{"id":"x"}`, nil); code != 400 || len(got["errors"].([]any)) == 0 {
		t.Fatalf("an invalid theme must be refused with its errors: %d %v", code, got)
	}
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/Themes", theme(t, "moonfin", "Mine"), nil); code != 400 {
		t.Fatal("a built-in theme id must be refused")
	}
	big := `{"id":"big","displayName":"` + strings.Repeat("x", maxThemeBytes) + `"}`
	if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Themes", big, nil); code != 400 || !strings.Contains(fmt.Sprint(got["errors"]), "exceeds max size") {
		t.Fatalf("an oversized theme must say it's too big: %d %v", code, got)
	}
	for i, name := range []string{"Eve", "Dawn", "Cobalt", "Birch", "Amber"} {
		if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Themes", theme(t, fmt.Sprintf("t%d", i), name), nil); code != 200 || got["success"] != true {
			t.Fatalf("upload %s = %d %v", name, code, got)
		}
	}
	if code, got := call(t, s, adminMain, "POST", "/Moonfin/Admin/Themes", theme(t, "t5", "Extra"), nil); code != 400 {
		t.Fatalf("a sixth theme must be refused: %d %v", code, got)
	}
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/Themes", theme(t, "t0", "Eve 2"), nil); code != 200 {
		t.Fatal("replacing a theme must work at the limit")
	}

	resp := serve(context.Background(), s, userKids, "GET", "/Moonfin/Themes", "", nil)
	var list []map[string]any
	if err := json.Unmarshal(readAll(t, resp), &list); err != nil || len(list) != 5 || list[0]["displayName"] != "Amber" || list[4]["displayName"] != "Eve 2" {
		t.Fatalf("themes = %d %v", len(list), err)
	}
	if code, got := call(t, s, userKids, "GET", "/Moonfin/Themes/T3", "", nil); code != 200 || got["displayName"] != "Birch" {
		t.Fatalf("one theme = %d %v", code, got)
	}
	_, admin := call(t, s, adminMain, "GET", "/Moonfin/Admin/Themes", "", nil)
	if items := admin["items"].([]any); len(items) != 5 || items[0].(map[string]any)["checksumSha256"] == "" {
		t.Fatalf("admin list = %v", admin)
	}
	raw, _, _ := s.kv.Get(context.Background(), keyThemes)
	if len(raw) > 4<<10 {
		t.Errorf("five themes take %d bytes stored, want them compressed", len(raw))
	}

	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Themes/t3", "", nil); code != 200 {
		t.Fatal("delete failed")
	}
	if code, _ := call(t, s, userKids, "GET", "/Moonfin/Themes/t3", "", nil); code != 404 {
		t.Fatal("a deleted theme must be gone")
	}
	if code, _ := call(t, s, adminMain, "DELETE", "/Moonfin/Admin/Themes/t3", "", nil); code != 404 {
		t.Fatal("deleting a missing theme must answer 404")
	}
}

func TestCollectionOrderIsKeptPerUser(t *testing.T) {
	kv := store.NewMemKV()
	s := NewServer("1", testHost(kv))
	order := func(c caller) []string {
		var ids []string
		if err := json.Unmarshal(readAll(t, serve(context.Background(), s, c, "GET", "/Moonfin/Collections/42/Order", "", nil)), &ids); err != nil {
			t.Fatal(err)
		}
		return ids
	}
	if got := order(userMain); got == nil || len(got) != 0 {
		t.Fatalf("no order yet must be an empty list, got %#v", got)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Collections/42/Order", `[" b ","a","b","",""]`, nil); code != 204 {
		t.Fatalf("save = %d", code)
	}
	if got := order(userMain); !reflect.DeepEqual(got, []string{"b", "a"}) {
		t.Fatalf("order = %v, want blanks and repeats dropped", got)
	}
	if got := order(userKids); len(got) != 0 {
		t.Fatalf("another profile sees %v", got)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Collections/42/Order", `[]`, nil); code != 204 || len(order(userMain)) != 0 {
		t.Fatal("an empty list must clear the order")
	}
	if code, _ := call(t, s, userKids, "POST", "/Moonfin/Collections/7/Order", `[]`, nil); code != 204 {
		t.Fatal("clearing nothing must still succeed")
	}
	if _, found, _ := kv.Get(context.Background(), ordersKey(Identity{UserID: 3, ProfileID: "31"})); found {
		t.Fatal("clearing an order that was never saved must not use a key")
	}
	call(t, s, userMain, "POST", "/Moonfin/Collections/42/Order", `["a","b"]`, nil)
	call(t, s, userMain, "POST", "/Moonfin/Collections/43/Order", `["c"]`, nil)
	if got := order(userMain); !reflect.DeepEqual(got, []string{"a", "b"}) {
		t.Fatalf("a second collection's order changed the first: %v", got)
	}
	long := `["` + strings.Repeat("9", maxOrderItemID+1) + `","d"]`
	call(t, s, userMain, "POST", "/Moonfin/Collections/42/Order", long, nil)
	if got := order(userMain); !reflect.DeepEqual(got, []string{"d"}) {
		t.Fatalf("an overlong item id must be dropped: %v", got)
	}
	ids := make([]string, 1500)
	for i := range ids {
		ids[i] = fmt.Sprintf(`"%019d"`, i)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Collections/44/Order", "["+strings.Join(ids, ",")+"]", nil); code != 204 {
		t.Fatalf("a large order within the cap = %d", code)
	}
	for c, code := 45, 0; code != http.StatusRequestEntityTooLarge; c++ {
		if c > 60 {
			t.Fatal("a profile's orders must have a size cap")
		}
		code, _ = call(t, s, userMain, "POST", fmt.Sprintf("/Moonfin/Collections/%d/Order", c), "["+strings.Join(ids, ",")+"]", nil)
	}
	if got := order(userMain); !reflect.DeepEqual(got, []string{"d"}) {
		t.Fatalf("a refused order must leave the saved ones alone: %v", got)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Collections/日本/Order", `["x"]`, nil); code != 400 {
		t.Fatal("an id with no ASCII letters or digits must be refused")
	}
	if code, _ := call(t, s, anon, "GET", "/Moonfin/Collections/42/Order", "", nil); code != 401 {
		t.Fatal("orders need a signed-in caller")
	}
}

func TestWatchPartyStoreKeepsLiveRooms(t *testing.T) {
	s := newTestServer()
	room := func(i int) string { return fmt.Sprintf("00000000-0000-4000-8000-%012d", i) }
	avatar := `{"url":"/avatars/presets/fox.svg"}`
	for i := 0; i < watchPartyRoomsPerAccount; i++ {
		if code, _ := call(t, s, other, "PUT", "/Moonfin/WatchParty/"+room(i)+"/Avatar", avatar, nil); code != 200 {
			t.Fatalf("room %d refused with %d", i, code)
		}
	}
	if code, _ := call(t, s, other, "PUT", "/Moonfin/WatchParty/"+room(99)+"/Avatar", avatar, nil); code != http.StatusServiceUnavailable {
		t.Fatalf("an account past its share of rooms is refused, got %d", code)
	}
	if code, _ := call(t, s, other, "PUT", "/Moonfin/WatchParty/"+room(0), `{"subtitle":null}`, nil); code != 200 {
		t.Fatalf("its existing rooms still take changes, got %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", "/Moonfin/WatchParty/"+room(0)+"/Avatar", avatar, nil); code != 200 {
		t.Fatalf("other members join its rooms, got %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", "/Moonfin/WatchParty/"+room(99)+"/Avatar", avatar, nil); code != 200 {
		t.Fatalf("another account starts its own room, got %d", code)
	}
	if _, got := call(t, s, other, "GET", "/Moonfin/WatchParty/"+room(0), "", nil); got["avatars"] == nil {
		t.Fatalf("the oldest room is kept: %v", got)
	}
	if code, _ := call(t, s, other, "GET", "/Moonfin/WatchParty/"+strings.Repeat("-", 36), "", nil); code != 400 {
		t.Fatalf("a room id that isn't a UUID is refused, got %d", code)
	}
}

// A host without profiles (Plex) lists just the account, which is the whole
// of its settings.
func TestAdminUsersWithoutProfiles(t *testing.T) {
	s := newTestServer()
	plexUser := caller{user: 9, name: "al"}
	call(t, s, plexUser, "POST", "/Moonfin/Settings/Profile/tv", `{"profile":{"a":1}}`, nil)
	body := readAll(t, serve(context.Background(), s, adminMain, "GET", "/Moonfin/Admin/Users", "", nil))
	if !strings.Contains(string(body), `"Id":"9","Name":"al","Policy":{"IsAdministrator":false},"Profiles":[]`) {
		t.Fatalf("users = %s", body)
	}
}
