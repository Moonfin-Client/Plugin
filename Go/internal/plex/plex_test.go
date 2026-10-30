package plex

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// fakePlex stands in for a Plex server and plex.tv. "owner" owns the server,
// "member" and "managed" are Home members whose server tokens plex.tv doesn't
// know, and anything else is refused.
type fakePlex struct {
	pms, tv  *httptest.Server
	approved string // the token plex.tv hands back for the sign-in PIN
}

func newFakePlex(t *testing.T) *fakePlex {
	f := &fakePlex{}
	f.pms = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := r.Header.Get("X-Plex-Token")
		switch r.URL.Path {
		case "/identity":
			io.WriteString(w, `{"MediaContainer":{"machineIdentifier":"m1"}}`)
		case "/myplex/account":
			switch token {
			case "owner":
				io.WriteString(w, `{"MyPlex":{"username":"boss"}}`)
			case "member", "managed":
				w.WriteHeader(http.StatusForbidden)
			default:
				w.WriteHeader(http.StatusUnauthorized)
			}
		case "/library/sections":
			if token != "owner" && token != "member" && token != "managed" {
				w.WriteHeader(http.StatusUnauthorized)
				return
			}
			io.WriteString(w, `{"MediaContainer":{"Directory":[{"key":"1","title":"Movies","type":"movie"},{"key":"2","title":"TV","type":"show"}]}}`)
		default:
			http.NotFound(w, r)
		}
	}))
	f.tv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		token := r.Header.Get("X-Plex-Token")
		switch {
		case r.URL.Path == "/api/v2/user" && token == "owner":
			io.WriteString(w, `{"id":1,"username":"boss","title":"Boss"}`)
		case r.URL.Path == "/api/v2/user":
			w.WriteHeader(http.StatusUnauthorized)
		case r.URL.Path == "/api/servers/m1/shared_servers" && token == "owner":
			io.WriteString(w, `<MediaContainer><SharedServer userID="7" username="" email="x@example.com" accessToken="other"/><SharedServer userID="42" username="kid" accessToken="member"/><SharedServer userID="43" username="" email="" accessToken="managed"/></MediaContainer>`)
		case r.URL.Path == "/api/v2/home/users" && token == "owner":
			io.WriteString(w, `{"users":[{"id":1,"title":"Boss"},{"id":43,"uuid":"abc","title":"Little One","restricted":true}]}`)
		case r.URL.Path == "/api/v2/pins" && r.Method == http.MethodPost:
			if r.Header.Get("X-Plex-Client-Identifier") == "" {
				w.WriteHeader(http.StatusBadRequest)
				return
			}
			w.WriteHeader(http.StatusCreated)
			io.WriteString(w, `{"id":99,"code":"ABCD"}`)
		case r.URL.Path == "/api/v2/pins/99":
			json.NewEncoder(w).Encode(map[string]any{"authToken": nullable(f.approved)})
		default:
			w.WriteHeader(http.StatusUnauthorized)
		}
	}))
	t.Cleanup(func() { f.pms.Close(); f.tv.Close() })
	return f
}

func nullable(s string) any {
	if s == "" {
		return nil
	}
	return s
}

func newCompanion(t *testing.T, f *fakePlex, dir string) *Companion {
	t.Helper()
	c, err := New(Config{PMS: f.pms.URL, PlexTV: f.tv.URL, DataDir: dir, Version: "1.0.0"})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func send(c *Companion, r *http.Request) (*httptest.ResponseRecorder, map[string]any) {
	w := httptest.NewRecorder()
	c.ServeHTTP(w, r)
	var body map[string]any
	_ = json.Unmarshal(w.Body.Bytes(), &body)
	return w, body
}

func withToken(method, path, token string, body string) *http.Request {
	r := httptest.NewRequest(method, path, strings.NewReader(body))
	if token != "" {
		r.Header.Set("X-Plex-Token", token)
	}
	return r
}

func TestOwnerIsAdminAndSeesLibraries(t *testing.T) {
	c := newCompanion(t, newFakePlex(t), t.TempDir())
	w, body := send(c, withToken("GET", "/Moonfin/Ping", "owner", ""))
	if w.Code != 200 || body["serverName"] != "Plex" || body["installed"] != true {
		t.Fatalf("ping = %d %v", w.Code, body)
	}
	_, who := send(c, withToken("GET", "/Moonfin/Whoami", "owner", ""))
	libs, _ := who["libraries"].([]any)
	if who["userId"] != 1.0 || who["admin"] != true || who["userName"] != "boss" || len(libs) != 2 {
		t.Fatalf("whoami = %v", who)
	}
	if lib := libs[1].(map[string]any); lib["mediaType"] != "tv" {
		t.Fatalf("a Plex show library must read as tv: %v", lib)
	}
	if strings.Contains(w.Body.String(), "owner") {
		t.Fatal("a reply must never carry the token")
	}
}

func TestMemberIsNamedFromTheOwnersShareList(t *testing.T) {
	c := newCompanion(t, newFakePlex(t), t.TempDir())
	if w, _ := send(c, withToken("GET", "/Moonfin/Ping", "member", "")); w.Code != 401 {
		t.Fatalf("before the owner signs in, a member can't be named: %d", w.Code)
	}
	if err := c.saveOwnerToken("owner"); err != nil {
		t.Fatal(err)
	}
	_, who := send(c, withToken("GET", "/Moonfin/Whoami", "member", ""))
	if who["userId"] != 42.0 || who["admin"] != false || who["userName"] != "kid" || who["libraries"] == nil {
		t.Fatalf("whoami = %v", who)
	}
	if w, _ := send(c, withToken("GET", "/Moonfin/Admin/Configuration", "member", "")); w.Code != 403 {
		t.Fatalf("a member must not reach admin routes: %d", w.Code)
	}
	// A managed Home member has no username or email, so the name comes from
	// the owner's Home list.
	if _, who := send(c, withToken("GET", "/Moonfin/Whoami", "managed", "")); who["userId"] != 43.0 || who["userName"] != "Little One" {
		t.Fatalf("managed member = %v", who)
	}
}

func TestTokenPlacesAndRefusals(t *testing.T) {
	c := newCompanion(t, newFakePlex(t), t.TempDir())
	if w, _ := send(c, withToken("GET", "/Moonfin/Ping", "", "")); w.Code != 401 {
		t.Fatalf("anonymous = %d", w.Code)
	}
	if w, _ := send(c, withToken("GET", "/Moonfin/Ping", "bogus", "")); w.Code != 401 {
		t.Fatalf("bogus = %d", w.Code)
	}
	r := withToken("GET", "/Moonfin/Ping", "", "")
	r.Header.Set("Authorization", `MediaBrowser Client="Moonfin", Device="Linux", DeviceId="d", Version="1", Token="owner"`)
	if w, _ := send(c, r); w.Code != 200 {
		t.Fatalf("MediaBrowser header = %d", w.Code)
	}
	if w, _ := send(c, withToken("GET", "/Moonfin/Ping?X-Plex-Token=owner", "", "")); w.Code != 200 {
		t.Fatalf("query token = %d", w.Code)
	}
}

func TestPlexDownIsBadGateway(t *testing.T) {
	f := newFakePlex(t)
	c := newCompanion(t, f, t.TempDir())
	f.pms.Close()
	if w, _ := send(c, withToken("GET", "/Moonfin/Ping", "owner", "")); w.Code != 502 {
		t.Fatalf("Plex down = %d", w.Code)
	}
}

func TestCookieWritesOnlyFromTheCompanionsOwnPages(t *testing.T) {
	c := newCompanion(t, newFakePlex(t), t.TempDir())
	post := func(origin string) int {
		r := httptest.NewRequest("POST", "http://companion:32500/Moonfin/Admin/Configuration", strings.NewReader(`{"TmdbApiKey":"k"}`))
		r.AddCookie(&http.Cookie{Name: cookieToken, Value: "owner"})
		if origin != "" {
			r.Header.Set("Origin", origin)
		}
		w, _ := send(c, r)
		return w.Code
	}
	if code := post("http://companion:8080"); code != 401 {
		t.Fatalf("another origin on the same host = %d", code)
	}
	if code := post("http://companion:32500"); code != 204 {
		t.Fatalf("own page = %d", code)
	}

	// Behind a proxy that rewrites Host, the browser's host arrives as
	// X-Forwarded-Host.
	r := httptest.NewRequest("POST", "http://moonbase:32500/Moonfin/Admin/Configuration", strings.NewReader(`{}`))
	r.AddCookie(&http.Cookie{Name: cookieToken, Value: "owner"})
	r.Header.Set("Origin", "https://moonbase.example.com")
	r.Header.Set("X-Forwarded-Host", "moonbase.example.com")
	if w, _ := send(c, r); w.Code != 204 {
		t.Fatalf("behind a proxy = %d", w.Code)
	}
}

func TestSettingsSurviveARestart(t *testing.T) {
	f, dir := newFakePlex(t), t.TempDir()
	c := newCompanion(t, f, dir)
	if w, _ := send(c, withToken("POST", "/Moonfin/Settings/Profile/tv", "owner", `{"profile":{"navbarPosition":"bottom"}}`)); w.Code/100 != 2 {
		t.Fatalf("save = %d", w.Code)
	}
	again := newCompanion(t, f, dir)
	_, got := send(again, withToken("GET", "/Moonfin/Settings/Resolved/tv", "owner", ""))
	if got["navbarPosition"] != "bottom" {
		t.Fatalf("after restart = %v", got)
	}
	if c.clientID != again.clientID {
		t.Fatal("the client id must stay the same across restarts")
	}
}

func TestAdminSignIn(t *testing.T) {
	f, dir := newFakePlex(t), t.TempDir()
	c := newCompanion(t, f, dir)

	w, _ := send(c, httptest.NewRequest("GET", "http://companion:32500/admin", nil))
	if w.Code != 302 || w.Header().Get("Location") != "/admin/signin" {
		t.Fatalf("signed-out admin page = %d %s", w.Code, w.Header().Get("Location"))
	}

	w, _ = send(c, httptest.NewRequest("GET", "http://companion:32500/admin/signin", nil))
	loc := w.Header().Get("Location")
	if w.Code != 302 || !strings.HasPrefix(loc, plexAuthPage) || !strings.Contains(loc, "code=ABCD") ||
		!strings.Contains(loc, "forwardUrl=http%3A%2F%2Fcompanion%3A32500%2Fadmin%2Fcallback") {
		t.Fatalf("sign-in = %d %s", w.Code, loc)
	}
	pin := w.Result().Cookies()[0]

	callback := func() *httptest.ResponseRecorder {
		r := httptest.NewRequest("GET", "http://companion:32500/admin/callback", nil)
		r.AddCookie(pin)
		w, _ := send(c, r)
		return w
	}
	if w := callback(); w.Code != 200 || !strings.Contains(w.Body.String(), "hasn") {
		t.Fatalf("before approval = %d %s", w.Code, w.Body.String())
	}
	f.approved = "member"
	if w := callback(); w.Code != 403 {
		t.Fatalf("a member's sign-in = %d", w.Code)
	}
	f.approved = "owner"
	w = callback()
	if w.Code != 302 || w.Header().Get("Location") != "/admin" {
		t.Fatalf("owner's sign-in = %d", w.Code)
	}
	var token *http.Cookie
	for _, ck := range w.Result().Cookies() {
		if ck.Name == cookieToken {
			token = ck
		}
	}
	if token == nil || token.Value != "owner" || !token.HttpOnly {
		t.Fatalf("token cookie = %+v", token)
	}
	if b, err := os.ReadFile(filepath.Join(dir, "owner-token")); err != nil || string(b) != "owner" {
		t.Fatalf("owner token file = %q %v", b, err)
	}
	if info, _ := os.Stat(filepath.Join(dir, "owner-token")); info.Mode().Perm() != 0o600 {
		t.Fatalf("owner token file mode = %v", info.Mode())
	}

	r := httptest.NewRequest("GET", "http://companion:32500/admin", nil)
	r.AddCookie(token)
	if w, _ := send(c, r); w.Code != 200 || !strings.Contains(w.Body.String(), `window.moonbaseHost = {"name":"Plex"};`) ||
		strings.Contains(w.Body.String(), `<div class="moonbaseHostBar"><a `) {
		t.Fatalf("admin page = %d", w.Code)
	}
}

func TestPreflightAllowsTheTokenHeader(t *testing.T) {
	c := newCompanion(t, newFakePlex(t), t.TempDir())
	r := httptest.NewRequest("OPTIONS", "/Moonfin/Settings", nil)
	r.Header.Set("Origin", "https://app.example")
	w, _ := send(c, r)
	if w.Code != 204 || !strings.Contains(w.Header().Get("Access-Control-Allow-Headers"), "X-Plex-Token") ||
		w.Header().Get("Access-Control-Allow-Origin") != "*" {
		t.Fatalf("preflight = %d %v", w.Code, w.Header())
	}
}
