package api

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"testing"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/smartsearch"
	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// When this is set the test binary plays the smart search helper, answering
// "ready" or not to status as its value says.
const fakeHelperEnv = "MOONBASE_FAKE_SMARTSEARCH"

func TestMain(m *testing.M) {
	if mode := os.Getenv(fakeHelperEnv); mode != "" {
		fakeHelper(mode)
		return
	}
	os.Exit(m.Run())
}

func fakeHelper(mode string) {
	in := bufio.NewScanner(os.Stdin)
	for in.Scan() {
		var req struct {
			ID int64  `json:"id"`
			Op string `json:"op"`
		}
		_ = json.Unmarshal(in.Bytes(), &req)
		ok := `{}`
		switch req.Op {
		case "status":
			ok = fmt.Sprintf(`{"ready":%v}`, mode == "ready")
		case "search":
			ok = `{"query":{},"library":[` +
				`{"key":"10","kind":"Movie","title":"Cast Away","year":2000},` +
				`{"key":"person:Tom Hanks","kind":"Person","title":"Tom Hanks"},` +
				`{"key":"12","kind":"Movie","title":"Cast Away","year":2000},` +
				`{"key":"11","kind":"Movie","title":"Big","year":1988}]}`
		}
		fmt.Printf(`{"id":%d,"ok":%s}`+"\n", req.ID, ok)
	}
}

// smartCatalog lets the caller see movies 10 and 12, a copy of 10 in another library, but not 11.
type smartFakeCatalog struct {
	fakeCatalog
	read chan struct{}
}

func (c smartFakeCatalog) SmartLibrary(context.Context) (SmartLibrary, error) {
	c.read <- struct{}{}
	return SmartLibrary{People: map[string]json.RawMessage{"Tom Hanks": json.RawMessage(`{"type":"person","id":5,"tag":"Tom Hanks"}`)}}, nil
}

func (smartFakeCatalog) SmartItems(context.Context, Identity, []string) (map[string]json.RawMessage, error) {
	return map[string]json.RawMessage{"10": json.RawMessage(`{"ratingKey":"10"}`), "12": json.RawMessage(`{"ratingKey":"12"}`)}, nil
}

func smartServer(t *testing.T, mode string) (*Server, smartFakeCatalog) {
	t.Setenv(fakeHelperEnv, mode)
	catalog := smartFakeCatalog{read: make(chan struct{}, 4)}
	host := Host{Name: "Plex", KV: store.NewMemKV(), Catalog: catalog, Identify: headerIdentity,
		SmartSearch: smartsearch.New(os.Args[0], t.TempDir())}
	return NewServer("1.2.3", host), catalog
}

func TestSmartSearchIsOffUntilTheAdminTurnsItOn(t *testing.T) {
	s, _ := smartServer(t, "ready")
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/SmartSearch", "", map[string]any{"query": "tom hanks island"}); code != http.StatusNotFound {
		t.Fatalf("off = %d, want 404", code)
	}
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["smartSearchEnabled"] != false {
		t.Fatalf("ping = %v", ping["smartSearchEnabled"])
	}
	saveConfig(t, s, map[string]any{"SmartSearchEnabled": true})
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["smartSearchEnabled"] != true {
		t.Fatalf("ping after turning it on = %v", ping["smartSearchEnabled"])
	}
}

func TestSmartSearchShowsWhatTheCallerCanSeeOnce(t *testing.T) {
	s, catalog := smartServer(t, "ready")
	saveConfig(t, s, map[string]any{"SmartSearchEnabled": true})
	s.rebuildSmartSearch(context.Background())
	<-catalog.read

	code, got := call(t, s, userMain, "GET", "/Moonfin/SmartSearch", "", map[string]any{"query": "tom hanks island"})
	if code != http.StatusOK || got["Ready"] != true {
		t.Fatalf("search = %d %v", code, got)
	}
	items, _ := json.Marshal(got["Items"])
	if want := `[{"ratingKey":"10"},{"id":5,"tag":"Tom Hanks","type":"person"}]`; string(items) != want {
		t.Fatalf("items = %s, want %s", items, want)
	}
	if seerr, _ := got["Seerr"].([]any); len(seerr) != 0 {
		t.Fatalf("seerr = %v", got["Seerr"])
	}
}

func TestSmartSearchBeforeTheFirstBuildStartsIt(t *testing.T) {
	s, catalog := smartServer(t, "not ready")
	saveConfig(t, s, map[string]any{"SmartSearchEnabled": true})
	code, got := call(t, s, userMain, "GET", "/Moonfin/SmartSearch", "", map[string]any{"query": "tom hanks island"})
	if code != http.StatusOK || got["Ready"] != false {
		t.Fatalf("search = %d %v", code, got)
	}
	select {
	case <-catalog.read:
	case <-time.After(5 * time.Second):
		t.Fatal("the search didn't start a build")
	}
}
