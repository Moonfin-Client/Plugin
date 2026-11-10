package api

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// fakeSeerr answers the calls Moonbase makes, with Seerr's CSRF protection on.
type fakeSeerr struct {
	*httptest.Server
	mu        sync.Mutex
	sessions  map[string]seerrUser
	rotated   bool
	logouts   int
	lastBody  string
	lastQuery string
}

var fakeSeerrUsers = map[string]seerrUser{
	"tok-owner":  {ID: 1, DisplayName: "boss", Permissions: 2, PlexID: 1},
	"tok-member": {ID: 7, DisplayName: "kid", Permissions: 32, PlexID: 42},
}

func newFakeSeerr(t *testing.T) *fakeSeerr {
	f := &fakeSeerr{sessions: map[string]seerrUser{}}
	f.Server = httptest.NewServer(http.HandlerFunc(f.serve))
	t.Cleanup(f.Close)
	return f
}

func (f *fakeSeerr) user(r *http.Request) (seerrUser, bool) {
	c, err := r.Cookie("connect.sid")
	if err != nil {
		return seerrUser{}, false
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	u, ok := f.sessions[c.Value]
	return u, ok
}

func (f *fakeSeerr) csrfOK(r *http.Request) bool {
	secret, err := r.Cookie("_csrf")
	return err == nil && secret.Value == "secret" && r.Header.Get("X-XSRF-TOKEN") == "tok"
}

func (f *fakeSeerr) open(w http.ResponseWriter, u seerrUser, id string) {
	f.mu.Lock()
	f.sessions[id] = u
	f.mu.Unlock()
	http.SetCookie(w, &http.Cookie{Name: "connect.sid", Value: id, HttpOnly: true})
}

func (f *fakeSeerr) serve(w http.ResponseWriter, r *http.Request) {
	http.SetCookie(w, &http.Cookie{Name: "_csrf", Value: "secret", Secure: true, SameSite: http.SameSiteStrictMode})
	http.SetCookie(w, &http.Cookie{Name: "XSRF-TOKEN", Value: "tok", Secure: true})
	body, _ := io.ReadAll(r.Body)
	f.mu.Lock()
	f.lastBody, f.lastQuery = string(body), r.URL.RawQuery
	f.mu.Unlock()

	switch r.Method + " " + r.URL.Path {
	case "GET /api/v1/auth/me":
		if u, ok := f.user(r); ok {
			writeJSON(w, http.StatusOK, u)
			return
		}
		writeJSON(w, http.StatusForbidden, M{"message": "You do not have permission to access this endpoint."})
	case "POST /api/v1/auth/plex":
		if !f.csrfOK(r) {
			writeJSON(w, http.StatusForbidden, M{"message": "invalid csrf token"})
			return
		}
		var in struct{ AuthToken string }
		_ = json.Unmarshal(body, &in)
		u, ok := fakeSeerrUsers[in.AuthToken]
		if !ok {
			writeJSON(w, http.StatusInternalServerError, M{"message": "Something went wrong."})
			return
		}
		f.open(w, u, "s%3Asess-"+u.DisplayName)
		writeJSON(w, http.StatusOK, u)
	case "POST /api/v1/auth/local":
		if !f.csrfOK(r) || !strings.Contains(string(body), `"password":"pw"`) {
			writeJSON(w, http.StatusForbidden, M{"message": "Access denied."})
			return
		}
		u := seerrUser{ID: 9, DisplayName: "local"}
		f.open(w, u, "sess-local")
		writeJSON(w, http.StatusOK, u)
	case "POST /api/v1/auth/logout":
		if c, err := r.Cookie("connect.sid"); err == nil && f.csrfOK(r) {
			f.mu.Lock()
			delete(f.sessions, c.Value)
			f.logouts++
			f.mu.Unlock()
		}
		writeJSON(w, http.StatusOK, M{"status": "ok"})
	case "GET /api/v1/request":
		u, ok := f.user(r)
		if !ok {
			writeJSON(w, http.StatusForbidden, M{"message": "no session"})
			return
		}
		// The first list call reissues the cookie and retires the old one, like a rolling session.
		f.mu.Lock()
		rotate := !f.rotated
		f.rotated = true
		f.mu.Unlock()
		if rotate {
			old, _ := r.Cookie("connect.sid")
			f.mu.Lock()
			delete(f.sessions, old.Value)
			f.mu.Unlock()
			f.open(w, u, "rotated-"+u.DisplayName)
		}
		writeJSON(w, http.StatusOK, M{"results": []any{}, "user": u.ID})
	case "POST /api/v1/request":
		if _, ok := f.user(r); !ok || !f.csrfOK(r) {
			writeJSON(w, http.StatusForbidden, M{"message": "refused"})
			return
		}
		writeJSON(w, http.StatusCreated, M{"id": 99})
	case "GET /api/v1/settings/main":
		writeJSON(w, http.StatusForbidden, M{"message": "You do not have permission to access this endpoint."})
	case "GET /api/v1/redirect":
		http.Redirect(w, r, "/login", http.StatusFound)
	case "GET /api/v1/html":
		w.Header().Set("Content-Type", "text/html")
		_, _ = io.WriteString(w, "<!DOCTYPE html><html>sign in</html>")
	default:
		http.NotFound(w, r)
	}
}

func (f *fakeSeerr) kill(name string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for id, u := range f.sessions {
		if u.DisplayName == name {
			delete(f.sessions, id)
		}
	}
}

func newSeerrServer(t *testing.T, seerr *fakeSeerr, plex bool) *Server {
	h := testHost(store.NewMemKV())
	if plex {
		h.Name, h.SeerrPlexLogin = "Plex", true
	}
	s := NewServer("1.2.3", h)
	if seerr != nil {
		if err := s.saveJSON(context.Background(), keyConfig, M{"SeerrEnabled": true, "SeerrUrl": " " + seerr.URL + "/ "}); err != nil {
			t.Fatal(err)
		}
	}
	return s
}

func plexSignIn(t *testing.T, s *Server, c caller, token string) (int, map[string]any) {
	t.Helper()
	return call(t, s, c, "POST", "/Moonfin/Seerr/Login", `{"authType":"plex","authToken":"`+token+`"}`, nil)
}

func TestSeerrIsOffUntilTheAdminSetsItUp(t *testing.T) {
	s := newSeerrServer(t, nil, true)
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Ping", "", nil); got["seerrEnabled"] != false || got["seerrUrl"] != nil {
		t.Fatalf("ping = %v", got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Status", "", nil); got["enabled"] != false {
		t.Fatalf("status = %v", got)
	}
	if code, _ := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Api/request", "", nil); code != 503 {
		t.Fatalf("proxy while off = %d", code)
	}
	if code, _ := plexSignIn(t, s, plexMember, "tok-member"); code != 503 {
		t.Fatalf("sign-in while off = %d", code)
	}
}

func TestSeerrPlexSignInProxiesAsThatAccount(t *testing.T) {
	seerr := newFakeSeerr(t)
	s := newSeerrServer(t, seerr, true)

	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Ping", "", nil); got["seerrEnabled"] != true || got["seerrUrl"] != seerr.URL {
		t.Fatalf("ping = %v", got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Config", "", nil); got["Enabled"] != true || got["Url"] != seerr.URL {
		t.Fatalf("config = %v", got)
	}
	if code, got := plexSignIn(t, s, plexMember, "tok-member"); code != 200 || got["success"] != true || got["seerrUserId"] != float64(7) {
		t.Fatalf("sign-in = %d %v", code, got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Status", "", nil); got["authenticated"] != true || got["displayName"] != "kid" {
		t.Fatalf("status = %v", got)
	}

	if code, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Api/request", "", map[string]any{"take": "5"}); code != 200 || got["user"] != float64(7) {
		t.Fatalf("list = %d %v", code, got)
	}
	if seerr.lastQuery != "take=5" {
		t.Fatalf("query reached Seerr as %q", seerr.lastQuery)
	}
	// The list call rotated the cookie, so this only works if the new one was kept.
	if code, got := call(t, s, plexMember, "POST", "/Moonfin/Seerr/Api/request", `{"mediaType":"movie","mediaId":603}`, nil); code != 201 || got["id"] != float64(99) {
		t.Fatalf("request = %d %v", code, got)
	}
	if seerr.lastBody != `{"mediaType":"movie","mediaId":603}` {
		t.Fatalf("body reached Seerr as %q", seerr.lastBody)
	}

	if _, got := call(t, s, plexOther, "GET", "/Moonfin/Seerr/Status", "", nil); got["authenticated"] != false {
		t.Fatalf("another account's status = %v", got)
	}
	if code, got := call(t, s, plexOther, "GET", "/Moonfin/Seerr/Api/request", "", nil); code != 401 || got["code"] != "NO_SESSION" {
		t.Fatalf("another account's proxy = %d %v", code, got)
	}
}

func TestSeerrPlexTokenHasToBeTheCallersOwn(t *testing.T) {
	seerr := newFakeSeerr(t)
	s := newSeerrServer(t, seerr, true)

	if code, got := plexSignIn(t, s, plexOther, "tok-member"); code != 403 || got["success"] != false {
		t.Fatalf("someone else's token = %d %v", code, got)
	}
	if _, got := call(t, s, plexOther, "GET", "/Moonfin/Seerr/Status", "", nil); got["authenticated"] != false {
		t.Fatalf("status after a refused sign-in = %v", got)
	}
	if seerr.logouts != 1 {
		t.Fatalf("the session Seerr opened has to be closed, logouts = %d", seerr.logouts)
	}
	if code, got := plexSignIn(t, s, plexMember, "unknown"); code != 401 || got["error"] != "Something went wrong." {
		t.Fatalf("a token Seerr rejects = %d %v", code, got)
	}
	if code, _ := call(t, s, plexMember, "POST", "/Moonfin/Seerr/Login", `{"authType":"plex"}`, nil); code != 400 {
		t.Fatalf("no token = %d", code)
	}
	if code, got := call(t, s, plexMember, "POST", "/Moonfin/Seerr/Login", `{"username":"kid","password":"x"}`, nil); code != 400 || got["errorCode"] != "unsupported_auth_type" {
		t.Fatalf("Jellyfin-style sign-in = %d %v", code, got)
	}
}

func TestSeerrRefusalOnALiveSessionKeepsItButADeadOneSignsOut(t *testing.T) {
	seerr := newFakeSeerr(t)
	s := newSeerrServer(t, seerr, true)
	plexSignIn(t, s, plexMember, "tok-member")

	if code, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Api/settings/main", "", nil); code != 403 || got["message"] == nil {
		t.Fatalf("a permission refusal has to pass through: %d %v", code, got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Validate", "", nil); got["valid"] != true {
		t.Fatalf("a permission refusal signed the user out: %v", got)
	}

	seerr.kill("kid")
	if code, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Api/settings/main", "", nil); code != 401 || got["code"] != "SESSION_EXPIRED" {
		t.Fatalf("a dead session = %d %v", code, got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Status", "", nil); got["authenticated"] != false {
		t.Fatalf("status after expiry = %v", got)
	}
}

func TestSeerrProxyReportsWhatsInFrontOfSeerr(t *testing.T) {
	seerr := newFakeSeerr(t)
	s := newSeerrServer(t, seerr, true)
	plexSignIn(t, s, plexMember, "tok-member")

	if code, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Api/redirect", "", nil); code != 502 || got["code"] != "UPSTREAM_REDIRECT" {
		t.Fatalf("redirect = %d %v", code, got)
	}
	if code, got := call(t, s, plexMember, "GET", "/Moonfin/Seerr/Api/html", "", nil); code != 502 || got["code"] != "UPSTREAM_HTML" {
		t.Fatalf("html = %d %v", code, got)
	}
}

func TestSeerrPathsStayInsideTheAPI(t *testing.T) {
	for path, want := range map[string]bool{
		"request": true, "movie/603": true, "search": true,
		"": false, "..": false, "request/../settings": false, "./auth": false, `a\b`: false,
		"request/%2e%2e/settings": false, "%2E%2E": false, "a%5cb": false, "bad%zz": false,
	} {
		if got := validSeerrPath(path); got != want {
			t.Errorf("validSeerrPath(%q) = %v", path, got)
		}
	}
	seerr := newFakeSeerr(t)
	s := newSeerrServer(t, seerr, true)
	plexSignIn(t, s, plexMember, "tok-member")
	resp := serve(context.Background(), s, plexMember, "GET", "/Moonfin/Seerr/Api/request/%2e%2e/settings/main", "", nil)
	if resp.StatusCode != 400 {
		t.Fatalf("an encoded .. reached Seerr: %d", resp.StatusCode)
	}
}

func TestSeerrLocalSignInWorksOnAnyHostAndPlexOnlyOnPlex(t *testing.T) {
	seerr := newFakeSeerr(t)
	s := newSeerrServer(t, seerr, false)

	if code, got := plexSignIn(t, s, userMain, "tok-member"); code != 400 || got["errorCode"] != "plex_unsupported" {
		t.Fatalf("Plex sign-in on Silo = %d %v", code, got)
	}
	if code, got := call(t, s, userMain, "POST", "/Moonfin/Seerr/Login", `{"authType":"local","username":"a@b.c","password":"nope"}`, nil); code != 401 || got["success"] != false {
		t.Fatalf("wrong password = %d %v", code, got)
	}
	if code, got := call(t, s, userMain, "POST", "/Moonfin/Seerr/Login", `{"authType":"local","username":"a@b.c","password":"pw"}`, nil); code != 200 || got["seerrUserId"] != float64(9) {
		t.Fatalf("local sign-in = %d %v", code, got)
	}
	if code, _ := call(t, s, userMain, "DELETE", "/Moonfin/Seerr/Logout", "", nil); code != 200 {
		t.Fatalf("logout = %d", code)
	}
	if seerr.logouts != 1 {
		t.Fatalf("logout has to end the Seerr session too, logouts = %d", seerr.logouts)
	}
	if _, got := call(t, s, userMain, "GET", "/Moonfin/Seerr/Status", "", nil); got["authenticated"] != false {
		t.Fatalf("status after logout = %v", got)
	}
}

func TestNormalizeSeerrURL(t *testing.T) {
	for in, want := range map[string]string{
		"seerr:5055":                   "http://seerr:5055",
		` "https://r.example/seerr/" `: "https://r.example/seerr",
		"ftp://seerr":                  "",
		"":                             "",
		"http://":                      "",
	} {
		if got := normalizeSeerrURL(in); got != want {
			t.Errorf("normalizeSeerrURL(%q) = %q, want %q", in, got, want)
		}
	}
}
