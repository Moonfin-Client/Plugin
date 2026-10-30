package silo

import (
	"context"
	"encoding/json"
	"net/http"
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
