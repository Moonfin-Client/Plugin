package store

import (
	"context"
	"encoding/json"
	"fmt"
	"math/rand"
	"strings"
	"testing"
)

func packAll(key string) bool { return strings.HasPrefix(key, "p/") }

// envelope is about the size of a Moonfin app's synced settings: a global
// profile with a few hundred settings, three device profiles and a home
// layout.
func envelope(r *rand.Rand, n int) []byte {
	layer := func(size int) map[string]any {
		m := map[string]any{}
		for i := 0; i < size; i++ {
			switch i % 4 {
			case 0:
				m[fmt.Sprintf("setting%d", i)] = r.Intn(2) == 0
			case 1:
				m[fmt.Sprintf("setting%d", i)] = r.Intn(1000)
			case 2:
				m[fmt.Sprintf("setting%d", i)] = []string{"poster", "backdrop", "logo"}[r.Intn(3)]
			default:
				m[fmt.Sprintf("setting%d", i)] = fmt.Sprintf("%x", r.Int63())
			}
		}
		return m
	}
	rows := []any{}
	for i := 0; i < 20; i++ {
		rows = append(rows, map[string]any{"type": fmt.Sprintf("row%d", i), "enabled": true, "order": i,
			"pluginSection": fmt.Sprintf("%x", r.Int63())})
	}
	global := layer(250)
	global["homeSections"] = rows
	b, _ := json.Marshal(map[string]any{"schemaVersion": 2, "profile": n, "global": global,
		"tv": layer(60), "mobile": layer(60), "desktop": layer(60)})
	return b
}

// More profiles than Silo has keys fit and read back, with room for four
// times as many.
func TestPackedHoldsThousandsOfProfiles(t *testing.T) {
	ctx := context.Background()
	mem := NewMemKV()
	p := &Packed{KV: mem, Pack: packAll}
	r := rand.New(rand.NewSource(1))
	const profiles = 400
	want := map[string][]byte{}
	for i := 0; i < profiles; i++ {
		key := fmt.Sprintf("p/%d/%x", i, r.Int63())
		want[key] = envelope(r, i)
		if err := p.Put(ctx, key, want[key]); err != nil {
			t.Fatalf("profile %d: %v", i, err)
		}
	}
	if len(mem.data) > PackedBuckets {
		t.Fatalf("%d keys used", len(mem.data))
	}
	for bucket, v := range mem.data {
		if len(v) > MaxValueBytes/4 {
			t.Fatalf("%s is %d bytes, more than a quarter full", bucket, len(v))
		}
	}
	for key, v := range want {
		got, found, err := p.Get(ctx, key)
		if err != nil || !found || !jsonEqual(got, v) {
			t.Fatalf("%s didn't read back: %v %v", key, found, err)
		}
	}
}

func jsonEqual(a, b []byte) bool {
	var x, y any
	_ = json.Unmarshal(a, &x)
	_ = json.Unmarshal(b, &y)
	return fmt.Sprint(x) == fmt.Sprint(y)
}

// A value from before packing is read from its own key until written, then
// lives in its bucket, and deleting it doesn't bring the old one back.
func TestPackedMovesOldValuesIntoBuckets(t *testing.T) {
	ctx := context.Background()
	mem := NewMemKV()
	_ = mem.Put(ctx, "p/1/a", []byte(`{"v":1}`))
	_ = mem.Put(ctx, "config", []byte(`{"c":1}`))
	p := &Packed{KV: mem, Pack: packAll}

	if got, found, _ := p.Get(ctx, "p/1/a"); !found || string(got) != `{"v":1}` {
		t.Fatalf("old value = %s %v", got, found)
	}
	if err := p.Put(ctx, "p/1/a", []byte(`{"v":2}`)); err != nil {
		t.Fatal(err)
	}
	if old, _, _ := mem.Get(ctx, "p/1/a"); len(old) != 0 {
		t.Fatalf("old key still holds %s", old)
	}
	if got, _, _ := p.Get(ctx, "p/1/a"); string(got) != `{"v":2}` {
		t.Fatalf("moved value = %s", got)
	}
	if err := p.Put(ctx, "p/1/a", nil); err != nil {
		t.Fatal(err)
	}
	if got, found, _ := p.Get(ctx, "p/1/a"); found && len(got) > 0 {
		t.Fatalf("deleted value came back: %s", got)
	}
	// Other keys keep their own.
	if got, _, _ := p.Get(ctx, "config"); string(got) != `{"c":1}` {
		t.Fatalf("config = %s", got)
	}
	if err := p.Put(ctx, "p/2/b", []byte("not json")); err == nil {
		t.Fatal("a packed value must be JSON")
	}
}

// Many values go in and come back out together, the same as one at a time,
// and a deleted value doesn't come back from its old unpacked key.
func TestPackedManyAtOnce(t *testing.T) {
	ctx := context.Background()
	mem := NewMemKV()
	_ = mem.Put(ctx, "p/old", []byte(`{"v":0}`))
	p := &Packed{KV: mem, Pack: packAll}
	r := rand.New(rand.NewSource(2))
	want := map[string][]byte{"config": []byte(`{"c":1}`), "p/old": nil}
	for i := 0; i < 400; i++ {
		want[fmt.Sprintf("p/%d", i)] = envelope(r, i)
	}
	if failed, err := PutMany(ctx, p, want); err != nil || len(failed) > 0 {
		t.Fatalf("PutMany: %v %v", failed, err)
	}
	keys := []string{"p/missing"}
	for key := range want {
		keys = append(keys, key)
	}
	got, err := GetMany(ctx, p, keys)
	if err != nil {
		t.Fatal(err)
	}
	for key, v := range want {
		if one, _, _ := p.Get(ctx, key); !jsonEqual(one, v) || !jsonEqual(got[key], v) {
			t.Fatalf("%s = %s / %s, want %s", key, one, got[key], v)
		}
	}
	if v, found := got["p/missing"]; found {
		t.Fatalf("a missing key read as %s", v)
	}
	if failed, err := PutMany(ctx, p, map[string][]byte{"p/1": []byte("not json"), "p/2": nil}); err == nil || len(failed) != 1 || failed[0] != "p/1" {
		t.Fatalf("only the bad value must fail: %v %v", failed, err)
	}
	if _, found, _ := p.Get(ctx, "p/2"); found {
		t.Fatal("the other write must still go through")
	}
}

// failOn refuses to write one key.
type failOn struct {
	KV
	key string
}

func (f failOn) Put(ctx context.Context, key string, value []byte) error {
	if key == f.key {
		return fmt.Errorf("refused %s", key)
	}
	return f.KV.Put(ctx, key, value)
}

// A value whose old unpacked key can't be cleared is still written: the
// error says so, and the key isn't listed as unwritten.
func TestPackedReportsOldKeyLeftBehind(t *testing.T) {
	ctx := context.Background()
	mem := NewMemKV()
	_ = mem.Put(ctx, "p/old", []byte(`{"v":0}`))
	p := &Packed{KV: failOn{KV: mem, key: "p/old"}, Pack: packAll}
	failed, err := PutMany(ctx, p, map[string][]byte{"p/old": []byte(`{"v":1}`), "p/new": []byte(`{"v":2}`)})
	if err == nil || len(failed) != 0 {
		t.Fatalf("want the cleanup error and nothing unwritten: %v %v", failed, err)
	}
	for key, want := range map[string]string{"p/old": `{"v":1}`, "p/new": `{"v":2}`} {
		if got, _, _ := p.Get(ctx, key); string(got) != want {
			t.Fatalf("%s = %s, want %s", key, got, want)
		}
	}
}
