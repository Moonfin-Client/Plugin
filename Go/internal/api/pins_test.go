package api

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// Plex callers have an account and no profile.
var (
	plexOwner  = caller{user: 1, admin: true, name: "boss"}
	plexMember = caller{user: 42, name: "kid"}
	plexOther  = caller{user: 77, name: "zed"}
)

func newPinServer() *Server {
	h := testHost(store.NewMemKV())
	h.Name, h.Pins = "Plex", true
	return NewServer("1.2.3", h)
}

func TestPinSetVerifyChangeAndRemove(t *testing.T) {
	s := newPinServer()
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Pin", "", nil); got["enabled"] != false {
		t.Fatalf("status before = %v", got)
	}
	if code, got := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"1234"}`, nil); code != 404 || got["error"] != "No PIN is set" {
		t.Fatalf("verify without a PIN = %d %v", code, got)
	}
	for _, bad := range []string{`{"pin":"12a4"}`, `{"pin":"12345"}`, `{}`, `nope`} {
		if code, got := call(t, s, plexMember, "PUT", "/Moonfin/Pin", bad, nil); code != 400 || got["error"] != "A PIN is exactly four digits" {
			t.Fatalf("set %s = %d %v", bad, code, got)
		}
	}
	if code, got := call(t, s, plexMember, "PUT", "/Moonfin/Pin", `{"pin":"1234"}`, nil); code != 200 || got["enabled"] != true {
		t.Fatalf("set = %d %v", code, got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Pin", "", nil); got["enabled"] != true {
		t.Fatalf("status after = %v", got)
	}
	if _, got := call(t, s, plexOther, "GET", "/Moonfin/Pin", "", nil); got["enabled"] != false {
		t.Fatalf("another account must have no PIN: %v", got)
	}
	if code, got := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"1234"}`, nil); code != 200 || got["valid"] != true {
		t.Fatalf("right PIN = %d %v", code, got)
	}
	if code, got := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"0000"}`, nil); code != 200 || got["valid"] != false {
		t.Fatalf("wrong PIN = %d %v", code, got)
	}

	if code, got := call(t, s, plexMember, "PUT", "/Moonfin/Pin", `{"pin":"5678"}`, nil); code != 403 || got["error"] != "The current PIN is wrong" {
		t.Fatalf("change without the current PIN = %d %v", code, got)
	}
	if code, _ := call(t, s, plexMember, "PUT", "/Moonfin/Pin", `{"pin":"5678","currentPin":"1234"}`, nil); code != 200 {
		t.Fatalf("change = %d", code)
	}
	if _, got := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"5678"}`, nil); got["valid"] != true {
		t.Fatalf("the new PIN must verify: %v", got)
	}

	if code, got := call(t, s, plexMember, "DELETE", "/Moonfin/Pin", "", nil); code != 403 || got["error"] != "The current PIN is wrong" {
		t.Fatalf("remove without the current PIN = %d %v", code, got)
	}
	if code, got := call(t, s, plexMember, "DELETE", "/Moonfin/Pin", `{"currentPin":"5678"}`, nil); code != 200 || got["enabled"] != false {
		t.Fatalf("remove = %d %v", code, got)
	}
	if _, got := call(t, s, plexMember, "GET", "/Moonfin/Pin", "", nil); got["enabled"] != false {
		t.Fatalf("status after remove = %v", got)
	}
}

func TestPinLocksOutAfterFreeAttempts(t *testing.T) {
	s := newPinServer()
	call(t, s, plexMember, "PUT", "/Moonfin/Pin", `{"pin":"1234"}`, nil)
	for i := range pinFreeAttempts {
		if code, got := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"0000"}`, nil); code != 200 || got["valid"] != false {
			t.Fatalf("wrong guess %d = %d %v", i+1, code, got)
		}
	}
	resp := serve(context.Background(), s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"0000"}`, nil)
	var got map[string]any
	_ = json.Unmarshal(readAll(t, resp), &got)
	if resp.StatusCode != 429 || got["error"] != "Too many attempts" || got["retryAfterSeconds"] != 30.0 || resp.Header.Get("Retry-After") != "30" {
		t.Fatalf("guess past the free ones = %d %v %q", resp.StatusCode, got, resp.Header.Get("Retry-After"))
	}
	if code, _ := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"1234"}`, nil); code != 429 {
		t.Fatalf("a locked account must wait even with the right PIN: %d", code)
	}
	if code, _ := call(t, s, plexMember, "PUT", "/Moonfin/Pin", `{"pin":"5678","currentPin":"1234"}`, nil); code != 429 {
		t.Fatalf("changing while locked = %d", code)
	}
	if _, got := call(t, s, plexOther, "GET", "/Moonfin/Pin", "", nil); got["enabled"] != false {
		t.Fatal("one account's lockout must not touch another's")
	}

	pins, err := s.loadPins(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	later := time.Now().Add(31 * time.Second)
	if check, _, changed := verifyPin(pins, "42", "1234", later); check != pinValid || !changed || pins["42"].Failed != 0 || pins["42"].LockedUntilUtc != nil {
		t.Fatalf("once the wait is over the right PIN must pass and reset the count: %v %+v", check, pins["42"])
	}
	for failed, want := range map[int]int{5: 0, 6: 30, 7: 60, 35: 900, 100: 900} {
		if got := lockoutSeconds(failed); got != want {
			t.Errorf("lockoutSeconds(%d) = %d, want %d", failed, got, want)
		}
	}
}

func TestPinRecordKeepsNoPin(t *testing.T) {
	s := newPinServer()
	call(t, s, plexMember, "PUT", "/Moonfin/Pin", `{"pin":"1234"}`, nil)
	raw, _, _ := s.kv.Get(context.Background(), keyPins)
	var pins map[string]pinRecord
	if err := json.Unmarshal(raw, &pins); err != nil {
		t.Fatal(err)
	}
	p := pins["42"]
	if p.Iterations != pinHashIterations || p.Hash == "" || p.Salt == "" || p.UserName != "kid" || strings.Contains(string(raw), "1234") {
		t.Fatalf("record = %s", raw)
	}
	p.Iterations = 5
	if p.matches("1234") {
		t.Fatal("a damaged iteration count must never match")
	}
}

func adminPins(t *testing.T, s *Server, c caller) (int, []map[string]any) {
	t.Helper()
	resp := serve(context.Background(), s, c, "GET", "/Moonfin/Admin/Pins", "", nil)
	var users []map[string]any
	_ = json.Unmarshal(readAll(t, resp), &users)
	return resp.StatusCode, users
}

func TestAdminListsSetsAndClearsPins(t *testing.T) {
	s := newPinServer()
	ctx := context.Background()
	if err := s.saveJSON(ctx, keyIndex, []indexEntry{
		{UserID: 42, UserName: "kid"}, {UserID: 1, UserName: "boss", Admin: true}, {UserID: 9, UserName: "Abe"},
	}); err != nil {
		t.Fatal(err)
	}
	// zed never synced settings, so only the PIN puts the account on the list.
	call(t, s, plexOther, "PUT", "/Moonfin/Pin", `{"pin":"1111"}`, nil)

	if code, _ := adminPins(t, s, plexMember); code != 403 {
		t.Fatalf("a member listing PINs = %d", code)
	}
	_, users := adminPins(t, s, plexOwner)
	var names []string
	for _, u := range users {
		names = append(names, u["name"].(string))
	}
	if len(users) != 4 || names[0] != "boss" || names[1] != "Abe" || names[2] != "kid" || names[3] != "zed" ||
		users[0]["isAdministrator"] != true || users[0]["id"] != "1" || users[3]["hasPin"] != true || users[2]["hasPin"] != false {
		t.Fatalf("users = %v", users)
	}

	if code, got := call(t, s, plexMember, "PUT", "/Moonfin/Users/42/Pin", `{"pin":"4321"}`, nil); code != 403 {
		t.Fatalf("a member setting a PIN for someone = %d %v", code, got)
	}
	if code, got := call(t, s, plexOwner, "PUT", "/Moonfin/Users/500/Pin", `{"pin":"4321"}`, nil); code != 404 || got["error"] != "That user does not exist" {
		t.Fatalf("unknown user = %d %v", code, got)
	}
	if code, _ := call(t, s, plexOwner, "PUT", "/Moonfin/Users/42/Pin", `{"pin":"43"}`, nil); code != 400 {
		t.Fatalf("short PIN = %d", code)
	}
	if code, _ := call(t, s, plexOwner, "PUT", "/Moonfin/Users/abc/Pin", `{"pin":"4321"}`, nil); code != 400 {
		t.Fatalf("malformed userId = %d", code)
	}
	if code, got := call(t, s, plexOwner, "PUT", "/Moonfin/Users/42/Pin", `{"pin":"4321"}`, nil); code != 200 || got["enabled"] != true {
		t.Fatalf("admin set = %d %v", code, got)
	}
	if _, got := call(t, s, plexMember, "POST", "/Moonfin/Pin/Verify", `{"pin":"4321"}`, nil); got["valid"] != true {
		t.Fatalf("the admin's PIN must verify: %v", got)
	}

	if code, got := call(t, s, plexOwner, "DELETE", "/Moonfin/Users/77/Pin", "", nil); code != 200 || got["enabled"] != false {
		t.Fatalf("admin clear = %d %v", code, got)
	}
	if _, got := call(t, s, plexOther, "GET", "/Moonfin/Pin", "", nil); got["enabled"] != false {
		t.Fatalf("a cleared PIN must be gone: %v", got)
	}
	if _, users := adminPins(t, s, plexOwner); len(users) != 3 {
		t.Fatalf("an account known only by its PIN leaves the list with it: %v", users)
	}
	if code, _ := call(t, s, plexOwner, "DELETE", "/Moonfin/Users/77/Pin", "", nil); code != 404 {
		t.Fatalf("clearing an unknown user = %d", code)
	}
}

// Silo keeps PINs on its profiles, and the apps fall back to those only when
// Moonbase answers 404.
func TestPinRoutesAreOffOnSilo(t *testing.T) {
	s := newTestServer()
	for _, tc := range []struct {
		c            caller
		method, path string
	}{
		{userMain, "GET", "/Moonfin/Pin"},
		{userMain, "PUT", "/Moonfin/Pin"},
		{userMain, "DELETE", "/Moonfin/Pin"},
		{userMain, "POST", "/Moonfin/Pin/Verify"},
		{adminMain, "GET", "/Moonfin/Admin/Pins"},
		{adminMain, "PUT", "/Moonfin/Users/3/Pin"},
		{adminMain, "DELETE", "/Moonfin/Users/3/Pin"},
	} {
		if code, _ := call(t, s, tc.c, tc.method, tc.path, `{"pin":"1234"}`, nil); code != 404 {
			t.Errorf("%s %s on Silo = %d, want 404", tc.method, tc.path, code)
		}
	}
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["pinSupported"] != false {
		t.Fatalf("Silo ping = %v", ping)
	}
	if _, ping := call(t, newPinServer(), plexMember, "GET", "/Moonfin/Ping", "", nil); ping["pinSupported"] != true {
		t.Fatalf("Plex ping = %v", ping)
	}
}
