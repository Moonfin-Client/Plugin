package settings

import (
	"encoding/json"
	"reflect"
	"testing"
	"time"
)

var now = time.Date(2026, 10, 23, 12, 0, 0, 0, time.UTC)

// decode gives values the shapes encoding/json produces, as stored data has.
func decode(t *testing.T, s string) M {
	t.Helper()
	var m M
	if err := json.Unmarshal([]byte(s), &m); err != nil {
		t.Fatal(err)
	}
	return m
}

func TestLayerDefaults(t *testing.T) {
	all := decode(t, `{"a":1,"b":1,"homeSections":[1],"homeRowOrder":["x"]}`)
	if got := LayerDefaults(all, nil); !reflect.DeepEqual(got, all) {
		t.Fatalf("no device defaults must leave the shared ones: %v", got)
	}
	got := LayerDefaults(all, decode(t, `{"a":2,"homeSections":[2]}`))
	if want := decode(t, `{"a":2,"b":1,"homeSections":[2]}`); !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
	if all["a"] != 1.0 {
		t.Fatal("the shared defaults must not change")
	}
}

func TestCleanNormalizesKeysAndAliases(t *testing.T) {
	got := Clean(decode(t, `{"SeerrEnabled":true,"jellyseerrBlockNsfw":false,"jellyseerrEnabled":false,"x":null}`))
	want := M{"seerrEnabled": true, "seerrBlockNsfw": false}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
}

func TestResolvePrecedenceAndAliases(t *testing.T) {
	env := decode(t, `{"global":{"a":1,"b":1,"seerrEnabled":true},"tv":{"a":2}}`)
	defaults := decode(t, `{"a":3,"b":3,"c":3}`)
	got := Resolve(env, "tv", defaults, ServerKeys{})
	want := decode(t, `{"a":2,"b":1,"c":3,"seerrEnabled":true,"jellyseerrEnabled":true}`)
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
	if g := Resolve(env, "global", nil, ServerKeys{}); g["a"] != float64(1) {
		t.Fatalf("global must ignore device profiles, got %v", g["a"])
	}
}

func TestResolveHomeLayoutComesFromOneLayer(t *testing.T) {
	env := decode(t, `{
		"global":{"homeSections":[
			{"type":"latest","order":2},
			{"type":"resume","order":1},
			{"type":"none","order":0},
			{"type":"nextup","enabled":false,"order":3},
			{"kind":"pluginDynamic","type":"x","order":4}]},
		"tv":{"homeRowOrder":["only"]}}`)
	got := Resolve(env, "global", nil, ServerKeys{})
	if !reflect.DeepEqual(got["homeRowOrder"], []any{"resume", "latest"}) {
		t.Fatalf("derived order %v", got["homeRowOrder"])
	}
	tv := Resolve(env, "tv", nil, ServerKeys{})
	if tv["homeSections"] != nil || !reflect.DeepEqual(tv["homeRowOrder"], []any{"only"}) {
		t.Fatalf("tv layout must come whole from tv, got %v / %v", tv["homeSections"], tv["homeRowOrder"])
	}
}

func TestServerKeysFillOnResolveAndStripOnSave(t *testing.T) {
	keys := ServerKeys{Tmdb: "server-tmdb", Mdblist: "server-mdb"}
	got := Resolve(nil, "tv", decode(t, `{"mdblistRatingSources":["imdb","RTAudience"]}`), keys)
	if got["tmdbApiKey"] != "server-tmdb" || got["mdblistApiKey"] != "server-mdb" {
		t.Fatalf("keys not filled: %v", got)
	}
	if !reflect.DeepEqual(got["mdblistRatingSources"], []any{"imdb", "tomatoes_audience"}) {
		t.Fatalf("rtAudience not rewritten: %v", got["mdblistRatingSources"])
	}

	env := SaveProfile(nil, "tv", Clean(decode(t, `{"tmdbApiKey":"server-tmdb","mdblistApiKey":"mine"}`)), "c", keys, now)
	tv := env["tv"].(M)
	if _, ok := tv["tmdbApiKey"]; ok {
		t.Fatal("a copy of the server key must not be stored")
	}
	if tv["mdblistApiKey"] != "mine" {
		t.Fatal("the user's own key must be kept")
	}
}

func TestMergeKeepsOldValuesAndSkipsBlankKeys(t *testing.T) {
	env := SaveProfile(nil, "tv", M{"a": 1.0, "seerrApiKey": "k", "homeSections": []any{}}, "c", ServerKeys{}, now)
	env = SaveProfile(env, "tv", M{"b": 2.0, "seerrApiKey": "  ", "homeRowOrder": []any{"x"}}, "c2", ServerKeys{}, now)
	tv := env["tv"].(M)
	if tv["a"] != 1.0 || tv["b"] != 2.0 || tv["seerrApiKey"] != "k" {
		t.Fatalf("merge lost data: %v", tv)
	}
	if _, ok := tv["homeSections"]; ok {
		t.Fatal("a push with only homeRowOrder must drop the stored homeSections")
	}
	if env["lastUpdatedBy"] != "c2" || env["lastUpdated"] != now.UnixMilli() || env["schemaVersion"] != 2 {
		t.Fatalf("stamps wrong: %v", env)
	}
}

func TestHiddenContentMovesToGlobalAndUnhides(t *testing.T) {
	keys := ServerKeys{}
	env := SaveProfile(nil, "tv", M{"hiddenContinueWatchingItems": `{"a":"2026-01-01T00:00:00Z","b":"2026-01-01T00:00:00Z"}`}, "c", keys, now)
	if _, ok := env["tv"].(M)["hiddenContinueWatchingItems"]; ok {
		t.Fatal("device profiles must not keep hidden items")
	}
	if parseHidden(env["global"].(M)["hiddenContinueWatchingItems"].(string))["b"] == "" {
		t.Fatal("hidden items must land on global")
	}

	// The phone pulls, then unhides "b" and hides "c".
	resolved := Resolve(env, "mobile", nil, keys)
	RecordBaseline(env, "mobile", resolved)
	env = SaveProfile(env, "mobile", M{"hiddenContinueWatchingItems": `{"a":"2026-01-01T00:00:00Z","c":"2026-02-01T00:00:00Z"}`}, "c", keys, now)
	got := parseHidden(env["global"].(M)["hiddenContinueWatchingItems"].(string))
	if _, ok := got["b"]; ok || got["a"] == "" || got["c"] == "" {
		t.Fatalf("got %v, want a and c", got)
	}
}

func TestIsLaterHide(t *testing.T) {
	if !isLaterHide("2026-01-02T00:00:00.000", "2026-01-01T00:00:00Z") {
		t.Fatal("zone-less Dart timestamps must compare as times")
	}
	if isLaterHide("a", "b") {
		t.Fatal("unparseable values compare as strings")
	}
}

func TestSaveEnvelopeMergeAndReplace(t *testing.T) {
	env := SaveProfile(nil, "global", M{"a": 1.0}, "c", ServerKeys{}, now)
	merged := SaveEnvelope(env, decode(t, `{"tv":{"b":2},"hiddenContentBaselines":{"x":{}}}`), "merge", "c", ServerKeys{}, now)
	if merged["global"].(M)["a"] != 1.0 || merged["tv"].(M)["b"] != 2.0 {
		t.Fatalf("merge lost data: %v", merged)
	}
	if _, ok := merged["hiddenContentBaselines"]; ok {
		t.Fatal("clients must not write baselines")
	}
	replaced := SaveEnvelope(env, decode(t, `{"tv":{"b":2}}`), "replace", "c", ServerKeys{}, now)
	if len(replaced["global"].(M)) != 0 {
		t.Fatalf("replace must drop old profiles: %v", replaced["global"])
	}
}

func TestCleanLeavesNonASCIIKeysAlone(t *testing.T) {
	if got := Clean(M{"Éclair": true, "Title": 1.0}); got["Éclair"] != true || got["title"] != 1.0 {
		t.Fatalf("got %v", got)
	}
}
