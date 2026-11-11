package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"slices"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

type similarFakeCatalog struct {
	fakeCatalog
	asked *[]any
}

func (c similarFakeCatalog) SimilarItems(_ context.Context, caller Identity, id string, limit int, exclude []string) ([]json.RawMessage, error) {
	*c.asked = []any{caller.UserID, id, limit, exclude}
	switch id {
	case "404":
		return nil, ErrItemNotFound
	case "500":
		return nil, errors.New("plex is down")
	case "20":
		return nil, nil
	}
	return []json.RawMessage{json.RawMessage(`{"ratingKey":"11"}`), json.RawMessage(`{"ratingKey":"12"}`)}, nil
}

func TestSimilarItemsServeTheHostsRanking(t *testing.T) {
	var asked []any
	s := NewServer("1.2.3", Host{Name: "Plex", KV: store.NewMemKV(), Catalog: similarFakeCatalog{asked: &asked}, Identify: headerIdentity})

	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["recommendationsSupported"] != true {
		t.Fatalf("ping = %v", ping["recommendationsSupported"])
	}
	code, got := call(t, s, userMain, "GET", "/Moonfin/Items/10/Similar", "", map[string]any{"limit": "500", "excludeItemIds": "12, 13,"})
	items, _ := json.Marshal(got["Items"])
	if code != http.StatusOK || string(items) != `[{"ratingKey":"11"},{"ratingKey":"12"}]` || got["TotalRecordCount"] != 2.0 {
		t.Fatalf("similar = %d %v", code, got)
	}
	if want := []any{3, "10", 200, []string{"12", "13"}}; asked[0] != want[0] || asked[1] != want[1] || asked[2] != want[2] || !slices.Equal(asked[3].([]string), want[3].([]string)) {
		t.Fatalf("the host was asked %v, want %v", asked, want)
	}
	call(t, s, userMain, "GET", "/Moonfin/Items/10/Similar", "", nil)
	if asked[2] != 20 {
		t.Fatalf("the default limit is %v", asked[2])
	}

	if code, got := call(t, s, userMain, "GET", "/Moonfin/Items/20/Similar", "", nil); code != http.StatusOK || got["Items"] == nil || got["TotalRecordCount"] != 0.0 {
		t.Fatalf("nothing similar = %d %v", code, got)
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Items/404/Similar", "", nil); code != http.StatusNotFound {
		t.Fatalf("missing item = %d", code)
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Items/500/Similar", "", nil); code != http.StatusBadGateway {
		t.Fatalf("failing host = %d", code)
	}
	if code, _ := call(t, s, anon, "GET", "/Moonfin/Items/10/Similar", "", nil); code != http.StatusUnauthorized {
		t.Fatalf("signed out = %d", code)
	}
}

func TestSimilarItemsTurnedOffAnswerEmpty(t *testing.T) {
	var asked []any
	s := NewServer("1.2.3", Host{Name: "Plex", KV: store.NewMemKV(), Catalog: similarFakeCatalog{asked: &asked}, Identify: headerIdentity})
	saveConfig(t, s, map[string]any{"RecommendationsProviderEnabled": false})

	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["recommendationsSupported"] != false {
		t.Fatalf("ping = %v", ping["recommendationsSupported"])
	}
	code, got := call(t, s, userMain, "GET", "/Moonfin/Items/10/Similar", "", nil)
	if items, _ := got["Items"].([]any); code != http.StatusOK || items == nil || len(items) != 0 || asked != nil {
		t.Fatalf("off = %d %v, host asked %v", code, got, asked)
	}
}

func TestSimilarItemsAreOffOnHostsWithoutThem(t *testing.T) {
	s := newTestServer()
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["recommendationsSupported"] != false {
		t.Fatalf("ping = %v", ping["recommendationsSupported"])
	}
	if code, _ := call(t, s, userMain, "GET", "/Moonfin/Items/10/Similar", "", nil); code != http.StatusNotFound {
		t.Fatalf("similar = %d", code)
	}
}
